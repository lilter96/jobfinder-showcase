using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using JobFinder.Core.Domain;
using JobFinder.Core.Telemetry;
using JobFinder.Data;
using JobFinder.Integrations.Http;
using JobFinder.Pipeline.Notifications;
using Microsoft.EntityFrameworkCore;

namespace JobFinder.Pipeline.Settings;

public sealed class ScrapeStateStore(IDbContextFactory<JobFinderDbContext> db, TimeProvider time, ScrapeOptions options, Notifier notifier) : IScrapeRequestLedger
{
    internal const string StatePrefix = "scrape.state.";
    internal const string OwnerPrefix = "scrape.owner.";
    internal static string PauseKey(string family) => "scrape.pausedUntil." + family;
    internal static string BudgetKey(string family) => "scrape.budget." + family;
    public static bool OwnsKey(string key) => key.StartsWith(StatePrefix, StringComparison.Ordinal)
        || key.StartsWith(OwnerPrefix, StringComparison.Ordinal)
        || key.StartsWith("scrape.pausedUntil.", StringComparison.Ordinal) || key.StartsWith("scrape.budget.", StringComparison.Ordinal);

    public Task SaveAsync(string family, ScrapeState state, CancellationToken ct) =>
        throw new InvalidOperationException("scrape snapshots are read-only; reserve requests and publish block/recovery transitions through their owner");

    public async Task<ScrapeState> LoadAsync(string family, CancellationToken ct)
    {
        family = Family(family);
        await using var context = await db.CreateDbContextAsync(ct);
        await using var transaction = await context.Database.BeginTransactionAsync(ct);
        await LockAsync(context, family, ct);
        return (await ReadAsync(context, family, ct)).State;
    }

    public async Task<ScrapeAdmission> ReserveAsync(string family, ScrapePriority priority, CancellationToken ct)
    {
        family = Family(family);
        if (!Enum.IsDefined(priority)) throw new ArgumentOutOfRangeException(nameof(priority));
        using var stage = Telemetry.StartStage("reserve", "scrape-ledger").Tag("family", family).Tag("priority", priority.ToString());
        await using var context = await db.CreateDbContextAsync(ct);
        await using var transaction = await context.Database.BeginTransactionAsync(ct);
        await LockAsync(context, family, ct);
        var saved = await ReadAsync(context, family, ct);
        var now = time.GetUtcNow();
        if (saved.PausedUntil > now) return new(saved.State, saved.BlockRevision, saved.PausedUntil, Paused: true);
        var limits = options.Sites.GetValueOrDefault(family)?.Limits ?? [];
        if (priority == ScrapePriority.High || limits.Count == 0) return new(saved.State, saved.BlockRevision);
        if (limits.Any(limit => limit.Minutes is <= 0 or > 1440 || limit.Max <= 0)) throw new InvalidOperationException("invalid persisted scrape window policy");
        var share = options.Sites[family].BackgroundShare;
        if (!double.IsFinite(share) || share is <= 0 or > 1) throw new InvalidOperationException("invalid background scrape share");
        var recent = saved.Requests.Where(at => at > now.AddDays(-1)).ToList();
        var background = saved.BackgroundRequests.Where(at => at > now.AddDays(-1)).ToList();
        DateTimeOffset? retry = null;
        foreach (var limit in limits)
        {
            var inWindow = recent.Where(at => at > now.AddMinutes(-limit.Minutes)).Order().ToArray();
            var backgroundWindow = priority == ScrapePriority.Low ? background.Where(at => at > now.AddMinutes(-limit.Minutes)).Order().ToArray() : [];
            var backgroundMax = Math.Max(1, (int)Math.Floor(limit.Max * share));
            if (inWindow.Length >= limit.Max)
            {
                var until = inWindow[inWindow.Length - limit.Max].AddMinutes(limit.Minutes);
                if (retry is null || until > retry) retry = until;
            }
            if (backgroundWindow.Length >= backgroundMax)
            {
                var until = backgroundWindow[backgroundWindow.Length - backgroundMax].AddMinutes(limit.Minutes);
                if (retry is null || until > retry) retry = until;
            }
        }
        if (retry is not null) return new(saved.State, saved.BlockRevision, retry);
        saved.Requests = recent;
        saved.Requests.Add(now);
        saved.BackgroundRequests = background;
        if (priority == ScrapePriority.Low) saved.BackgroundRequests.Add(now);
        await PublishAsync(context, family, saved, ct);
        await context.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return new(saved.State, saved.BlockRevision);
    }

