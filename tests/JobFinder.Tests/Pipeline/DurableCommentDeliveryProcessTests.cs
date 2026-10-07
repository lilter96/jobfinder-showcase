using JobFinder.Core.Domain;
using JobFinder.MessagingProbe;
using JobFinder.Pipeline.Modules.Engagement;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace JobFinder.Tests.Pipeline;

public sealed partial class DurableCommentPreparationIntegrationTests
{
    [Theory]
    [InlineData(CommentDeliveryStage.Sending, "before-checkpoint")]
    [InlineData(CommentDeliveryStage.Sending, "after-checkpoint")]
    [InlineData(CommentDeliveryStage.Sending, "before-effect")]
    [InlineData(CommentDeliveryStage.Sending, "after-effect")]
    [InlineData(CommentDeliveryStage.Completed, "before-checkpoint")]
    [InlineData(CommentDeliveryStage.Completed, "after-checkpoint")]
    public async Task Actual_delivery_process_termination_preserves_claim_effect_and_receipt_boundaries(CommentDeliveryStage phase, string boundary)
    {
        var producer = await DeliveryHostAsync(false);
        await SourceProfileAsync(producer);
        var id = await QueueDeliveryAsync(producer, await DeliveryDraftAsync());
        await producer.StopAsync(Ct);
        await KillCommentProcessAsync(id, "--comment-delivery-consumer", phase.ToString(), boundary);
        var before = await DeliveryRootAsync(id);
        var beforeClaim = phase == CommentDeliveryStage.Sending && boundary == "before-checkpoint";
        var committed = phase == CommentDeliveryStage.Completed && boundary == "after-checkpoint";
        Assert.Equal(beforeClaim ? CommentDeliveryStage.Ready : committed ? CommentDeliveryStage.Completed : CommentDeliveryStage.Sending, before.Stage);
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        var key = CommentDeliveryProbeTransport.CounterKey(id);
        var hadEffect = boundary == "after-effect" || phase == CommentDeliveryStage.Completed;
        Assert.Equal(hadEffect ? "1" : null, await context.Settings.Where(row => row.Key == key).Select(row => row.Value).SingleOrDefaultAsync(Ct));
        var recovered = await BuildAsync(true, fastRecovery: true, delivery: new CommentDeliveryProbeTransport(pg.Db, id));
        _hosts.Add(recovered);
        await recovered.StartAsync(Ct);
        await UntilAsync(async () => (await DeliveryRootAsync(id)).Stage == (beforeClaim || committed ? CommentDeliveryStage.Completed : CommentDeliveryStage.OutcomeUnknown));
        await UntilAsync(async () => await context.Database.SqlQuery<int>($"SELECT count(*)::int AS \"Value\" FROM jobfinder_messages.wolverine_incoming_envelopes WHERE status = 'Incoming'").SingleAsync(Ct) == 0);
        Assert.Equal(beforeClaim || hadEffect ? "1" : null, await context.Settings.Where(row => row.Key == key).Select(row => row.Value).SingleOrDefaultAsync(Ct));
        Assert.Equal(beforeClaim || committed ? DeliveryAttemptStatus.Confirmed : DeliveryAttemptStatus.OutcomeUnknown,
            (await context.CommentDeliveryAttempts.SingleAsync(Ct)).Status);
        Assert.Single(await context.CommentDeliveryRequests.ToListAsync(Ct));
        Assert.Single(await context.CommentDeliveryAttempts.ToListAsync(Ct));
        if (!beforeClaim && !committed)
            Assert.Single(await context.Notifications.Where(row => row.Text.Contains("Неизвестный исход комментария")).ToListAsync(Ct));
    }
}
