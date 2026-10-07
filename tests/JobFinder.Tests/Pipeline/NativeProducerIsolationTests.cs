using JobFinder.Core.Domain;
using JobFinder.Pipeline.Notifications;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Wolverine;
using Wolverine.EntityFrameworkCore;
using Wolverine.Persistence;
using Wolverine.Runtime;
using Wolverine.Runtime.Agents;

namespace JobFinder.Tests.Pipeline;

public sealed partial class NativeNotificationDeliveryTests
{
    [Fact]
    public async Task Producer_preserves_explicit_ef_outbox_atomicity_and_recovery_coordination_without_business_consumers()
    {
        var producer = await PostProcessingTestHost.BuildAsync(pg, consumer: false);
        _hosts.Add(producer);
        await producer.StartAsync(Ct);
        var runtime = producer.Services.GetRequiredService<IWolverineRuntime>();
        Assert.Equal(DurabilityMode.Balanced, runtime.Options.Durability.Mode);
        Assert.False(runtime.Options.Durability.DurabilityAgentEnabled);
        Assert.Contains(producer.Services.GetServices<IAgentFamily>(), family => ReferenceEquals(family, runtime.Stores));
        var coordinator = (WolverineRuntime)runtime;
        await UntilAsync(() => Task.FromResult(coordinator.IsLeader()));
        await using var scope = producer.Services.CreateAsyncScope();
        var outbox = scope.ServiceProvider.GetRequiredService<IDbContextOutbox>();
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        await using var transaction = await context.Database.BeginTransactionAsync(Ct);
        outbox.Enroll(context);
        var notice = await NoticeAsync();
        context.Notifications.Add(notice);
        var requests = producer.Services.GetRequiredService<NotificationDeliveryRequests>();
        var id = await requests.QueueAsync(context, outbox, notice, Ct);
        await requests.SaveJoinedAsync(context, outbox, Ct);
        await using (var outside = await pg.Db.CreateDbContextAsync(Ct))
        {
            Assert.False(await outside.Notifications.AnyAsync(row => row.Id == notice.Id, Ct));
            Assert.False(await outside.NotificationDeliveryRequests.AnyAsync(row => row.Id == id, Ct));
            Assert.Equal(0, await outside.Database.SqlQuery<int>($"""
                SELECT count(*)::int AS "Value" FROM jobfinder_queues.wolverine_queue_notifications
                """).SingleAsync(Ct));
        }
        await requests.SaveAndFlushAsync(context, outbox, Ct);
        Assert.Equal(NotificationDeliveryStage.AwaitingTarget, (await RootAsync(id)).Stage);
        await using var read = await pg.Db.CreateDbContextAsync(Ct);
        Assert.Equal(1, await read.Database.SqlQuery<int>($"""
            SELECT count(*)::int AS "Value" FROM jobfinder_queues.wolverine_queue_notifications
            """).SingleAsync(Ct));
        Assert.Empty(_api.Calls);
        await HostAsync(true, fastRecovery: true);
        await UntilAsync(async () => (await RootAsync(id)).Stage == NotificationDeliveryStage.Completed);
        Assert.Equal("sendMessage", Assert.Single(_api.Calls).Method);
        Assert.True(coordinator.IsLeader());
        Assert.DoesNotContain(coordinator.AllRunningAgentUris(), uri => uri.Scheme == "wolverinedb");
    }
}
