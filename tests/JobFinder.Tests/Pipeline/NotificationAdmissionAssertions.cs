using JobFinder.Core.Domain;
using JobFinder.Data;
using JobFinder.Pipeline.Notifications;
using Microsoft.EntityFrameworkCore;

namespace JobFinder.Tests.Pipeline;

internal static class NotificationAdmissionAssertions
{
    public static async Task ResetAsync(JobFinderDbContext context, CancellationToken ct)
    {
        await context.NotificationDeliveryAttempts.ExecuteDeleteAsync(ct);
        await context.NotificationDeliveryRequests.ExecuteDeleteAsync(ct);
        await context.Notifications.ExecuteDeleteAsync(ct);
        await context.Database.ExecuteSqlRawAsync("""
            TRUNCATE jobfinder_queues.wolverine_queue_notifications, jobfinder_queues.wolverine_queue_notifications_scheduled;
            DELETE FROM jobfinder_messages.wolverine_incoming_envelopes WHERE message_type LIKE 'jobfinder.notifications.%' OR message_type LIKE '%DeliverNotification%' OR message_type LIKE '%BindNotificationTarget%';
            DELETE FROM jobfinder_messages.wolverine_outgoing_envelopes WHERE message_type LIKE 'jobfinder.notifications.%' OR message_type LIKE '%DeliverNotification%' OR message_type LIKE '%BindNotificationTarget%';
            DELETE FROM jobfinder_messages.wolverine_dead_letters WHERE message_type LIKE 'jobfinder.notifications.%' OR message_type LIKE '%DeliverNotification%' OR message_type LIKE '%BindNotificationTarget%';
            """, ct);
    }

    public static async Task JoinedAsync(JobFinderDbContext context, Notification notice, CancellationToken ct, NotificationTarget? expectedTarget = null)
    {
        Assert.NotNull(notice.DeliveryRequestId);
        var root = await context.NotificationDeliveryRequests.AsNoTracking().SingleAsync(row => row.Id == notice.DeliveryRequestId, ct);
        Assert.Equal(notice.Id, root.NotificationId);
        Assert.NotNull(root.AdmissionHash);
        Assert.NotNull(root.AdmissionJson);
        var input = NotificationDeliveryInputs.Input(root);
        Assert.True(input.Matches(notice));
        Assert.Equal(expectedTarget, input.Target);
        Assert.Equal(expectedTarget is null ? NotificationDeliveryStage.AwaitingTarget : NotificationDeliveryStage.Preparing, root.Stage);
        Assert.Null(root.PlanJson);
        Assert.Null(root.AttemptId);
    }
}
