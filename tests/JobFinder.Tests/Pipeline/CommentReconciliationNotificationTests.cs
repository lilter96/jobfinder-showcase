using JobFinder.Core.Domain;
using JobFinder.Pipeline.Modules.Engagement;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;

namespace JobFinder.Tests.Pipeline;

public sealed partial class DurableCommentPreparationIntegrationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Manual_comment_reconciliation_commits_native_notice_without_publishing_or_certifying_transport(bool sent)
    {
        var host = await DeliveryHostAsync(false);
        await SourceProfileAsync(host);
        var id = await QueueDeliveryAsync(host, await DeliveryDraftAsync(attempts: 1));
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        await NotificationAdmissionAssertions.ResetAsync(context, Ct);
        var ledger = host.Services.GetRequiredService<CommentDeliveryLedger>();
        Assert.True((await ledger.ReconcileAsync(id, sent, "Synthetic primary thread checked", Ct)).Ok);
        Assert.False((await ledger.ReconcileAsync(id, sent, "Synthetic repeated check", Ct)).Ok);
        var attempt = Assert.Single(await context.CommentDeliveryAttempts.ToListAsync(Ct));
        Assert.Equal(DeliveryAttemptStatus.Reconciled, attempt.Status);
        Assert.Equal(sent, attempt.ReconciledSent);
        Assert.Equal(sent ? CommentDeliveryStage.Completed : CommentDeliveryStage.Cancelled, (await DeliveryRootAsync(id)).Stage);
        var notice = Assert.Single(await context.Notifications.ToListAsync(Ct));
        await NotificationAdmissionAssertions.JoinedAsync(context, notice, Ct);
        Assert.Empty(await context.NotificationDeliveryAttempts.ToListAsync(Ct));
        Assert.Equal(1, await context.Database.SqlQuery<int>($"""
            SELECT count(*)::int AS "Value" FROM jobfinder_queues.wolverine_queue_notifications
            """).SingleAsync(Ct));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Failed_comment_reconciliation_admission_preserves_unknown_attempt_source_and_revision(bool sent)
    {
        var fault = new ReconciliationAdmissionFault();
        var host = await BuildAsync(false, fault: fault);
        _hosts.Add(host);
        await host.StartAsync(Ct);
        await SourceProfileAsync(host);
        var id = await QueueDeliveryAsync(host, await DeliveryDraftAsync(attempts: 1));
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        await NotificationAdmissionAssertions.ResetAsync(context, Ct);
        var original = await context.CommentDrafts.AsNoTracking().SingleAsync(Ct);
        fault.Active = true;
        var ledger = host.Services.GetRequiredService<CommentDeliveryLedger>();
        await Assert.ThrowsAsync<InvalidOperationException>(() => ledger.ReconcileAsync(id, sent, "Synthetic primary thread checked", Ct));
        Assert.True(fault.SourceSaveCompleted);
        Assert.Equal(CommentDeliveryStage.OutcomeUnknown, (await DeliveryRootAsync(id)).Stage);
        var unchanged = await context.CommentDrafts.AsNoTracking().SingleAsync(Ct);
        Assert.Equal(CommentStatus.OutcomeUnknown, unchanged.Status);
        Assert.Equal(original.Revision, unchanged.Revision);
        Assert.Equal(original.DeliveryRequestId, unchanged.DeliveryRequestId);
        var attempt = Assert.Single(await context.CommentDeliveryAttempts.AsNoTracking().ToListAsync(Ct));
        Assert.Equal(DeliveryAttemptStatus.OutcomeUnknown, attempt.Status);
        Assert.Null(attempt.ReconciledSent);
        Assert.Empty(await context.Notifications.ToListAsync(Ct));
        Assert.Empty(await context.NotificationDeliveryRequests.ToListAsync(Ct));
        Assert.Equal(0, await context.Database.SqlQuery<int>($"""
            SELECT count(*)::int AS "Value" FROM jobfinder_queues.wolverine_queue_notifications
            """).SingleAsync(Ct));
    }

    private sealed class ReconciliationAdmissionFault : SaveChangesInterceptor
    {
        public bool Active { get; set; }
        public bool SourceSaveCompleted { get; private set; }
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (Active && eventData.Context!.ChangeTracker.Entries<NotificationDeliveryRequest>().Any(entry => entry.State == EntityState.Added))
            {
                SourceSaveCompleted = eventData.Context.ChangeTracker.Entries<Notification>().Any(entry => entry.State == EntityState.Modified
                    && entry.Entity.Id > 0 && entry.OriginalValues.GetValue<Guid?>(nameof(Notification.DeliveryRequestId)) is null);
                throw new InvalidOperationException("synthetic reconciliation admission failure");
            }
            return ValueTask.FromResult(result);
        }
    }
}
