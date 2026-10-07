using JobFinder.Core.Domain;
using JobFinder.Data;
using JobFinder.Pipeline.Notifications;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Wolverine;

namespace JobFinder.Tests.Pipeline;

public sealed class NotificationDeliverySchemaTests(PostgresFixture pg) : IClassFixture<PostgresFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("root")]
    [InlineData("attempt")]
    [InlineData("marker")]
    [InlineData("envelope")]
    [InlineData("binding-envelope")]
    [InlineData("edit-parent")]
    public async Task Guarded_notification_downgrade_refuses_native_work_and_orphaned_ownership_evidence(string evidence)
    {
        Assert.SkipWhen(pg.SkipReason is not null, pg.SkipReason ?? "");
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        var id = Guid.NewGuid();
        try
        {
            if (evidence == "root") context.NotificationDeliveryRequests.Add(new() { Id = id, NotificationId = 1, CreatedAt = DateTimeOffset.UtcNow });
            else if (evidence == "attempt") context.NotificationDeliveryAttempts.Add(new() { Id = id, RequestId = id, CreatedAt = DateTimeOffset.UtcNow });
            else if (evidence == "marker") context.Notifications.Add(new() { Text = "synthetic ownership marker", DeliveryRequestId = id, CreatedAt = DateTimeOffset.UtcNow });
            else if (evidence == "edit-parent") context.Notifications.Add(new() { Text = "synthetic original card marker", EditParentNotificationId = 1, CreatedAt = DateTimeOffset.UtcNow });
            else
            {
                using var producer = await PostProcessingTestHost.BuildAsync(pg, false);
                await producer.StartAsync(Ct);
                if (evidence == "binding-envelope") await producer.Services.GetRequiredService<IMessageBus>().ScheduleAsync(new BindNotificationTarget { RequestId = id }, DateTimeOffset.UtcNow.AddHours(1));
                else await producer.Services.GetRequiredService<IMessageBus>().ScheduleAsync(new DeliverNotification { RequestId = id }, DateTimeOffset.UtcNow.AddHours(1));
                await producer.StopAsync(Ct);
            }
            await context.SaveChangesAsync(Ct);
            var error = await Assert.ThrowsAsync<PostgresException>(() => MigrationTestRunner.DowngradeAndRestoreAsync(context, "20261001145654_DurableInterviewPreparation", Ct));
            Assert.Contains("Cannot roll back notification delivery", error.MessageText);
        }
        finally
        {
            await context.Database.ExecuteSqlRawAsync("TRUNCATE jobfinder_queues.wolverine_queue_notifications, jobfinder_queues.wolverine_queue_notifications_scheduled; DELETE FROM jobfinder_messages.wolverine_incoming_envelopes; DELETE FROM jobfinder_messages.wolverine_outgoing_envelopes; DELETE FROM jobfinder_messages.wolverine_dead_letters", CancellationToken.None);
            await context.NotificationDeliveryAttempts.ExecuteDeleteAsync(CancellationToken.None);
            await context.NotificationDeliveryRequests.ExecuteDeleteAsync(CancellationToken.None);
            await context.Notifications.Where(row => row.DeliveryRequestId != null || row.EditParentNotificationId != null).ExecuteDeleteAsync(CancellationToken.None);
            await context.Database.MigrateAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Unused_notification_schema_can_be_downgraded_and_reapplied_without_model_drift()
    {
        Assert.SkipWhen(pg.SkipReason is not null, pg.SkipReason ?? "");
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        try { await context.GetService<IMigrator>().MigrateAsync("20261001145654_DurableInterviewPreparation", Ct); }
        finally { await context.Database.MigrateAsync(CancellationToken.None); }
        Assert.False(context.Database.HasPendingModelChanges());
    }
}
