using JobFinder.Core.Domain;
using JobFinder.Data;
using JobFinder.Pipeline.Modules.Hiring;
using JobFinder.Pipeline.Notifications;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;

namespace JobFinder.Tests.Pipeline;

public sealed partial class DeliveryIntegrationTests
{
    [Theory]
    [InlineData(ApplicationStatus.Sent)]
    [InlineData(ApplicationStatus.NeedsManual)]
    [InlineData(ApplicationStatus.OutcomeUnknown)]
    public async Task Legacy_receipt_and_callback_notice_commit_as_one_native_admission(ApplicationStatus status)
    {
        var dispatch = await SeedAsync();
        var recipient = new Recipient(_ => Task.FromResult(new DeliveryReceipt(status, "Synthetic checked result")));
        await Gateway(recipient).DispatchAsync(dispatch, RecordReceipt, Ct);
        Assert.Equal(1, recipient.Calls);
        Assert.Equal(status, (await ReadAsync(dispatch.Application.Id)).Status);
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        var notice = Assert.Single(await context.Notifications.Where(row => row.EntityId == dispatch.Application.VacancyId).ToListAsync(Ct));
        await NotificationAdmissionAssertions.JoinedAsync(context, notice, Ct);
        Assert.Empty(await context.NotificationDeliveryAttempts.Where(row => row.RequestId == notice.DeliveryRequestId).ToListAsync(Ct));
    }

    [Theory]
    [InlineData(ApplicationStatus.Sent)]
    [InlineData(ApplicationStatus.NeedsManual)]
    [InlineData(ApplicationStatus.OutcomeUnknown)]
    public async Task Legacy_receipt_admission_failure_preserves_submitting_attempt_and_sending_source(ApplicationStatus status)
    {
        var dispatch = await SeedAsync();
        var attempt = Assert.IsType<Guid>(await Ledger().TryClaimAsync(dispatch, Ct));
        var fault = new LegacyAdmissionFault();
        var ledger = await FaultLedgerAsync(fault);
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        var roots = await context.NotificationDeliveryRequests.CountAsync(Ct);
        var commands = await context.Database.SqlQuery<int>($"""
            SELECT count(*)::int AS "Value" FROM jobfinder_queues.wolverine_queue_notifications
            """).SingleAsync(Ct);
        await Assert.ThrowsAsync<InvalidOperationException>(() => ledger.CompleteAsync(attempt,
            new DeliveryReceipt(status, "Synthetic checked result"), RecordReceipt, Ct));
        Assert.True(fault.SourceSaveCompleted);
        Assert.Equal(ApplicationStatus.Sending, (await ReadAsync(dispatch.Application.Id)).Status);
        Assert.Equal(DeliveryAttemptStatus.Submitting, Assert.Single(await AttemptsAsync(dispatch.Application.Id)).Status);
        Assert.Empty(await context.Notifications.Where(row => row.EntityId == dispatch.Application.VacancyId).ToListAsync(Ct));
        Assert.Equal(roots, await context.NotificationDeliveryRequests.CountAsync(Ct));
        Assert.Equal(commands, await context.Database.SqlQuery<int>($"""
            SELECT count(*)::int AS "Value" FROM jobfinder_queues.wolverine_queue_notifications
            """).SingleAsync(Ct));
    }

    [Fact]
    public async Task Preparation_rejection_and_callback_notice_commit_as_one_native_admission()
    {
        var dispatch = await SeedAsync();
        var ledger = Ledger();
        Assert.True(await ledger.RejectPreparationAsync(dispatch.Application, "Synthetic rejected content", RecordRejection, Ct));
        Assert.False(await ledger.RejectPreparationAsync(dispatch.Application, "Synthetic repeated rejection", RecordRejection, Ct));
        Assert.Equal(ApplicationStatus.NeedsManual, (await ReadAsync(dispatch.Application.Id)).Status);
        Assert.Empty(await AttemptsAsync(dispatch.Application.Id));
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        var notice = Assert.Single(await context.Notifications.Where(row => row.EntityId == dispatch.Application.VacancyId).ToListAsync(Ct));
        await NotificationAdmissionAssertions.JoinedAsync(context, notice, Ct);
    }

    [Fact]
    public async Task Preparation_rejection_admission_failure_preserves_pending_source_and_does_not_create_a_notice()
    {
        var dispatch = await SeedAsync();
        var fault = new LegacyAdmissionFault();
        var ledger = await FaultLedgerAsync(fault);
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        var roots = await context.NotificationDeliveryRequests.CountAsync(Ct);
        var commands = await context.Database.SqlQuery<int>($"""
            SELECT count(*)::int AS "Value" FROM jobfinder_queues.wolverine_queue_notifications
            """).SingleAsync(Ct);
        await Assert.ThrowsAsync<InvalidOperationException>(() => ledger.RejectPreparationAsync(dispatch.Application,
            "Synthetic rejected content", RecordRejection, Ct));
        Assert.True(fault.SourceSaveCompleted);
        var unchanged = await ReadAsync(dispatch.Application.Id);
        Assert.Equal(ApplicationStatus.Pending, unchanged.Status);
        Assert.Equal(dispatch.Application.Revision, unchanged.Revision);
        Assert.Null(unchanged.Error);
        Assert.Empty(await context.Notifications.Where(row => row.EntityId == dispatch.Application.VacancyId).ToListAsync(Ct));
        Assert.Empty(await AttemptsAsync(dispatch.Application.Id));
        Assert.Equal(roots, await context.NotificationDeliveryRequests.CountAsync(Ct));
        Assert.Equal(commands, await context.Database.SqlQuery<int>($"""
            SELECT count(*)::int AS "Value" FROM jobfinder_queues.wolverine_queue_notifications
            """).SingleAsync(Ct));
    }

    private async Task<DeliveryLedger> FaultLedgerAsync(SaveChangesInterceptor fault)
    {
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        var options = new DbContextOptionsBuilder<JobFinderDbContext>();
        JobFinderDbContext.Configure(options, context.Database.GetConnectionString()!);
        options.AddInterceptors(fault);
        return new DeliveryLedger(new Factory(options.Options), TimeProvider.System, _producer!.Services.GetRequiredService<Notifier>());
    }

    private static void RecordReceipt(JobFinderDbContext context, Application application, DeliveryReceipt receipt) =>
        context.Notifications.Add(new Notification
        {
            Kind = NotificationKind.Info, Status = NotificationStatus.Pending, EntityId = application.VacancyId,
            Text = "Synthetic receipt: " + receipt.Status, CreatedAt = DateTimeOffset.UtcNow,
        });

    private static void RecordRejection(JobFinderDbContext context, Application application) =>
        RecordReceipt(context, application, new DeliveryReceipt(ApplicationStatus.NeedsManual, "Synthetic rejected content"));

    private sealed class LegacyAdmissionFault : SaveChangesInterceptor
    {
        public bool SourceSaveCompleted { get; private set; }
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (eventData.Context!.ChangeTracker.Entries<NotificationDeliveryRequest>().Any(entry => entry.State == EntityState.Added))
            {
                SourceSaveCompleted = eventData.Context.ChangeTracker.Entries<Notification>().Any(entry => entry.State == EntityState.Modified
                    && entry.Entity.Id > 0 && entry.OriginalValues.GetValue<Guid?>(nameof(Notification.DeliveryRequestId)) is null);
                throw new InvalidOperationException("synthetic legacy ledger admission failure");
            }
            return ValueTask.FromResult(result);
        }
    }
}
