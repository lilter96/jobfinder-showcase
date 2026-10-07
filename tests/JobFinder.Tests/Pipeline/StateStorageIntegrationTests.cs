using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using JobFinder.Core.Options;
using JobFinder.Core.Profile;
using JobFinder.Core.Storage;
using JobFinder.Integrations.Browser;
using JobFinder.Integrations.Telegram;
using JobFinder.Pipeline.Profile;
using JobFinder.Pipeline.Modules.Candidate;
using JobFinder.Pipeline.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace JobFinder.Tests.Pipeline;

/// <summary>
/// The state that used to be files, on a real Postgres: documents (profile, facts, browser logins, Telegram updates), the MTProto session,
/// the dashboard's key ring, and the one-time import of the old files.
/// </summary>
public sealed class StateStorageIntegrationTests(PostgresFixture pg) : IClassFixture<PostgresFixture>, IDisposable
{
    private const string ApiHash = "0123456789abcdef0123456789abcdef";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly string _home = Path.Combine(Path.GetTempPath(), "jf-state-" + Guid.NewGuid().ToString("N"));
    private CandidateDocuments Candidate { get; set; } = null!;

    public void Dispose()
    {
        if (Directory.Exists(_home)) Directory.Delete(_home, true);
    }

    private async Task<PgDocumentStore> StoreAsync()
    {
        Assert.SkipWhen(pg.SkipReason is not null, pg.SkipReason ?? "");
        Candidate = await pg.CandidateAsync();
        await using var ctx = await pg.Db.CreateDbContextAsync(Ct);
        await ctx.Database.ExecuteSqlRawAsync("DELETE FROM app_documents; DELETE FROM candidate_revisions; DELETE FROM telegram_sessions; DELETE FROM data_protection_keys;", Ct);
        return new PgDocumentStore(pg.Db, TimeProvider.System);
    }

    private static CandidateProfile Profile(string name) => new() { Name = name, Title = "Senior .NET Developer", Skills = [new() { Name = "C#" }] };

    // ---- documents ----

    [Fact]
    public async Task A_document_keeps_its_text_as_written_and_every_write_bumps_its_version()
    {
        var store = await StoreAsync();
        // Field order, Cyrillic and a 64-bit access hash exactly as written (json, not jsonb: nothing is re-encoded).
        const string first = """{"z": 1, "имя": "Терентий", "access_hash": -9041529019291537800}""";

        Assert.Equal(1, await store.SaveAsync("t.doc", first, Ct));
        Assert.Equal(2, await store.SaveAsync("t.doc", """{"a": 2}""", Ct));
        Assert.Equal(3, await store.SaveAsync("t.doc", first, Ct));

        var doc = await store.GetAsync("t.doc", Ct);
        Assert.Equal((first, 3L), (doc!.Json, doc.Version));
        Assert.Equal(3, await store.VersionAsync("t.doc", Ct));
        Assert.Null(await store.VersionAsync("t.none", Ct));
        Assert.True(await store.DeleteAsync("t.doc", Ct));
        Assert.Null(await store.GetAsync("t.doc", Ct));
    }

    [Fact]
    public async Task Writers_at_once_never_lose_a_version()
    {
        var store = await StoreAsync();

        var versions = await Task.WhenAll(Enumerable.Range(0, 24).Select(i => store.SaveAsync("t.race", $$"""{"i": {{i}}}""", Ct)));

        Assert.Equal(Enumerable.Range(1, 24).Select(i => (long)i), versions.Order());
    }

    [Fact]
    public async Task An_import_never_overwrites_what_the_database_holds()
    {
        var store = await StoreAsync();

        Assert.True(await store.AddAsync("t.add", """{"from": "file"}""", Ct));
        Assert.False(await store.AddAsync("t.add", """{"from": "another file"}""", Ct));

        Assert.Equal(("""{"from": "file"}""", 1L), ((await store.GetAsync("t.add", Ct))!.Json, (await store.GetAsync("t.add", Ct))!.Version));
    }

    [Fact]
    public async Task A_browser_state_with_a_NUL_in_local_storage_is_stored()
    {
        // jsonb refuses \u0000; a site's local storage may hold it, and a refused save would lose the refreshed login.
        var store = await StoreAsync();
        const string state = """{"cookies":[],"origins":[{"origin":"https://rabota.by","localStorage":[{"name":"k","value":"a\u0000b"}]}]}""";

        await store.SaveAsync(BrowserSessions.StateKey("hh"), state, Ct);

        Assert.Equal(state, (await store.GetAsync("browser.hh", Ct))!.Json);
    }