    public async Task<ScrapeState> BlockAsync(string family, CancellationToken ct)
    {
        family = Family(family);
        using var stage = Telemetry.StartStage("block", "scrape-ledger").Tag("family", family);
        await using var context = await db.CreateDbContextAsync(ct);
        await using var transaction = await context.Database.BeginTransactionAsync(ct);
        await LockAsync(context, family, ct);
        var saved = await ReadAsync(context, family, ct);
        if (saved.PausedUntil > time.GetUtcNow()) return saved.State;
        saved.BlockStreak = checked(saved.BlockStreak + 1);
        saved.BlockRevision = checked(saved.BlockRevision + 1);
        saved.PausedUntil = time.GetUtcNow() + ScrapeCooldown.After(saved.BlockStreak, options.BlockCooldownMinutes,
            options.Sites.GetValueOrDefault(family)?.MaxCooldownMinutes ?? options.BlockCooldownMinutes);
        await PublishAsync(context, family, saved, ct);
        notifier.Add(context, NotificationKind.Info, $"⏸ Сбор {Core.Text.TextUtil.EscapeHtml(family)} приостановлен после блокировки до {saved.PausedUntil:HH:mm} UTC; повторные чтения ждут конца паузы.");
        await notifier.SaveAndFlushAsync(context, ct);
        return saved.State;
    }

    public async Task<ScrapeState> RecoverAsync(string family, long blockRevision, CancellationToken ct)
    {
        family = Family(family);
        await using var context = await db.CreateDbContextAsync(ct);
        await using var transaction = await context.Database.BeginTransactionAsync(ct);
        await LockAsync(context, family, ct);
        var saved = await ReadAsync(context, family, ct);
        if (saved.BlockRevision != blockRevision || saved.PausedUntil > time.GetUtcNow()
            || saved.BlockStreak == 0 && saved.PausedUntil is null) return saved.State;
        saved.BlockStreak = 0;
        saved.PausedUntil = null;
        await PublishAsync(context, family, saved, ct);
        notifier.Add(context, NotificationKind.Info, $"▶️ Сбор {Core.Text.TextUtil.EscapeHtml(family)} возобновлён после успешного чтения.");
        await notifier.SaveAndFlushAsync(context, ct);
        return saved.State;
    }

