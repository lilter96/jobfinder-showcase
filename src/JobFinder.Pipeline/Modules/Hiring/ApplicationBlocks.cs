using System.Globalization;
using System.Text.Json.Serialization;
using JobFinder.Core.Domain;
using JobFinder.Data;
using JobFinder.Pipeline.Actions;
using JobFinder.Pipeline.Messaging;
using JobFinder.Pipeline.Notifications;
using JobFinder.Pipeline.Settings;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Wolverine.EntityFrameworkCore;

namespace JobFinder.Pipeline.Modules.Hiring;

public sealed record ApplicationBlockSnapshot
{
    public int Version { get; set; } = 1;
    public long Revision { get; set; }
    public string? Value { get; set; }
    public DateTimeOffset? UpdatedAt { get; set; }
    [JsonIgnore] public DateTimeOffset? Until => ApplicationBlocks.Parse(Value);
}

public sealed class ApplicationBlockConflictException() : Exception("application block changed; retain the original control until explicit reload");

public sealed class ApplicationBlocks(IDbContextFactory<JobFinderDbContext> db, NativePublicationScopes scopes, Notifier notifier, TimeProvider time)
{
    internal const string RevisionKey = "apply.blockedUntil.revision";
    public static bool OwnsKey(string key) => key is RuntimeSettings.ApplyBlockedUntil or RevisionKey;
    public Task<ApplicationBlockSnapshot> ReadAsync(CancellationToken ct) => ReadAsync(db, ct);

    internal static async Task<ApplicationBlockSnapshot> ReadAsync(IDbContextFactory<JobFinderDbContext> db, CancellationToken ct)
    {
        await using var context = await db.CreateDbContextAsync(ct);
        await using var transaction = await context.Database.BeginTransactionAsync(ct);
        await LockAsync(context, ct);
        return await ReadJoinedAsync(context, ct);
    }