    [Fact]
    public async Task A_profile_saved_by_another_process_is_read_on_the_next_access_and_an_unchanged_one_is_not_parsed_again()
    {
        await StoreAsync();
        // The server and a CLI command: two providers over two stores, one database.
        var server = new ProfileProvider(new PgDocumentStore(pg.Db, TimeProvider.System), new PathsOptions(), NullLogger<ProfileProvider>.Instance, Candidate);
        var cli = new ProfileProvider(new PgDocumentStore(pg.Db, TimeProvider.System), new PathsOptions(), NullLogger<ProfileProvider>.Instance, Candidate);
        Assert.Null(await server.GetAsync(Ct));

        await cli.SaveAsync(Profile("Первый"), 0, "test", Ct);
        var first = await server.GetAsync(Ct);
        Assert.Equal("Первый", first!.Name);
        Assert.Same(first, await server.GetAsync(Ct));

        await cli.SaveAsync(Profile("Второй") with { Reviewed = true }, 1, "test", Ct);
        var second = await server.GetAsync(Ct);
        Assert.Equal(("Второй", true), (second!.Name, second.Reviewed));
    }

    [Fact]
    public async Task A_version_that_does_not_parse_keeps_the_last_good_profile()
    {
        await StoreAsync();
        var profiles = new ProfileProvider(new PgDocumentStore(pg.Db, TimeProvider.System), new PathsOptions(), NullLogger<ProfileProvider>.Instance, Candidate);
        await profiles.SaveAsync(Profile("Хороший"), 0, "test", Ct);
        Assert.Equal("Хороший", (await profiles.GetAsync(Ct))!.Name);

        // Valid JSON, not a profile: an edit by hand in psql.
        await using (var ctx = await pg.Db.CreateDbContextAsync(Ct))
            await ctx.Database.ExecuteSqlRawAsync("""UPDATE app_documents SET content = '{{"skills": 5}}', version = version + 1 WHERE key = 'candidate.profile'""", Ct);

        Assert.Equal("Хороший", (await profiles.GetAsync(Ct))!.Name);
        Assert.Equal("Хороший", (await profiles.GetAsync(Ct))!.Name);
    }

    [Fact]
    public async Task Facts_are_drafted_once_from_the_profile_and_a_confirmed_answer_reaches_every_reader()
    {
        var store = await StoreAsync();
        var profiles = new ProfileProvider(store, new PathsOptions(), NullLogger<ProfileProvider>.Instance, Candidate);
        await profiles.SaveAsync(Profile("Кандидат") with { DesiredSalary = new() { Amount = 4000, Currency = "USD", Net = true } }, 0, "test", Ct);
        var facts = new FactsProvider(store, NullLogger<FactsProvider>.Instance, Candidate);

        var drafted = await facts.GetAsync(Ct);
        Assert.Contains("4000", drafted.Get("salary")!.Answer);
        Assert.False(drafted.IsConfirmed("salary"));
        Assert.Equal(1, await store.VersionAsync(FactsProvider.Key, Ct));
        await facts.GetAsync(Ct);
        Assert.Equal(1, await store.VersionAsync(FactsProvider.Key, Ct));

        await facts.UpsertAsync("salary", "Зарплатные ожидания", "от 4500 USD на руки", Ct);

        var elsewhere = new FactsProvider(new PgDocumentStore(pg.Db, TimeProvider.System), NullLogger<FactsProvider>.Instance, Candidate);
        var read = await elsewhere.GetAsync(Ct);
        Assert.True(read.IsConfirmed("salary"));
        Assert.Equal(("от 4500 USD на руки", "вы"), (read.Get("salary")!.Answer, read.Get("salary")!.Source));
    }

