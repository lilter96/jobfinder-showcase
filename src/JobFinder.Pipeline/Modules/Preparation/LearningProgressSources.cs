using System.Globalization;
using System.Data;
using System.Text.Json;
using JobFinder.Core.Domain;
using JobFinder.Core.Telemetry;
using JobFinder.Core.Text;
using JobFinder.Data;
using JobFinder.Pipeline.Actions;
using JobFinder.Pipeline.Career;
using JobFinder.Pipeline.Notifications;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Wolverine.EntityFrameworkCore;

namespace JobFinder.Pipeline.Modules.Preparation;

public sealed class LearningProgressSources(IDbContextFactory<JobFinderDbContext> db, IServiceScopeFactory scopes, Notifier notifier, TimeProvider time)
{
    internal static bool OwnsKey(string key) => key is "hr.progress" or "hr.approved" or "hr.learning"
        || key.StartsWith("interview.", StringComparison.Ordinal) && (key.EndsWith(".progress", StringComparison.Ordinal)
            || key.EndsWith(".approved", StringComparison.Ordinal) || key.EndsWith(".learning", StringComparison.Ordinal));

    internal static bool ValidScope(string? scope) => scope == InterviewProgress.HrScope
        || scope is not null && scope.StartsWith("interview.", StringComparison.Ordinal) && long.TryParse(scope[10..], NumberStyles.None, CultureInfo.InvariantCulture, out var id)
            && id > 0 && scope == InterviewProgress.Scope(id);
    private static bool ValidKey(string? key) => key is { Length: > 0 and <= 512 } && key.All(character => char.IsLetterOrDigit(character) || character is '_' or '-' or '.' or ':');

