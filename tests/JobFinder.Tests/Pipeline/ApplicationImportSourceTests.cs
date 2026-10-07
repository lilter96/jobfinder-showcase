using JobFinder.Core.Domain;
using JobFinder.Pipeline.Modules.Hiring;
using JobFinder.Pipeline.Notifications;
using JobFinder.Pipeline.Workers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace JobFinder.Tests.Pipeline;

public sealed partial class OrdinaryNotificationAdmissionTests
{
    [Theory]
    [InlineData(DeliveryAttemptStatus.Submitting)]
    [InlineData(DeliveryAttemptStatus.OutcomeUnknown)]
    public async Task Dry_run_release_preserves_uncertain_retained_delivery(DeliveryAttemptStatus status)
    {
        var held = await ApplicationAsync(ApplicationStatus.DryRun);
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        context.DeliveryAttempts.Add(new DeliveryAttempt { ApplicationId = held.Id,
            OperationId = Guid.NewGuid(), Channel = ApplicationChannel.Email, Text = "Synthetic retained attempt", TextHash = new string('a', 64),
            Status = status, CreatedAt = DateTimeOffset.UtcNow });
        await context.SaveChangesAsync(Ct);
        var host = await HostAsync();
        await using var publication = await host.Services.GetRequiredService<Microsoft.EntityFrameworkCore.IDbContextFactory<JobFinder.Data.JobFinderDbContext>>().CreateDbContextAsync(Ct);
        Assert.Equal((0, 0), await ApplyWorker.ReleaseHeldAsync(publication, host.Services.GetRequiredService<Notifier>(), DateTimeOffset.UtcNow, Ct));
        Assert.Equal(ApplicationStatus.DryRun, (await context.Applications.AsNoTracking().SingleAsync(Ct)).Status);
        Assert.Equal(status, (await context.DeliveryAttempts.AsNoTracking().SingleAsync(Ct)).Status);
        await NoAdmissionAsync(context);
    }

    [Fact]
    public async Task Dry_run_release_waits_for_source_lock_and_cannot_undo_a_newer_cancellation()
    {
        var held = await ApplicationAsync(ApplicationStatus.DryRun);
        var host = await HostAsync();
        await using var owner = await pg.Db.CreateDbContextAsync(Ct);
        await using var transaction = await owner.Database.BeginTransactionAsync(Ct);
        await DeliveryLedger.LockVacancyAsync(owner, held.VacancyId, Ct);
        await using var publication = await pg.Db.CreateDbContextAsync(Ct);
        await publication.Database.OpenConnectionAsync(Ct);
        var backend = await publication.Database.SqlQuery<int>($"SELECT pg_backend_pid() AS \"Value\"").SingleAsync(Ct);
        var release = ApplyWorker.ReleaseHeldAsync(publication, host.Services.GetRequiredService<Notifier>(), DateTimeOffset.UtcNow, Ct);
        try
        {
            var deadline = DateTimeOffset.UtcNow.AddSeconds(10);
            while (!await owner.Database.SqlQuery<bool>($"SELECT EXISTS(SELECT 1 FROM pg_stat_activity WHERE pid = {backend} AND wait_event_type = 'Lock') AS \"Value\"").SingleAsync(Ct))
            {
                Assert.True(DateTimeOffset.UtcNow < deadline, "source release did not reach the held lock");
                await Task.Delay(20, Ct);
            }
            await owner.Applications.Where(row => row.Id == held.Id).ExecuteUpdateAsync(update => update
                .SetProperty(row => row.Status, ApplicationStatus.Cancelled).SetProperty(row => row.Revision, row => row.Revision + 1), Ct);
            await transaction.CommitAsync(Ct);
            Assert.Equal((0, 0), await release);
            Assert.Equal(ApplicationStatus.Cancelled, (await owner.Applications.AsNoTracking().SingleAsync(Ct)).Status);
            await NoAdmissionAsync(owner);
        }
        finally { if (!release.IsCompleted) { await transaction.RollbackAsync(CancellationToken.None); await release; } }
    }
}
