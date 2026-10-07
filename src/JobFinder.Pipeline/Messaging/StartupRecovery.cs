using JobFinder.Core.Domain;
using JobFinder.Core.Logging;
using JobFinder.Core.Telemetry;
using JobFinder.Data;
using JobFinder.Pipeline.Modules.Engagement;
using JobFinder.Pipeline.Modules.Hiring;
using JobFinder.Pipeline.Notifications;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace JobFinder.Pipeline.Messaging;

public sealed class StartupRecovery(IDbContextFactory<JobFinderDbContext> db, Notifier notifier, TimeProvider time,
    ServerOwnership ownership, IHostApplicationLifetime lifetime, ILogger<StartupRecovery> log) : IHostedService
{
    public async Task StartAsync(CancellationToken ct)
    {
        using var stopping = CancellationTokenSource.CreateLinkedTokenSource(ct, lifetime.ApplicationStopping);
        ownership.EnsureOwned();
        stopping.Token.ThrowIfCancellationRequested();
        using var stage = Telemetry.StartStage("recover-deliveries", "bootstrap");
        await using var context = await db.CreateDbContextAsync(stopping.Token);
        ownership.EnsureOwned();
        var now = time.GetUtcNow();
        var sending = await DeliveryLedger.RecoverAsync(context, notifier, now, stopping.Token);
        ownership.EnsureOwned();
        var comments = await CommentDeliveryLedger.RecoverAsync(context, notifier, now, stopping.Token);
        ownership.EnsureOwned();
        await Modules.Discovery.ChannelMembershipLedger.RecoverAsync(context, notifier, now, stopping.Token);
        ownership.EnsureOwned();
        await Controls.BotControls.RecoverAsync(context, notifier, stopping.Token);
        ownership.EnsureOwned();
        var running = await context.JobRuns.Where(row => row.Status == JobRunStatus.Running && row.WeeklyReportRequestId == null)
            .ExecuteUpdateAsync(update => update.SetProperty(row => row.Status, JobRunStatus.Failed)
                .SetProperty(row => row.Message, "interrupted"), stopping.Token);
        ownership.EnsureOwned();
        stage.Tag("deliveries", sending).Tag("comments", comments).Tag("job_runs", running);
        if (sending + comments + running > 0)
            log.Info($"[db] recovered {sending} applications, {comments} comments and {running} job runs interrupted by the last stop");
    }

    public Task StopAsync(CancellationToken ct) => Task.CompletedTask;
}