    public async Task<LearningProgressSnapshot> ReadAsync(string scope, CancellationToken ct)
    {
        if (!ValidScope(scope)) throw new ArgumentException("неверная область прогресса", nameof(scope));
        await using var context = await db.CreateDbContextAsync(ct);
        await using var transaction = await context.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, ct);
        var keys = Keys(scope);
        var snapshot = Read(scope, await context.Settings.AsNoTracking().Where(row => keys.Contains(row.Key)).ToListAsync(ct));
        await CheckHistoryAsync(context, snapshot, ct);
        return snapshot;
    }

    public Task<LearningPublication> MarkAsync(LearningItemSnapshot expected, string state, CancellationToken ct) =>
        PublishAsync(expected, state, null, null, ct);
    public Task<LearningPublication> ApproveAsync(LearningItemSnapshot expected, bool approved, CancellationToken ct) =>
        PublishAsync(expected, null, approved, null, ct);
    public Task<LearningPublication> ResetAsync(LearningProgressSnapshot expected, CancellationToken ct) =>
        PublishAsync(null, null, null, expected, ct);

    private async Task<LearningPublication> PublishAsync(LearningItemSnapshot? expected, string? state, bool? approved,
        LearningProgressSnapshot? reset, CancellationToken ct)
    {
        if (reset is not null && !ValidSnapshot(reset)) return Conflict();
        expected = expected is null ? null : expected with { };
        reset = reset is null ? null : Freeze(reset);
        await using var scope = scopes.CreateAsyncScope();
        var outbox = scope.ServiceProvider.GetRequiredService<IDbContextOutbox>();
        await using var context = await db.CreateDbContextAsync(ct);
        await using var transaction = await context.Database.BeginTransactionAsync(ct);
        outbox.Enroll(context);
        var result = await PublishJoinedAsync(context, expected, state, approved, reset, ct, notify: true);
        if (!result.Result.Ok) return result;
        await notifier.SaveAndFlushAsync(context, outbox, ct);
        return result;
    }

    internal async Task<LearningPublication> PublishJoinedAsync(JobFinderDbContext context, LearningItemSnapshot? expected,
        string? state, bool? approved, LearningProgressSnapshot? reset, CancellationToken ct, bool notify = false)
    {
        if (context.Database.CurrentTransaction is null) throw new InvalidOperationException("learning progress needs an enrolled source transaction");
        var scope = reset?.Scope ?? expected?.Scope;
        if (scope is null || !ValidScope(scope) || reset is not null && (!ValidSnapshot(reset)
            || expected is not null || state is not null || approved is not null)
            || reset is null && (expected is null || !ValidKey(expected.Key) || expected.Revision < 0
            || state is not null && state is not (InterviewProgress.Known or InterviewProgress.Again)
            || (state is null) == (approved is null))) return Conflict();
        expected = expected is null ? null : expected with { };
        reset = reset is null ? null : Freeze(reset);
        using var stage = Telemetry.StartStage("publish", "learning-progress").Tag("scope", scope);
        await context.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({"jobfinder.learning:" + scope},0))", ct);
        var keys = Keys(scope);
        foreach (var entry in context.ChangeTracker.Entries<AppSetting>().Where(entry => keys.Contains(entry.Entity.Key)).ToArray())
        {
            if (entry.State != EntityState.Unchanged) return Conflict();
            entry.State = EntityState.Detached;
        }
        var rows = await context.Settings.FromSqlInterpolated($"SELECT * FROM settings WHERE key = ANY({keys}) FOR UPDATE").ToListAsync(ct);
        var before = Read(scope, rows);
        await CheckHistoryAsync(context, before, ct);
        if (before.Revision == long.MaxValue || reset is not null && Json(before) != Json(reset)
            || reset is null && before.Item(expected!.Key) != expected) return Conflict();
        var after = Freeze(before);
        if (reset is not null)
        {
            if (after.Learned.Count == 0) return new(ActionResult.Done("прогресс уже сброшен"), after);
            foreach (var key in after.Learned.Keys)
            {
                if (after.ItemRevisions.GetValueOrDefault(key) == long.MaxValue) return Conflict();
                after.ItemRevisions[key] = after.ItemRevisions.GetValueOrDefault(key) + 1;
            }
            after.Learned.Clear();
        }
        else
        {
            var key = expected!.Key;
            if (state is not null && after.Learned.GetValueOrDefault(key) == state
                || approved is not null && after.Approved.Contains(key) == approved) return new(ActionResult.Done("отметка уже сохранена"), after);
            if (expected.Revision == long.MaxValue) return Conflict();
            if (state is not null) after.Learned[key] = state;
            else if (approved == true) after.Approved.Add(key);
            else after.Approved.Remove(key);
            after.ItemRevisions[key] = expected.Revision + 1;
        }
        after.Revision++;
        after.ProgressRaw = JsonSerializer.Serialize(after.Learned, LearningProgressJsonContext.Default.DictionaryStringString);
        after.ApprovedRaw = string.Join('\n', after.Approved.Order(StringComparer.Ordinal));
        after.MetadataRaw = JsonSerializer.Serialize(new LearningProgressMetadata
        {
            Version = 1, Revision = after.Revision, ItemRevisions = after.ItemRevisions,
            ProgressHash = CanonicalJson.Hash(after.ProgressRaw), ApprovedHash = ApprovedHash(after.Approved),
        }, LearningProgressJsonContext.Default.LearningProgressMetadata);
        var now = time.GetUtcNow();
        Set(context, rows, keys[0], after.ProgressRaw, now);
        Set(context, rows, keys[1], after.ApprovedRaw, now);
        Set(context, rows, keys[2], after.MetadataRaw, now);
        context.LearningProgressRevisions.Add(new() { Scope = scope, Revision = after.Revision, BeforeJson = Json(before), AfterJson = Json(after),
            Change = reset is not null ? "Reset" : state is not null ? "Mark" : "Approval", RecordedAt = now });
        if (notify) notifier.Add(context, NotificationKind.Info, reset is not null ? "Прогресс обучения сброшен: " + TextUtil.EscapeHtml(scope)
            : "Отметка обучения сохранена: " + TextUtil.EscapeHtml(scope + " · " + expected!.Key) + " — " + (state ?? (approved == true ? "проверено" : "проверка снята")));
        return new(ActionResult.Done("отметка сохранена"), after);
    }

    internal static LearningProgressSnapshot Read(string scope, IReadOnlyList<AppSetting> rows)
    {
        var keys = Keys(scope);
        var progress = rows.SingleOrDefault(row => row.Key == keys[0])?.Value;
        var approval = rows.SingleOrDefault(row => row.Key == keys[1])?.Value;
        var metadata = rows.SingleOrDefault(row => row.Key == keys[2])?.Value;
        try
        {
            var learned = metadata is null && string.IsNullOrWhiteSpace(progress) ? [] : JsonSerializer.Deserialize(progress ?? "{}", LearningProgressJsonContext.Default.DictionaryStringString)
                ?? throw new UnsupportedLearningProgressException();
            var approved = approval?.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToHashSet(StringComparer.Ordinal) ?? [];
            if (learned.Any(pair => !ValidKey(pair.Key) || pair.Value is not (InterviewProgress.Known or InterviewProgress.Again)) || approved.Any(key => !ValidKey(key)))
                throw new UnsupportedLearningProgressException();
            var meta = metadata is null ? null : JsonSerializer.Deserialize(metadata, LearningProgressJsonContext.Default.LearningProgressMetadata)
                ?? throw new UnsupportedLearningProgressException();
            if (metadata is not null && (meta is not { Version: 1, Revision: > 0, ItemRevisions: not null }
                    || meta.ItemRevisions.Any(pair => !ValidKey(pair.Key) || pair.Value < 0 || pair.Value > meta.Revision)
                    || meta.ProgressHash != CanonicalJson.Hash(JsonSerializer.Serialize(learned, LearningProgressJsonContext.Default.DictionaryStringString))
                    || meta.ApprovedHash != ApprovedHash(approved))) throw new UnsupportedLearningProgressException();
            return new() { Scope = scope, Revision = meta?.Revision ?? 0, Learned = learned, Approved = approved,
                ItemRevisions = meta?.ItemRevisions ?? [], ProgressRaw = progress, ApprovedRaw = approval, MetadataRaw = metadata };
        }
        catch (JsonException) { throw new UnsupportedLearningProgressException(); }
    }

    private static string ApprovedHash(HashSet<string> approved) => CanonicalJson.Hash(JsonSerializer.Serialize(approved.Order(StringComparer.Ordinal).ToList(), LearningProgressJsonContext.Default.ListString));
    private static async Task CheckHistoryAsync(JobFinderDbContext context, LearningProgressSnapshot snapshot, CancellationToken ct)
    {
        var last = await context.LearningProgressRevisions.AsNoTracking().Where(row => row.Scope == snapshot.Scope).OrderByDescending(row => row.Revision).FirstOrDefaultAsync(ct);
        if (snapshot.Revision == 0 ? last is not null : last is null || last.Revision != snapshot.Revision || CanonicalJson.Hash(last.AfterJson) != CanonicalJson.Hash(Json(snapshot)))
            throw new UnsupportedLearningProgressException();
    }
    private static string[] Keys(string scope) => [scope + ".progress", scope + ".approved", scope + ".learning"];
    private static bool ValidSnapshot(LearningProgressSnapshot snapshot) => snapshot.Revision >= 0
        && snapshot.Learned is not null && snapshot.Approved is not null && snapshot.ItemRevisions is not null;
    internal static string Json(LearningProgressSnapshot snapshot) => JsonSerializer.Serialize(snapshot with
        { Approved = snapshot.Approved.Order(StringComparer.Ordinal).ToHashSet(StringComparer.Ordinal) }, LearningProgressJsonContext.Default.LearningProgressSnapshot);
    internal static LearningProgressSnapshot Freeze(LearningProgressSnapshot snapshot) => JsonSerializer.Deserialize(Json(snapshot), LearningProgressJsonContext.Default.LearningProgressSnapshot)!;
    private static LearningPublication Conflict() => new(ActionResult.Fail("прогресс или исходная отметка изменились; запрос сохранён, сначала явно обновите состояние"));
    private static void Set(JobFinderDbContext context, List<AppSetting> rows, string key, string value, DateTimeOffset now)
    {
        var row = rows.SingleOrDefault(row => row.Key == key);
        if (row is null) { row = new() { Key = key, Value = value }; context.Settings.Add(row); rows.Add(row); }
        row.Value = value;
        row.UpdatedAt = now;
    }
}