    internal static async Task LockAsync(JobFinderDbContext context, CancellationToken ct)
    {
        if (context.Database.CurrentTransaction is null) throw new InvalidOperationException("application block requires its source transaction");
        await context.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({RuntimeSettings.ApplyBlockedUntil}, 0))", ct);
    }

    internal static async Task<ApplicationBlockSnapshot> ReadJoinedAsync(JobFinderDbContext context, CancellationToken ct)
    {
        var row = await context.Settings.AsNoTracking().SingleOrDefaultAsync(row => row.Key == RuntimeSettings.ApplyBlockedUntil, ct);
        var marker = await context.Settings.AsNoTracking().SingleOrDefaultAsync(row => row.Key == RevisionKey, ct);
        var last = await context.ApplicationBlockRevisions.AsNoTracking().OrderByDescending(row => row.Revision).FirstOrDefaultAsync(ct);
        Parse(row?.Value);
        if (marker is null && last is null) return new() { Value = row?.Value, UpdatedAt = row?.UpdatedAt };
        if (marker is null || last is null || !long.TryParse(marker.Value, NumberStyles.None, CultureInfo.InvariantCulture, out var revision)
            || revision <= 0 || last.Revision != revision || last.PublishedAt == default || marker.UpdatedAt != last.PublishedAt
            || last.Value != row?.Value || last.Value is null && row is not null || row is not null && row.UpdatedAt != last.PublishedAt)
            throw new InvalidOperationException("damaged application block revision or preimage; automatic release refused");
        Parse(last.Value);
        return new() { Revision = last.Revision, Value = last.Value, UpdatedAt = last.PublishedAt };
    }

    internal static async Task EnsureAsync(JobFinderDbContext context, ApplicationBlockSnapshot expected, CancellationToken ct)
    {
        if (expected.Version != 1 || expected.Revision < 0 || expected.Value is not null && expected.UpdatedAt is null)
            throw new ApplicationBlockConflictException();
        await LockAsync(context, ct);
        var current = await ReadJoinedAsync(context, ct);
        if (current.Revision != expected.Revision || current.Value != expected.Value || current.UpdatedAt != expected.UpdatedAt)
            throw new ApplicationBlockConflictException();
    }

    public async Task<ActionResult> ReleaseAsync(ApplicationBlockSnapshot expected, string actor, CancellationToken ct)
    {
        await using var context = await db.CreateDbContextAsync(ct);
        await using var transaction = await context.Database.BeginTransactionAsync(ct);
        await EnsureAsync(context, expected, ct);
        if (expected.Value is null) return ActionResult.Done("Пауза после капчи уже снята");
        await using var scope = scopes.CreateAsyncScope();
        var outbox = scope.ServiceProvider.GetRequiredService<IDbContextOutbox>();
        outbox.Enroll(context);
        await ReleaseJoinedAsync(context, outbox, expected, actor, ct);
        await notifier.SaveAndFlushAsync(context, outbox, ct);
        return ActionResult.Done("Пауза после капчи снята");
    }

    internal async Task ReleaseJoinedAsync(JobFinderDbContext context, IDbContextOutbox outbox, ApplicationBlockSnapshot expected, string actor, CancellationToken ct)
    {
        if (!ReferenceEquals(outbox.ActiveContext, context)) throw new InvalidOperationException("application block release requires its enrolled context");
        await EnsureAsync(context, expected, ct);
        if (expected.Value is null) return;
        await ChangeJoinedAsync(context, expected, null, actor, null, ct);
        notifier.Add(context, NotificationKind.Info, "▶️ Пауза откликов hh после капчи снята по проверенной версии.");
    }

    internal async Task RecordJoinedAsync(JobFinderDbContext context, IDbContextOutbox outbox, DateTimeOffset until, DeliveryAttempt attempt, CancellationToken ct)
    {
        if (!ReferenceEquals(outbox.ActiveContext, context) || attempt.Status != DeliveryAttemptStatus.KnownNotSent
            || attempt.ApplicationDeliveryRequestId is null || attempt.CompletedAt is null || until == default
            || !ReferenceEquals(context.DeliveryAttempts.Local.SingleOrDefault(row => row.Id == attempt.Id), attempt))
            throw new InvalidOperationException("application block requires its original tracked known-not-sent native receipt");
        await LockAsync(context, ct);
        var current = await ReadJoinedAsync(context, ct);
        if (await context.ApplicationBlockRevisions.AnyAsync(row => row.AttemptId == attempt.Id, ct)) return;
        var at = (current.Until > until ? current.Until!.Value : until).ToUniversalTime();
        await ChangeJoinedAsync(context, current, at.ToString("O", CultureInfo.InvariantCulture), "hh-receipt", attempt.Id, ct);
        notifier.Add(context, NotificationKind.Info, $"⏸ Отклики hh приостановлены после капчи до {at:HH:mm} UTC; остальные каналы не заблокированы.");
    }

    private async Task ChangeJoinedAsync(JobFinderDbContext context, ApplicationBlockSnapshot before, string? value, string actor, Guid? attempt, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(actor)) throw new ArgumentException("actor is required", nameof(actor));
        Parse(value);
        var now = time.GetUtcNow();
        now = new DateTimeOffset(now.UtcTicks - now.UtcTicks % 10, TimeSpan.Zero);
        var revision = checked(before.Revision + 1);
        var row = await context.Settings.FindAsync([RuntimeSettings.ApplyBlockedUntil], ct);
        if (value is null) { if (row is not null) context.Settings.Remove(row); }
        else if (row is null) context.Settings.Add(new AppSetting { Key = RuntimeSettings.ApplyBlockedUntil, Value = value, UpdatedAt = now });
        else { row.Value = value; row.UpdatedAt = now; }
        var marker = await context.Settings.FindAsync([RevisionKey], ct);
        if (marker is null) context.Settings.Add(new AppSetting { Key = RevisionKey, Value = revision.ToString(CultureInfo.InvariantCulture), UpdatedAt = now });
        else { marker.Value = revision.ToString(CultureInfo.InvariantCulture); marker.UpdatedAt = now; }
        context.ApplicationBlockRevisions.Add(new() { Revision = revision, BeforeValue = before.Value, Value = value, PublishedAt = now, Actor = actor, AttemptId = attempt });
    }

    internal static DateTimeOffset? Parse(string? value) => value is null ? null
        : DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var at) && at != default ? at
        : throw new InvalidOperationException("damaged application block instant; automatic sending/release refused");
}
