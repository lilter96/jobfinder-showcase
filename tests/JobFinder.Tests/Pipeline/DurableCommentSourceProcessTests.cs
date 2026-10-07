using JobFinder.Core.Domain;
using JobFinder.MessagingProbe;
using JobFinder.Pipeline.Modules.Engagement;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace JobFinder.Tests.Pipeline;

public sealed partial class DurableCommentPreparationIntegrationTests
{
    [Theory]
    [InlineData(CommentSourceStage.Queued, "before-checkpoint")]
    [InlineData(CommentSourceStage.Queued, "after-checkpoint")]
    [InlineData(CommentSourceStage.Waiting, "before-checkpoint")]
    [InlineData(CommentSourceStage.Waiting, "after-checkpoint")]
    public async Task Actual_source_process_termination_keeps_child_handoff_and_card_publication_atomic(CommentSourceStage phase, string boundary)
    {
        var producer = await HostAsync(false);
        var id = (await CollectAsync(producer, await SourceProfileAsync(producer)))!.Value;
        await producer.StopAsync(Ct);
        await KillCommentProcessAsync(id, "--comment-source-consumer", phase.ToString(), boundary);
        var before = await SourceAsync(id);
        if (boundary == "after-checkpoint")
        {
            Assert.Equal(phase == CommentSourceStage.Queued ? CommentSourceStage.Waiting : CommentSourceStage.Completed, before.Stage);
            Assert.Null(before.ClaimEnvelopeId);
        }
        else
        {
            Assert.Equal(phase, before.Stage);
            Assert.NotNull(before.ClaimEnvelopeId);
        }
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        Assert.Equal(phase == CommentSourceStage.Queued && boundary == "before-checkpoint" ? 0 : 1, await context.CommentPreparationRequests.CountAsync(Ct));
        var committed = phase == CommentSourceStage.Waiting && boundary == "after-checkpoint";
        Assert.Equal(committed ? 1 : 0, await context.CommentDrafts.CountAsync(Ct));
        Assert.Equal(committed ? 1 : 0, await context.Notifications.CountAsync(row => row.Kind == NotificationKind.CommentDraft, Ct));
        var recovered = await BuildAsync(true, steps: new CommentProbeSteps(pg.Db, id, source: true), fastRecovery: true);
        _hosts.Add(recovered);
        await recovered.StartAsync(Ct);
        var draft = await recovered.Services.GetRequiredService<CommentSources>().WaitAsync(id, Ct);
        await UntilAsync(async () =>
        {
            await using var read = await pg.Db.CreateDbContextAsync(Ct);
            return await read.Database.SqlQuery<int>($"SELECT count(*)::int AS \"Value\" FROM jobfinder_messages.wolverine_incoming_envelopes WHERE status = 'Incoming'").SingleAsync(Ct) == 0
                && await read.Database.SqlQuery<int>($"SELECT count(*)::int AS \"Value\" FROM jobfinder_messages.wolverine_outgoing_envelopes").SingleAsync(Ct) == 0;
        });
        Assert.Equal(id, draft!.SourceRequestId);
        Assert.Equal(CommentStatus.Pending, draft.Status);
        Assert.Equal(1, await CallsAsync(id, CommentPreparationStage.Drafting));
        Assert.Equal(1, await CallsAsync(id, CommentPreparationStage.Checking));
        Assert.Equal(1, await context.CommentPreparationRequests.CountAsync(Ct));
        Assert.Equal(1, await context.CommentDrafts.CountAsync(Ct));
        Assert.Equal(1, await context.Notifications.CountAsync(row => row.Kind == NotificationKind.CommentDraft, Ct));
        Assert.Empty(await context.DeliveryAttempts.ToListAsync(Ct));
    }
}
