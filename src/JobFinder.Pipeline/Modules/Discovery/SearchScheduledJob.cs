using JobFinder.Data;
using JobFinder.Pipeline.Jobs;
using JobFinder.Pipeline.Notifications;
using Microsoft.EntityFrameworkCore;

namespace JobFinder.Pipeline.Modules.Discovery;

public abstract class SearchScheduledJob(SearchPolicyStore policies, IDbContextFactory<JobFinderDbContext> db,
    JobTriggers triggers, Notifier notifier, TimeProvider time, ILogger log) : ScheduledJob(db, triggers, notifier, time, log)
{
    protected SearchPolicySnapshot Snapshot { get; private set; } = null!;

    protected override TimeSpan? ConfigurationRefreshInterval => TimeSpan.FromMinutes(1);

    protected override long ConfigurationRevision => Snapshot.Version;

    protected override async Task RefreshConfigurationAsync(CancellationToken ct) => Snapshot = await policies.ReadAsync(ct);
}
