using System.Globalization;
using JobFinder.Core.Domain;
using JobFinder.Core.Time;
using JobFinder.Data;
using JobFinder.Pipeline.Notifications;
using Microsoft.EntityFrameworkCore;

namespace JobFinder.Pipeline.Jobs;

public sealed class DigestOptions
{
    public const string Section = "Digest";

    public bool Enabled { get; set; } = true;

    /// <summary>Local times (Decision:TimeZone) of the digests.</summary>
    public List<string> Times { get; set; } = ["09:00", "19:00"];

    public int TopVacancies { get; set; } = 10;
}

/// <summary>Morning and evening summary: what was found, what was sent, what waits for the user, what broke, what it cost.</summary>
public sealed class DigestJob(DigestOptions options, LocalClock clock, DigestPublications publications, JobTriggers triggers, IDbContextFactory<JobFinderDbContext> db,
    Notifier notifier, TimeProvider time, ILogger<DigestJob> log) : ScheduledJob(db, triggers, notifier, time, log)
{
    public override string Name => JobNames.Digest;

    protected override bool Enabled => options.Enabled && options.Times.Count > 0;

    protected override TimeSpan FirstDelay => NextRunAfter(Time.GetUtcNow()) - Time.GetUtcNow();

    protected override DateTimeOffset NextRunAfter(DateTimeOffset finishedAt) => Slots.Select(clock.NextOccurrenceUtc).Min();

    protected override DateTimeOffset? LastSlot(DateTimeOffset now) => Slots.Select(t => clock.NextOccurrenceUtc(t).AddDays(-1)).Max();

    private IEnumerable<TimeOnly> Slots => options.Times
        .Select(t => TimeOnly.TryParse(t, CultureInfo.InvariantCulture, out var at) ? at : (TimeOnly?)null)
        .OfType<TimeOnly>();

    protected override Task<JobOutcome> RunOnceAsync(CancellationToken ct) =>
        throw new InvalidOperationException("digest publication requires its captured scheduler invocation");

    protected override Task<JobOutcome> RunForInvocationAsync(JobInvocation invocation, CancellationToken ct) => publications.PublishAsync(invocation, ct);

    protected override Task<DateTimeOffset?> LastSuccessfulStartAsync(CancellationToken ct) => publications.LastSlotAsync(ct);
}

/// <summary>Hourly cleanup: stale comment drafts expire, noise and old diagnostics are deleted.</summary>
public sealed class HousekeepingJob(Modules.Engagement.CommentExpiry expiry, JobTriggers triggers, IDbContextFactory<JobFinderDbContext> db,
    Notifier notifier, TimeProvider time, ILogger<HousekeepingJob> log, Maintenance.OperationalRetention retention) : ScheduledJob(db, triggers, notifier, time, log)
{
    public override string Name => JobNames.Housekeeping;

    protected override bool Enabled => true;

    protected override DateTimeOffset NextRunAfter(DateTimeOffset finishedAt) => finishedAt.AddHours(1);

    protected override async Task<JobOutcome> RunOnceAsync(CancellationToken ct)
    {
        var expired = await expiry.ExpireBatchAsync(100, ct);
        var swept = await retention.SweepAsync(100, ct);
        return new JobOutcome(expired + swept.Total, $"{expired} drafts expired, {swept.Posts} noise posts deleted");
    }
}
