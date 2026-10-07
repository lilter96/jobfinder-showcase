using JobFinder.Core.Domain;
using JobFinder.Pipeline.Jobs;
using JobFinder.Pipeline.Modules.Preparation;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;

namespace JobFinder.Tests.Pipeline;

public sealed partial class InterviewSourceIntegrationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Interview_reminder_admission_commits_exactly_one_flag_notice_and_command(bool debrief)
    {
        var source = await InterviewAsync();
        await SourcesAsync();
        var now = DateTimeOffset.UtcNow;
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        await NotificationAdmissionAssertions.ResetAsync(context, Ct);
        await context.Interviews.ExecuteUpdateAsync(update => update.SetProperty(row => row.ScheduledAt,
            debrief ? now.AddDays(-1) : now.AddMinutes(30)), Ct);
        var snapshot = InterviewEditSnapshot.Capture(await ReadAsync(source.Id));
        var job = ActivatorUtilities.CreateInstance<InterviewReminderJob>(_host!.Services);
        var results = await Task.WhenAll(job.NotifyAsync(snapshot, now, debrief, Ct), job.NotifyAsync(snapshot, now, debrief, Ct));
        Assert.Single(results, result => result);
        var saved = await ReadAsync(source.Id);
        Assert.Equal(!debrief, saved.Reminded);
        Assert.Equal(debrief, saved.DebriefAsked);
        Assert.True(snapshot.Matches(saved));
        var notice = Assert.Single(await context.Notifications.ToListAsync(Ct));
        await NotificationAdmissionAssertions.JoinedAsync(context, notice, Ct);
        Assert.Equal(1, await context.Database.SqlQuery<int>($"""
            SELECT count(*)::int AS "Value" FROM jobfinder_queues.wolverine_queue_notifications
            """).SingleAsync(Ct));
        Assert.Empty(await context.NotificationDeliveryAttempts.ToListAsync(Ct));
        Assert.Empty(await context.InterviewPreparationRequests.ToListAsync(Ct));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Interview_reminder_admission_failure_preserves_flag_and_preparation_without_losing_the_notice(bool debrief)
    {
        var source = await InterviewAsync();
        var fault = new InterviewReminderAdmissionFault();
        await SourcesAsync(fault);
        var now = DateTimeOffset.UtcNow;
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        await NotificationAdmissionAssertions.ResetAsync(context, Ct);
        await context.Interviews.ExecuteUpdateAsync(update => update.SetProperty(row => row.ScheduledAt,
            debrief ? now.AddDays(-1) : now.AddMinutes(30)), Ct);
        var snapshot = InterviewEditSnapshot.Capture(await ReadAsync(source.Id));
        var job = ActivatorUtilities.CreateInstance<InterviewReminderJob>(_host!.Services);
        fault.Active = true;
        await Assert.ThrowsAsync<InvalidOperationException>(() => job.NotifyAsync(snapshot, now, debrief, Ct));
        Assert.True(fault.SourceSaveCompleted);
        var unchanged = await ReadAsync(source.Id);
        Assert.False(unchanged.Reminded);
        Assert.False(unchanged.DebriefAsked);
        Assert.True(snapshot.Matches(unchanged));
        Assert.Empty(await context.Notifications.ToListAsync(Ct));
        Assert.Empty(await context.NotificationDeliveryRequests.ToListAsync(Ct));
        Assert.Empty(await context.InterviewPreparationRequests.ToListAsync(Ct));
        Assert.Equal(0, await context.Database.SqlQuery<int>($"""
            SELECT count(*)::int AS "Value" FROM jobfinder_queues.wolverine_queue_notifications
            """).SingleAsync(Ct));
        fault.Active = false;
        Assert.True(await job.NotifyAsync(snapshot, now, debrief, Ct));
        Assert.False(await job.NotifyAsync(snapshot, now, debrief, Ct));
        var notice = Assert.Single(await context.Notifications.ToListAsync(Ct));
        await NotificationAdmissionAssertions.JoinedAsync(context, notice, Ct);
    }

    private sealed class InterviewReminderAdmissionFault : SaveChangesInterceptor
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
                throw new InvalidOperationException("synthetic interview reminder admission failure");
            }
            return ValueTask.FromResult(result);
        }
    }
}
