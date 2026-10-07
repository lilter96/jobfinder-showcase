using System.Buffers.Binary;
using System.Text.Json;
using JobFinder.Core.Options;
using JobFinder.Core.Profile;
using JobFinder.Core.Storage;
using JobFinder.Data;
using JobFinder.Integrations.Browser;
using JobFinder.Integrations.Telegram;
using JobFinder.Pipeline.Profile;
using JobFinder.Pipeline.Modules.Candidate;
using Microsoft.AspNetCore.DataProtection.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;

namespace JobFinder.Pipeline.Storage;

/// <param name="File">The file, relative to the JobFinder home.</param>
/// <param name="Target">Where it went: a document key, <c>telegram_sessions</c>, <c>data_protection_keys</c>.</param>
public sealed record ImportedFile(string File, string Target, string Outcome);

/// <summary>
/// The state that lived in files until 2026-09-29, taken into the database once: the candidate profile and facts, the browser logins, the
/// Telegram session and updates state, the dashboard's key ring. A file is imported only when the database has nothing under its name —
/// the database always wins — and is then renamed <c>*.imported</c> (a backup nothing reads). A file that does not open (not JSON, a session
/// another api_hash wrote) stays in place and is reported, and the next start tries again. Runs in the server after the single-instance
/// guard (before Telegram connects) and in <c>--migrate</c>.
/// </summary>
public sealed class FileStateImport(IDocumentStore documents, PgTelegramSessionStore sessions, IDbContextFactory<JobFinderDbContext> db,
    PathsOptions paths, TelegramOptions telegram, ILogger<FileStateImport> log, ICandidatePublisher candidate) : IHostedService
{
    public const string ImportedSuffix = ".imported";

    public async Task StartAsync(CancellationToken ct) => await ImportAsync(ct);

    public Task StopAsync(CancellationToken ct) => Task.CompletedTask;

    public async Task<List<ImportedFile>> ImportAsync(CancellationToken ct)
    {
        using var stage = Core.Telemetry.Telemetry.StartStage("import", "storage");
        var done = new List<ImportedFile>();
        // Typed documents are read as the old readers read them (comments, trailing commas) and stored as the new writers write them.
        await DocumentAsync(done, Path.Combine(paths.Profile, "profile.json"), ProfileProvider.Key,
            json => ProfileProvider.ToJson(ProfileProvider.FromJson(json)), ct);
        await DocumentAsync(done, Path.Combine(paths.Profile, "profile.json.bak"), ProfileProvider.PreviousKey,
            json => ProfileProvider.ToJson(ProfileProvider.FromJson(json)), ct);
        await DocumentAsync(done, Path.Combine(paths.Profile, "facts.json"), FactsProvider.Key, FactsProvider.Normalize, ct);
        // Library state (WTelegramClient, Playwright) is stored as the library wrote it, once it is known to be JSON.
        await DocumentAsync(done, Path.Combine(paths.Data, "telegram.updates.json"), TelegramUserClient.UpdatesKey, Verbatim, ct);
        var browser = Path.Combine(paths.Data, "browser");
        if (Directory.Exists(browser))
            foreach (var file in Directory.EnumerateFiles(browser, "*.json").Order(StringComparer.Ordinal))
                await DocumentAsync(done, file, BrowserSessions.StateKey(Path.GetFileNameWithoutExtension(file)), Verbatim, ct);
        await SessionAsync(done, Path.Combine(paths.Data, "telegram.session"), ct);
        await KeyRingAsync(done, Path.Combine(paths.Data, "keys"), ct);

        stage.Tag("files", done.Count).Tag("imported", done.Count(d => d.Outcome == "imported"));
        foreach (var d in done) log.Info($"[storage] {d.File} → {d.Target}: {d.Outcome}");
        return done;
    }

    private static string Verbatim(string json)
    {
        using var _ = JsonDocument.Parse(json);
        return json;
    }

    private async Task DocumentAsync(List<ImportedFile> done, string path, string key, Func<string, string> normalize, CancellationToken ct)
    {
        if (!File.Exists(path)) return;
        string json;
        try
        {
            json = normalize(await File.ReadAllTextAsync(path, ct));
        }
        catch (JsonException e)
        {
            done.Add(new(Relative(path), key, $"not imported, the file does not parse: {e.Message}"));
            log.Error($"[storage] {Relative(path)} does not parse, left in place: {e.Message}");
            return;
        }

        bool added;
        try
        {
            added = await AddDocumentAsync(key, json, ct);
        }
        catch (Exception error) when (error is JsonException or ArgumentException)
        {
            done.Add(new(Relative(path), key, "not imported: " + error.Message));
            return;
        }
        done.Add(new(Relative(path), key, added ? "imported" : "the database already had it, file set aside"));
        SetAside(path);
    }

    private async Task<bool> AddDocumentAsync(string key, string json, CancellationToken ct)
    {
        try
        {
            if (key == ProfileProvider.Key)
                await candidate.PublishProfileAsync(ProfileProvider.FromJson(json), 0, "file-import", ct);
            else if (key == FactsProvider.Key)
                await candidate.PublishFactsAsync(JsonSerializer.Deserialize(json, FactsJsonContext.Default.CandidateFacts)!, 0, "file-import", ct);
            else return await documents.AddAsync(key, json, ct);
            return true;
        }
        catch (CandidateConflictException) { return false; }
    }

    /// <summary>
    /// WTelegramClient's session file: an 8-byte header (offset and length of the current block, little-endian) and the block, which moves
    /// through the file between writes. The block is what the library's byte[] store takes; it is opened with the configured api_hash
    /// before it replaces the file (offline, nothing connects).
    /// </summary>
    private async Task SessionAsync(List<ImportedFile> done, string path, CancellationToken ct)
    {
        const string target = "telegram_sessions";
        if (!File.Exists(path)) return;
        if (string.IsNullOrWhiteSpace(telegram.ApiHash))
        {
            done.Add(new(Relative(path), target, "not imported: Telegram:ApiHash is not set, the session cannot be checked"));
            return;
        }

        var session = SessionBlock(await File.ReadAllBytesAsync(path, ct));
        try
        {
            TelegramUserClient.ValidateSession(telegram, session ?? throw new InvalidDataException("no session block in the file"));
        }
        catch (Exception e) when (e is WTelegram.WTException or InvalidDataException)
        {
            done.Add(new(Relative(path), target, $"not imported, the session does not open: {e.Message}"));
            log.Error($"[storage] {Relative(path)}: the session does not open with this api_hash, left in place: {e.Message}");
            return;
        }

        var added = await sessions.AddAsync(session, ct);
        done.Add(new(Relative(path), target, added ? "imported" : "the database already had it, file set aside"));
        SetAside(path);
    }

    /// <summary>The current block of a session file (null when the header does not describe one).</summary>
    public static byte[]? SessionBlock(byte[] file)
    {
        if (file.Length < 8) return null;
        var offset = BinaryPrimitives.ReadInt32LittleEndian(file);
        var length = BinaryPrimitives.ReadInt32LittleEndian(file.AsSpan(4));
        return offset >= 8 && length > 0 && (long)offset + length <= file.Length ? file[offset..(offset + length)] : null;
    }

    /// <summary>ASP.NET data-protection keys (XML files): into the key ring table when it is still empty.</summary>
    private async Task KeyRingAsync(List<ImportedFile> done, string dir, CancellationToken ct)
    {
        const string target = "data_protection_keys";
        if (!Directory.Exists(dir)) return;
        var files = Directory.EnumerateFiles(dir, "*.xml").Order(StringComparer.Ordinal).ToList();
        if (files.Count == 0) return;
        await using var ctx = await db.CreateDbContextAsync(ct);
        var empty = !await ctx.DataProtectionKeys.AnyAsync(ct);
        if (empty)
        {
            foreach (var file in files)
                ctx.DataProtectionKeys.Add(new DataProtectionKey { FriendlyName = Path.GetFileNameWithoutExtension(file), Xml = await File.ReadAllTextAsync(file, ct) });
            await ctx.SaveChangesAsync(ct);
        }

        foreach (var file in files)
        {
            done.Add(new(Relative(file), target, empty ? "imported" : "the database already had a key ring, file set aside"));
            SetAside(file);
        }
    }

    private void SetAside(string path)
    {
        var target = path + ImportedSuffix;
        if (File.Exists(target)) target += "-" + DateTime.UtcNow.ToString("yyyyMMddHHmmss", System.Globalization.CultureInfo.InvariantCulture);
        try
        {
            File.Move(path, target);
        }
        catch (IOException e)
        {
            // The database has it either way; the next start sets the file aside again.
            log.Warn($"[storage] {Relative(path)} imported but not renamed: {e.Message}");
        }
    }

    private static string Relative(string path) => Path.GetRelativePath(Directory.GetCurrentDirectory(), path).Replace('\\', '/');
}