    [Fact]
    public async Task Authoritative_profile_snapshots_do_not_share_objects_with_cached_readers_or_the_saving_caller()
    {
        var store = await StoreAsync();
        var profiles = new ProfileProvider(store, new PathsOptions(), NullLogger<ProfileProvider>.Instance, Candidate);
        var supplied = Profile("Сохранённый");
        await profiles.SaveAsync(supplied, 0, "test", Ct);
        supplied.Name = "Изменён без сохранения";
        supplied.Skills[0].Name = "Unsupported skill";
        Assert.Equal("Сохранённый", (await profiles.GetAsync(Ct))!.Name);
        var cached = (await profiles.GetAsync(Ct))!;
        cached.Name = "Неподтверждённая правка читателя";
        cached.Skills[0].Name = "Another unsupported skill";
        var snapshot = (await profiles.GetSnapshotAsync(Ct))!;
        Assert.Equal(1, snapshot.Version);
        Assert.Equal("Сохранённый", snapshot.Value.Name);
        Assert.Equal("C#", snapshot.Value.Skills[0].Name);
        snapshot.Value.Skills.Clear();
        Assert.Single((await profiles.GetSnapshotAsync(Ct))!.Value.Skills);
        Assert.Equal(1, await store.VersionAsync(ProfileProvider.Key, Ct));
    }

    [Fact]
    public async Task Confirmed_fact_updates_do_not_mutate_an_inflight_snapshot_or_accept_unsaved_cached_edits()
    {
        var store = await StoreAsync();
        var profiles = new ProfileProvider(store, new PathsOptions(), NullLogger<ProfileProvider>.Instance, Candidate);
        await profiles.SaveAsync(Profile("Кандидат"), 0, "test", Ct);
        var facts = new FactsProvider(store, NullLogger<FactsProvider>.Instance, Candidate);
        await facts.UpsertAsync("salary", "Ожидания", "4000 USD", Ct);
        var snapshot = await facts.GetSnapshotAsync(Ct);
        var cached = await facts.GetAsync(Ct);
        cached.Get("salary")!.Answer = "Несохранённое значение";
        await facts.UpsertAsync("notice", "Выход", "Через две недели", Ct);
        Assert.Equal("4000 USD", snapshot.Value.Get("salary")!.Answer);
        var updated = await facts.GetSnapshotAsync(Ct);
        Assert.Equal(snapshot.Version + 1, updated.Version);
        Assert.Equal("4000 USD", updated.Value.Get("salary")!.Answer);
        Assert.Equal("Через две недели", updated.Value.Get("notice")!.Answer);
        snapshot.Value.Get("salary")!.Answer = "Ещё одна несохранённая правка";
        Assert.Equal("4000 USD", (await facts.GetSnapshotAsync(Ct)).Value.Get("salary")!.Answer);
    }