    private static Task LockAsync(JobFinderDbContext context, string family, CancellationToken ct) =>
        context.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({StatePrefix + family}, 0))", ct);

    private static async Task<ScrapeLedgerDocument> ReadAsync(JobFinderDbContext context, string family, CancellationToken ct)
    {
        var keys = new[] { StatePrefix + family, PauseKey(family), BudgetKey(family), OwnerPrefix + family };
        var rows = await context.Settings.AsNoTracking().Where(row => keys.Contains(row.Key)).ToDictionaryAsync(row => row.Key, ct);
        if (!rows.TryGetValue(keys[0], out var root))
        {
            if (rows.ContainsKey(keys[3])) throw new InvalidOperationException("missing owned scrape ledger; restore the original state rather than reimporting");
            var legacy = new ScrapeLedgerDocument();
            if (rows.TryGetValue(keys[1], out var pause))
                legacy.PausedUntil = DateTimeOffset.TryParse(pause.Value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var until) && until != default
                    ? until : throw new InvalidOperationException("damaged legacy scrape pause");
            if (rows.TryGetValue(keys[2], out var budget))
            {
                var parts = budget.Value.Split('|');
                if (parts.Length != 2 || !int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var streak))
                    throw new InvalidOperationException("damaged legacy scrape budget");
                legacy.BlockStreak = streak;
                foreach (var value in parts[1].Split(',', StringSplitOptions.RemoveEmptyEntries))
                {
                    if (!long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var unix)) throw new InvalidOperationException("damaged legacy scrape request");
                    legacy.Requests.Add(DateTimeOffset.FromUnixTimeSeconds(unix));
                }
            }
            legacy.BackgroundRequests = legacy.Requests.ToList();
            return legacy;
        }
        var saved = JsonSerializer.Deserialize(root.Value, ScrapeLedgerJsonContext.Default.ScrapeLedgerDocument)
            ?? throw new InvalidOperationException("missing scrape ledger");
        if (saved.Version != 1 || saved.Revision <= 0 || saved.BlockRevision < 0 || saved.BlockStreak < 0 || saved.Requests is null || saved.BackgroundRequests is null
            || saved.Requests.Any(at => at == default) || saved.BackgroundRequests.Any(at => !saved.Requests.Contains(at)) || saved.PausedUntil == default(DateTimeOffset) || root.UpdatedAt == default
            || !rows.TryGetValue(keys[3], out var marker) || marker.Value != saved.Revision.ToString(CultureInfo.InvariantCulture) || marker.UpdatedAt != root.UpdatedAt
            || !rows.TryGetValue(keys[2], out var materialized) || materialized.Value != Budget(saved) || materialized.UpdatedAt != root.UpdatedAt
            || saved.PausedUntil is not null && (!rows.TryGetValue(keys[1], out var paused) || paused.Value != saved.PausedUntil.Value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture) || paused.UpdatedAt != root.UpdatedAt)
            || saved.PausedUntil is null && rows.ContainsKey(keys[1]))
            throw new InvalidOperationException("damaged scrape ledger or materialized budget/pause; reading refused");
        return saved;
    }

    private async Task PublishAsync(JobFinderDbContext context, string family, ScrapeLedgerDocument saved, CancellationToken ct)
    {
        saved.Revision = checked(saved.Revision + 1);
        var now = time.GetUtcNow();
        now = new DateTimeOffset(now.UtcTicks - now.UtcTicks % 10, TimeSpan.Zero);
        var values = new Dictionary<string, string?>
        {
            [StatePrefix + family] = JsonSerializer.Serialize(saved, ScrapeLedgerJsonContext.Default.ScrapeLedgerDocument),
            [OwnerPrefix + family] = saved.Revision.ToString(CultureInfo.InvariantCulture),
            [PauseKey(family)] = saved.PausedUntil?.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
            [BudgetKey(family)] = Budget(saved),
        };
        foreach (var (key, value) in values)
        {
            var row = await context.Settings.FindAsync([key], ct);
            if (value is null) { if (row is not null) context.Settings.Remove(row); }
            else if (row is null) context.Settings.Add(new() { Key = key, Value = value, UpdatedAt = now });
            else { row.Value = value; row.UpdatedAt = now; }
        }
    }

    private static string Budget(ScrapeLedgerDocument saved) => saved.BlockStreak.ToString(CultureInfo.InvariantCulture)
        + "|" + string.Join(',', saved.Requests.Select(at => at.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture)));

    private static string Family(string family)
    {
        if (string.IsNullOrWhiteSpace(family) || family != family.Trim() || family.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not '.' and not '-'))
            throw new ArgumentException("invalid scrape family", nameof(family));
        return PoliteHttp.Family(family.ToLowerInvariant());
    }
}

internal sealed class ScrapeLedgerDocument
{
    public int Version { get; set; } = 1;
    public long Revision { get; set; }
    public long BlockRevision { get; set; }
    public DateTimeOffset? PausedUntil { get; set; }
    public int BlockStreak { get; set; }
    public List<DateTimeOffset> Requests { get; set; } = [];
    public List<DateTimeOffset> BackgroundRequests { get; set; } = [];
    [JsonIgnore] public ScrapeState State => new(PausedUntil, BlockStreak, Requests.ToArray());
}

[JsonSerializable(typeof(ScrapeLedgerDocument))]
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
internal sealed partial class ScrapeLedgerJsonContext : JsonSerializerContext;
