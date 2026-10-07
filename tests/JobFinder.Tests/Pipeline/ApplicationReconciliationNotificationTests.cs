using JobFinder.Core.Domain;
using JobFinder.Pipeline.Actions;
using JobFinder.Pipeline.Modules.Hiring;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;

namespace JobFinder.Tests.Pipeline;

public sealed partial class DurableApplicationDeliveryIntegrationTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Application_reconciliation_admission_failure_preserves_unknown_source_attempt_and_funnel(bool sent, bool manualAction)
    {
        var application = await SeedAsync(channel: ApplicationChannel.TelegramDm);
        var recipient = new Recipient { Receipt = new DeliveryReceipt(ApplicationStatus.OutcomeUnknown, "synthetic uncertain receipt") };
        var fault = new ApplicationReconciliationAdmissionFault();
        var host = await HostAsync(true, new Gate(), recipient, fault);
        var id = await EnqueueAsync(host, application);
        await UntilAsync(async () => (await RequestAsync(id)).Stage == ApplicationDeliveryStage.OutcomeUnknown);
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        await NotificationAdmissionAssertions.ResetAsync(context, Ct);
        var original = await context.Applications.AsNoTracking().SingleAsync(Ct);
        var originalRequest = await RequestAsync(id);
        fault.Active = true;
        if (manualAction)
            await Assert.ThrowsAsync<InvalidOperationException>(() => host.Services.GetRequiredService<ApprovalService>()
                .MarkManualAsync(application.VacancyId, Ct));
        else
            await Assert.ThrowsAsync<InvalidOperationException>(() => host.Services.GetRequiredService<DeliveryLedger>()
                .ReconcileApplicationAsync(id, sent, "Synthetic checked inbox", Ct));
        Assert.True(fault.SourceSaveCompleted);
        var unchanged = await context.Applications.AsNoTracking().SingleAsync(Ct);
        Assert.Equal(ApplicationStatus.OutcomeUnknown, unchanged.Status);
        Assert.Equal(original.Revision, unchanged.Revision);
        Assert.Equal(original.DeliveryRequestId, unchanged.DeliveryRequestId);
        Assert.Equal(original.Stage, unchanged.Stage);
        Assert.Equal(original.SentAt, unchanged.SentAt);
        var request = await RequestAsync(id);
        Assert.Equal(ApplicationDeliveryStage.OutcomeUnknown, request.Stage);
        Assert.Equal(originalRequest.InputHash, request.InputHash);
        Assert.Equal(originalRequest.InputJson, request.InputJson);
        Assert.Equal(DeliveryAttemptStatus.OutcomeUnknown, (await context.DeliveryAttempts.AsNoTracking().SingleAsync(Ct)).Status);
        Assert.Empty(await context.ApplicationEvents.ToListAsync(Ct));
        Assert.Empty(await context.Notifications.ToListAsync(Ct));
        Assert.Empty(await context.NotificationDeliveryRequests.ToListAsync(Ct));
        Assert.Equal(0, await context.Database.SqlQuery<int>($"""
            SELECT count(*)::int AS "Value" FROM jobfinder_queues.wolverine_queue_notifications
            """).SingleAsync(Ct));
        Assert.Single(recipient.Deliveries);
        fault.Active = false;
        Assert.True(await host.Services.GetRequiredService<DeliveryLedger>().ReconcileApplicationAsync(id, sent, "Synthetic checked inbox", Ct));
        Assert.False(await host.Services.GetRequiredService<DeliveryLedger>().ReconcileApplicationAsync(id, sent, "Synthetic repeated check", Ct));
        var notice = Assert.Single(await context.Notifications.ToListAsync(Ct));
        await NotificationAdmissionAssertions.JoinedAsync(context, notice, Ct);
        Assert.Equal(DeliveryAttemptStatus.Reconciled, (await context.DeliveryAttempts.AsNoTracking().SingleAsync(Ct)).Status);
        Assert.Empty(await context.NotificationDeliveryAttempts.ToListAsync(Ct));
        Assert.Single(recipient.Deliveries);
    }

    private sealed class ApplicationReconciliationAdmissionFault : SaveChangesInterceptor
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
                throw new InvalidOperationException("synthetic application reconciliation admission failure");
            }
            return ValueTask.FromResult(result);
        }
    }
}