    [Fact]
    public async Task Employer_facing_snapshots_fail_closed_on_a_corrupt_current_candidate_document()
    {
        var store = await StoreAsync();
        var profiles = new ProfileProvider(store, new PathsOptions(), NullLogger<ProfileProvider>.Instance, Candidate);
        await profiles.SaveAsync(Profile("Кандидат"), 0, "test", Ct);
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        await context.Database.ExecuteSqlInterpolatedAsync($"UPDATE app_documents SET content = {"{\"skills\":5}"}::json, version = version + 1 WHERE key = {ProfileProvider.Key}", Ct);
        await Assert.ThrowsAsync<JsonException>(() => profiles.GetSnapshotAsync(Ct));
        await context.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO app_documents (key, content, version, updated_at) VALUES ({FactsProvider.Key}, {"{\"facts\":5}"}::json, 1, now())", Ct);
        var facts = new FactsProvider(store, NullLogger<FactsProvider>.Instance, Candidate);
        await Assert.ThrowsAsync<JsonException>(() => facts.GetSnapshotAsync(Ct));
    }

    // ---- the Telegram session ----

    [Fact]
    public async Task The_session_store_keeps_the_latest_session_and_the_import_never_replaces_it()
    {
        await StoreAsync();
        var sessions = new PgTelegramSessionStore(pg.Db, TimeProvider.System);
        Assert.Null(await sessions.LoadAsync(Ct));

        Assert.True(await sessions.AddAsync([1, 2, 3], Ct));
        await sessions.SaveAsync([4, 5], Ct);
        Assert.False(await sessions.AddAsync([9], Ct));

        Assert.Equal([4, 5], await sessions.LoadAsync(Ct));
    }

    [Fact]
    public void A_session_opens_only_with_the_api_hash_that_wrote_it()
    {
        var block = SessionBlockFor(ApiHash);

        TelegramUserClient.ValidateSession(new TelegramOptions { ApiId = 1, ApiHash = ApiHash }, block);
        Assert.Throws<WTelegram.WTException>(() =>
            TelegramUserClient.ValidateSession(new TelegramOptions { ApiId = 1, ApiHash = "ffffffffffffffffffffffffffffffff" }, block));
    }

    [Fact]
    public void The_block_is_read_where_the_header_says_it_moved()
    {
        // As on disk on 2026-09-29: the block at 8584, not right after the header (WTelegramClient moves it between writes).
        var block = SessionBlockFor(ApiHash);
        var file = LegacySessionFile(block, offset: 8584);

        Assert.Equal(block, FileStateImport.SessionBlock(file));
        Assert.Null(FileStateImport.SessionBlock([1, 2, 3]));
        Assert.Null(FileStateImport.SessionBlock(LegacySessionFile(block, offset: 8)[..20])); // truncated
    }

    [Fact]
    public void The_old_updates_state_reads_as_the_client_reads_it()
    {
        const string legacy = """{ "2187734666": { "pts": 2875, "access_hash": 4931766002169529384 }, "2263550175": { "pts": 853 } }""";

        var state = JsonSerializer.Deserialize<Dictionary<long, WTelegram.UpdateManager.MBoxState>>(legacy, WTelegram.Helpers.JsonOptions)!;

        Assert.Equal((2875, 4931766002169529384L, 853), (state[2187734666].pts, state[2187734666].access_hash, state[2263550175].pts));
    }

    // ---- the import of the old files ----

    private FileStateImport Import(PgDocumentStore store, string apiHash = ApiHash) => new(store, new PgTelegramSessionStore(pg.Db, TimeProvider.System), pg.Db,
        new PathsOptions { Data = Path.Combine(_home, "data"), Profile = Path.Combine(_home, "profile") }, new TelegramOptions { ApiId = 1, ApiHash = apiHash },
        NullLogger<FileStateImport>.Instance, Candidate);

    private string Write(string relative, string text)
    {
        var path = Path.Combine(_home, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
        return path;
    }

    [Fact]
    public async Task Every_old_file_goes_into_the_database_once_and_is_set_aside()
    {
        var store = await StoreAsync();
        // The profile as it was edited by hand: a comment and a trailing comma, which the old reader accepted and json does not.
        var profile = Write("profile/profile.json", """
            {
              // edited in the IDE
              "name": "Терентий", "title": "Senior .NET Developer", "reviewed": true,
              "skills": [{ "name": "C#" }],
            }
            """);
        var facts = Write("profile/facts.json", """{"facts":[{"key":"salary","question":"Зарплата","answer":"от 4000 USD","confirmed":true,"source":"вы"}]}""");
        const string updatesJson = """{ "2187734666": { "pts": 2875, "access_hash": 4931766002169529384 } }""";
        var updates = Write("data/telegram.updates.json", updatesJson);
        const string hhJson = """{"cookies":[{"name":"hhtoken","value":"x","domain":".rabota.by","path":"/","expires":1790000000.5}],"origins":[]}""";
        var hh = Write("data/browser/hh.json", hhJson);
        var key = Write("data/keys/key-4b47.xml", """<?xml version="1.0" encoding="utf-8"?><key id="4b47" version="1" />""");
        var block = SessionBlockFor(ApiHash);
        var session = Path.Combine(_home, "data", "telegram.session");
        await File.WriteAllBytesAsync(session, LegacySessionFile(block, offset: 64), Ct);

        var done = await Import(store).ImportAsync(Ct);

        Assert.Equal(6, done.Count);
        Assert.All(done, d => Assert.Equal("imported", d.Outcome));
        var imported = (await store.GetAsync(ProfileProvider.Key, Ct))!.Json;
        Assert.Equal(("Терентий", true), (ProfileProvider.FromJson(imported).Name, ProfileProvider.FromJson(imported).Reviewed));
        Assert.Contains("Терентий", imported); // readable, not \u escapes
        var sheet = JsonSerializer.Deserialize((await store.GetAsync(FactsProvider.Key, Ct))!.Json, FactsJsonContext.Default.CandidateFacts)!;
        Assert.True(sheet.IsConfirmed("salary"));
        Assert.Equal(updatesJson, (await store.GetAsync(TelegramUserClient.UpdatesKey, Ct))!.Json);
        Assert.Equal(hhJson, (await store.GetAsync("browser.hh", Ct))!.Json);
        Assert.Equal(block, await new PgTelegramSessionStore(pg.Db, TimeProvider.System).LoadAsync(Ct));
        await using (var ctx = await pg.Db.CreateDbContextAsync(Ct))
        {
            var ring = Assert.Single(await ctx.DataProtectionKeys.ToListAsync(Ct));
            Assert.Equal(("key-4b47", true), (ring.FriendlyName, ring.Xml!.Contains("id=\"4b47\"", StringComparison.Ordinal)));
        }

        foreach (var file in new[] { profile, facts, updates, hh, key, session })
        {
            Assert.False(File.Exists(file), file);
            Assert.True(File.Exists(file + FileStateImport.ImportedSuffix), file);
        }

        Assert.Empty(await Import(store).ImportAsync(Ct));
    }

    [Fact]
    public async Task The_database_wins_over_a_file_that_shows_up_again()
    {
        var store = await StoreAsync();
        await new ProfileProvider(store, new PathsOptions(), NullLogger<ProfileProvider>.Instance, Candidate).SaveAsync(Profile("Из базы"), 0, "test", Ct);
        var file = Write("profile/profile.json", """{"name": "Из файла"}""");
        Write("profile/profile.json.imported", "{}"); // the backup of an earlier import is not overwritten

        var done = Assert.Single(await Import(store).ImportAsync(Ct));

        Assert.StartsWith("the database already had it", done.Outcome);
        Assert.Equal("Из базы", ProfileProvider.FromJson((await store.GetAsync(ProfileProvider.Key, Ct))!.Json).Name);
        Assert.False(File.Exists(file));
        Assert.Equal("{}", await File.ReadAllTextAsync(file + FileStateImport.ImportedSuffix, Ct));
        Assert.Single(Directory.GetFiles(Path.GetDirectoryName(file)!, "profile.json.imported-*"));
    }

    [Fact]
    public async Task A_file_that_does_not_open_stays_in_place_for_the_next_start()
    {
        var store = await StoreAsync();
        var profile = Write("profile/profile.json", "{ \"name\": ");
        var session = Path.Combine(_home, "data", "telegram.session");
        Directory.CreateDirectory(Path.GetDirectoryName(session)!);
        await File.WriteAllBytesAsync(session, LegacySessionFile(SessionBlockFor(ApiHash), offset: 8), Ct);

        // Another account's api_hash: the session would be useless, and the file may still be the only copy.
        var done = await Import(store, apiHash: "ffffffffffffffffffffffffffffffff").ImportAsync(Ct);

        Assert.All(done, d => Assert.StartsWith("not imported", d.Outcome));
        Assert.True(File.Exists(profile));
        Assert.True(File.Exists(session));
        Assert.Null(await store.GetAsync(ProfileProvider.Key, Ct));
        Assert.Null(await new PgTelegramSessionStore(pg.Db, TimeProvider.System).LoadAsync(Ct));
    }

    /// <summary>
    /// A session block as WTelegramClient writes it: a random IV, then AES-CBC (key = the api_hash bytes) of SHA-256(json) followed by the
    /// JSON of an empty session.
    /// </summary>
    private static byte[] SessionBlockFor(string apiHash)
    {
        var json = Encoding.UTF8.GetBytes("""{"ApiId":1,"UserId":0,"MainDC":0,"DCSessions":{}}""");
        using var aes = Aes.Create();
        aes.Key = Convert.FromHexString(apiHash);
        var iv = RandomNumberGenerator.GetBytes(16);
        byte[] plain = [.. SHA256.HashData(json), .. json];
        return [.. iv, .. aes.EncryptCbc(plain, iv, PaddingMode.PKCS7)];
    }

    /// <summary>WTelegramClient's session file: an 8-byte header (offset, length), stale bytes, the current block.</summary>
    private static byte[] LegacySessionFile(byte[] block, int offset)
    {
        var file = new byte[offset + block.Length];
        RandomNumberGenerator.Fill(file.AsSpan(8, offset - 8));
        BinaryPrimitives.WriteInt32LittleEndian(file, offset);
        BinaryPrimitives.WriteInt32LittleEndian(file.AsSpan(4), block.Length);
        block.CopyTo(file, offset);
        return file;
    }
}
