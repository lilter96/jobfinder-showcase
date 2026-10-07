using JobFinder.Core.Domain;
using JobFinder.Core.Profile;
using JobFinder.Data;
using JobFinder.Llm.Client;
using JobFinder.Pipeline;
using JobFinder.Pipeline.Career;
using JobFinder.Pipeline.Modules.Candidate;
using JobFinder.Pipeline.Notifications;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace JobFinder.Tests.Pipeline;

public sealed class DurableStoryPreparationTests(PostgresFixture pg) : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private readonly List<IHost> _hosts = [];
    public async ValueTask InitializeAsync()
    {
        Assert.SkipWhen(pg.SkipReason is not null, pg.SkipReason ?? "");
        await pg.SeedPoliciesAsync();
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        await context.StoryRevisions.ExecuteDeleteAsync(Ct);
        await context.Stories.IgnoreQueryFilters().ExecuteDeleteAsync(Ct);
        await context.StoryPreparationRequests.ExecuteDeleteAsync(Ct);
        await context.Documents.Where(row => row.Key.StartsWith("candidate.")).ExecuteDeleteAsync(Ct);
        await context.CandidateRevisions.ExecuteDeleteAsync(Ct);
        await context.Settings.Where(row => row.Key == ConversationStore.Key || row.Key == StorySources.SkippedKey).ExecuteDeleteAsync(Ct);
        await NotificationAdmissionAssertions.ResetAsync(context, Ct);
        await context.Database.ExecuteSqlRawAsync("TRUNCATE jobfinder_queues.wolverine_queue_preparation, jobfinder_queues.wolverine_queue_preparation_scheduled; DELETE FROM jobfinder_messages.wolverine_incoming_envelopes; DELETE FROM jobfinder_messages.wolverine_outgoing_envelopes; DELETE FROM jobfinder_messages.wolverine_dead_letters", Ct);
    }
    public async ValueTask DisposeAsync()
    {
        foreach (var host in _hosts.AsEnumerable().Reverse())
        {
            try { await host.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(20)); }
            finally { host.Dispose(); }
        }
    }
    private async Task<IHost> HostAsync(Steps steps, bool consumer = false, Fault? fault = null, bool seed = true)
    {
        var host = await PostProcessingTestHost.BuildAsync(pg, consumer, fault: fault, configure: services =>
        {
            services.RemoveAll<IStoryPreparationSteps>();
            services.AddSingleton<IStoryPreparationSteps>(steps);
            if (!consumer) services.AddHostedService<SingleInstanceGuard>();
        });
        _hosts.Add(host);
        await host.StartAsync(Ct);
        if (seed)
        {
            var publisher = host.Services.GetRequiredService<ICandidatePublisher>();
            await publisher.PublishProfileAsync(new CandidateProfile { Name = "Synthetic Candidate", Reviewed = true,
                Summary = "Supported synthetic evidence", Achievements = ["Synthetic achievement"] }, 0, "synthetic", Ct);
            await publisher.PublishFactsAsync(new CandidateFacts(), 0, "synthetic", Ct);
            await using var context = await pg.Db.CreateDbContextAsync(Ct);
            await NotificationAdmissionAssertions.ResetAsync(context, Ct);
        }
        return host;
    }
    private static Task<ConversationState?> StateAsync(IHost host) => host.Services.GetRequiredService<ConversationStore>().GetAsync(Ct);

    [Theory]
    [InlineData(MockInterviews.Mode)]
    [InlineData(OfferAdvisor.Mode)]
    [InlineData(CareerChat.TimeMode)]
    public async Task Explicit_story_start_refuses_another_owner_without_replacing_its_pending_dialogue(string mode)
    {
        var steps = new Steps();
        var host = await HostAsync(steps);
        var store = host.Services.GetRequiredService<ConversationStore>();
        var previous = new ConversationState(mode, 12, 3, "Original question", ["Raw answer"], "Original draft")
        {
            MockRequestId = Guid.NewGuid(), MockPending = true,
        };
        await store.SetAsync(previous, Ct);
        var before = ConversationStore.Capture((await StateAsync(host))!);
        Assert.NotEmpty((await host.Services.GetRequiredService<StoryBank>().StartAsync(Ct)).Text);
        Assert.Equal(before, ConversationStore.Capture((await StateAsync(host))!));
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        Assert.Empty(await context.StoryPreparationRequests.ToListAsync(Ct));
        Assert.Empty(await context.Notifications.ToListAsync(Ct));
    }
    private async Task<StoryPreparationRequest> RootAsync(Guid id)
    {
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        return await context.StoryPreparationRequests.AsNoTracking().SingleAsync(row => row.Id == id, Ct);
    }
    private async Task<ConversationState> ThreeAnswersAsync(IHost host)
    {
        var bank = host.Services.GetRequiredService<StoryBank>();
        Assert.Empty((await bank.StartAsync(Ct)).Text);
        for (var index = 0; index < 3; index++) Assert.Empty((await bank.AnswerAsync((await StateAsync(host))!, "Captured answer " + index, Ct)).Text);
        return (await StateAsync(host))!;
    }
    private async Task<Guid> RequestAsync(IHost host)
    {
        var before = await ThreeAnswersAsync(host);
        Assert.Empty((await host.Services.GetRequiredService<StoryBank>().AnswerAsync(before, "Captured reflection", Ct)).Text);
        return (await StateAsync(host))!.StoryRequestId!.Value;
    }
    private async Task ReadyAsync(IHost host, Guid id)
    {
        var workflow = host.Services.GetRequiredService<StoryPreparationWorkflow>();
        await workflow.DraftAsync(new() { RequestId = id }, Guid.NewGuid(), Ct);
        await workflow.PublishAsync(new() { RequestId = id }, Guid.NewGuid(), Ct);
    }

    [Fact]
    public async Task Questions_answers_fourth_answer_root_and_version_bound_cards_are_native_without_inline_paid_work()
    {
        var steps = new Steps();
        var host = await HostAsync(steps);
        var before = await ThreeAnswersAsync(host);
        var bank = host.Services.GetRequiredService<StoryBank>();
        var requests = host.Services.GetRequiredService<StoryPreparationRequests>();
        var ids = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => requests.RequestAsync(before, Ct, " Captured reflection ")));
        Assert.Single(ids.Distinct());
        Assert.NotNull(ids[0]);
        var waiting = (await StateAsync(host))!;
        var root = await RootAsync(ids[0]!.Value);
        var input = StoryPreparationRequests.ReadInput(root);
        Assert.Equal(4, waiting.Step);
        Assert.Equal("Captured reflection", input.Answer);
        Assert.Equal(4, waiting.Answers.Count);
        Assert.Equal(ConversationStore.Capture(waiting), ConversationStore.Capture(input.Waiting));
        Assert.DoesNotContain("synthetic-key", root.InputJson);
        Assert.Equal(0, steps.Calls);
        Assert.Contains("изменился", (await bank.AnswerAsync(before, "Stale different fourth answer", Ct)).Text);
        Assert.Contains("уже сохранена", (await host.Services.GetRequiredService<CareerChat>().HandleTextAsync("New text", Ct))!.Text);
        Assert.Contains("изменился", (await bank.SaveAsync(StoryBank.ControlId(waiting), StoryBank.Version(waiting), Ct)).Text);
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        var notices = await context.Notifications.OrderBy(row => row.Id).ToListAsync(Ct);
        Assert.Equal(5, notices.Count);
        foreach (var notice in notices) await NotificationAdmissionAssertions.JoinedAsync(context, notice, Ct);
        for (var index = 0; index < 4; index++) Assert.Contains($"Вопрос {index + 1}/4", notices[index].Text);
        Assert.Single(await context.StoryPreparationRequests.ToListAsync(Ct));
        Assert.Empty(await context.Stories.ToListAsync(Ct));
    }

    [Fact]
    public async Task Native_producer_before_consumer_restart_finishes_only_a_draft_until_explicit_versioned_confirmation()
    {
        var steps = new Steps();
        var producer = await HostAsync(steps);
        var id = await RequestAsync(producer);
        await producer.StopAsync(Ct);
        var consumer = await HostAsync(steps, consumer: true, seed: false);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(40));
        await consumer.Services.GetRequiredService<StoryPreparationRequests>().WaitAsync(id, timeout.Token);
        var ready = (await StateAsync(consumer))!;
        Assert.Null(ready.StoryPending);
        Assert.NotNull(ready.Draft);
        Assert.Equal(1, steps.Calls);
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        Assert.Empty(await context.Stories.ToListAsync(Ct));
        var card = await context.Notifications.SingleAsync(row => row.Text.Contains("<b>S</b>"), Ct);
        Assert.Contains("9999", card.Text);
        Assert.Contains("проверьте", card.Text);
        Assert.Contains(Callback.Button("✅ Сохранить", CareerCallback.StorySave, StoryBank.ControlId(ready), StoryBank.Version(ready)), card.Buttons.SelectMany(row => row));
        await NotificationAdmissionAssertions.JoinedAsync(context, card, Ct);
        var result = await consumer.Services.GetRequiredService<StoryBank>().SaveAsync(StoryBank.ControlId(ready), StoryBank.Version(ready), Ct);
        Assert.Contains("История сохранена", result.Text);
        var story = await context.Stories.SingleAsync(Ct);
        Assert.True(story.Confirmed);
        Assert.Equal(id, story.PreparationRequestId);
        Assert.Equal(string.Join("\n", ready.Answers), story.RawNotes);
        Assert.Single(await context.StoryRevisions.ToListAsync(Ct));
        Assert.Null(await StateAsync(consumer));
        Assert.Contains("изменился", (await consumer.Services.GetRequiredService<StoryBank>().SaveAsync(StoryBank.ControlId(ready), StoryBank.Version(ready), Ct)).Text);
        Assert.Equal(1, steps.Calls);
    }

    [Fact]
    public async Task Saved_paid_draft_can_publish_after_host_recreation_without_another_paid_call()
    {
        var steps = new Steps();
        var first = await HostAsync(steps);
        var id = await RequestAsync(first);
        await first.Services.GetRequiredService<StoryPreparationWorkflow>().DraftAsync(new() { RequestId = id }, Guid.NewGuid(), Ct);
        var checkpoint = await RootAsync(id);
        Assert.Equal(StoryPreparationStage.Publishing, checkpoint.Stage);
        Assert.True((await StateAsync(first))!.StoryPending);
        await first.StopAsync(Ct);
        var second = await HostAsync(steps, seed: false);
        await second.Services.GetRequiredService<StoryPreparationWorkflow>().PublishAsync(new() { RequestId = id }, Guid.NewGuid(), Ct);
        Assert.Equal(StoryPreparationStage.Completed, (await RootAsync(id)).Stage);
        Assert.Equal(checkpoint.ResultHash, (await RootAsync(id)).ResultHash);
        Assert.Equal(1, steps.Calls);
    }

    [Fact]
    public async Task Duplicate_claim_cannot_pay_twice_and_captured_stop_fences_a_late_draft()
    {
        var steps = new Steps { Release = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        var host = await HostAsync(steps);
        var id = await RequestAsync(host);
        var workflow = host.Services.GetRequiredService<StoryPreparationWorkflow>();
        var running = workflow.DraftAsync(new() { RequestId = id }, Guid.NewGuid(), Ct);
        try
        {
            await steps.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);
            await workflow.DraftAsync(new() { RequestId = id }, Guid.NewGuid(), Ct);
            Assert.Equal(1, steps.Calls);
            await host.Services.GetRequiredService<CareerChat>().StopAsync(Ct);
            Assert.Null(await StateAsync(host));
        }
        finally { steps.Release.TrySetResult(); }
        await running;
        Assert.Equal(StoryPreparationStage.Cancelled, (await RootAsync(id)).Stage);
        Assert.Equal(4, StoryPreparationRequests.ReadInput(await RootAsync(id)).Waiting.Answers.Count);
        await workflow.PublishAsync(new() { RequestId = id }, Guid.NewGuid(), Ct);
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        Assert.Empty(await context.Stories.ToListAsync(Ct));
        Assert.Empty(await context.Notifications.Where(row => row.Text.Contains("<b>S</b>")).ToListAsync(Ct));
    }

    [Theory]
    [InlineData("before-candidate")]
    [InlineData("during-candidate")]
    [InlineData("during-dialogue")]
    [InlineData("after-dialogue")]
    public async Task Candidate_or_dialogue_races_never_publish_or_replace_a_newer_source(string race)
    {
        var during = race.StartsWith("during", StringComparison.Ordinal);
        var steps = new Steps { Release = during ? new(TaskCreationOptions.RunContinuationsAsynchronously) : null };
        var host = await HostAsync(steps);
        var id = await RequestAsync(host);
        var workflow = host.Services.GetRequiredService<StoryPreparationWorkflow>();
        async Task Change()
        {
            if (race.EndsWith("candidate", StringComparison.Ordinal))
            {
                await using var context = await pg.Db.CreateDbContextAsync(Ct);
                await context.Documents.Where(row => row.Key == "candidate.profile").ExecuteUpdateAsync(setters => setters.SetProperty(row => row.Version, row => row.Version + 1), Ct);
            }
            else await host.Services.GetRequiredService<ConversationStore>().SetAsync(new("offer", 0, 0, null, [], "new unrelated draft"), Ct);
        }
        if (race == "before-candidate") await Change();
        var running = workflow.DraftAsync(new() { RequestId = id }, Guid.NewGuid(), Ct);
        try
        {
            if (during) { await steps.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct); await Change(); }
        }
        finally { steps.Release?.TrySetResult(); }
        await running;
        if (race == "after-dialogue") await Change();
        var before = (await StateAsync(host))!;
        await workflow.PublishAsync(new() { RequestId = id }, Guid.NewGuid(), Ct);
        Assert.Equal(StoryPreparationStage.Cancelled, (await RootAsync(id)).Stage);
        Assert.Equal(race == "before-candidate" ? 0 : 1, steps.Calls);
        Assert.Equal(ConversationStore.Capture(before), ConversationStore.Capture((await StateAsync(host))!));
        await using var final = await pg.Db.CreateDbContextAsync(Ct);
        Assert.Empty(await final.Stories.ToListAsync(Ct));
        Assert.Empty(await final.Notifications.Where(row => row.Text.Contains("<b>S</b>")).ToListAsync(Ct));
    }

    [Fact]
    public async Task Provider_outage_and_cancelled_wait_keep_the_exact_saved_answers_and_resume_the_same_stage()
    {
        var steps = new Steps { Unavailable = true };
        var host = await HostAsync(steps);
        var id = await RequestAsync(host);
        var workflow = host.Services.GetRequiredService<StoryPreparationWorkflow>();
        await workflow.DraftAsync(new() { RequestId = id }, Guid.NewGuid(), Ct);
        var paused = await RootAsync(id);
        Assert.Equal(StoryPreparationStage.Drafting, paused.Stage);
        Assert.NotNull(paused.RetryAt);
        Assert.Equal(0, paused.Failures);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => host.Services.GetRequiredService<StoryPreparationRequests>().WaitAsync(id, cancelled.Token));
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        await context.StoryPreparationRequests.Where(row => row.Id == id).ExecuteUpdateAsync(setters => setters.SetProperty(row => row.RetryAt, (DateTimeOffset?)null), Ct);
        steps.Unavailable = false;
        await ReadyAsync(host, id);
        Assert.Equal(paused.InputHash, (await RootAsync(id)).InputHash);
        Assert.Equal(2, steps.Calls);
    }

    [Theory]
    [InlineData("begin")]
    [InlineData("answer")]
    [InlineData("admission")]
    [InlineData("checkpoint")]
    [InlineData("publication")]
    [InlineData("confirmation")]
    public async Task Source_native_and_paid_boundaries_rollback_and_reuse_exact_inputs_without_repaying(string boundary)
    {
        var fault = new Fault(boundary);
        var steps = new Steps();
        var host = await HostAsync(steps, fault: fault);
        var bank = host.Services.GetRequiredService<StoryBank>();
        if (boundary == "begin")
        {
            fault.Armed = true;
            await Assert.ThrowsAsync<IOException>(() => bank.StartAsync(Ct));
            Assert.Null(await StateAsync(host));
        }
        if (boundary == "answer")
        {
            await bank.StartAsync(Ct);
            var original = (await StateAsync(host))!;
            fault.Armed = true;
            await Assert.ThrowsAsync<IOException>(() => bank.AnswerAsync(original, "raw typed answer", Ct));
            Assert.Equal(ConversationStore.Capture(original), ConversationStore.Capture((await StateAsync(host))!));
            await host.Services.GetRequiredService<CareerChat>().StopAsync(Ct);
        }
        var before = await ThreeAnswersAsync(host);
        fault.Armed = true;
        if (boundary == "admission")
        {
            await Assert.ThrowsAsync<IOException>(() => bank.AnswerAsync(before, "Captured reflection", Ct));
            Assert.Equal(ConversationStore.Capture(before), ConversationStore.Capture((await StateAsync(host))!));
            await using var context = await pg.Db.CreateDbContextAsync(Ct);
            Assert.Empty(await context.StoryPreparationRequests.ToListAsync(Ct));
        }
        await bank.AnswerAsync(before, "Captured reflection", Ct);
        var id = (await StateAsync(host))!.StoryRequestId!.Value;
        await ReadyAsync(host, id);
        if (boundary == "publication")
        {
            Assert.Equal(StoryPreparationStage.Publishing, (await RootAsync(id)).Stage);
            Assert.True((await StateAsync(host))!.StoryPending);
            await using var context = await pg.Db.CreateDbContextAsync(Ct);
            await context.StoryPreparationRequests.Where(row => row.Id == id).ExecuteUpdateAsync(setters => setters.SetProperty(row => row.RetryAt, (DateTimeOffset?)null), Ct);
            await host.Services.GetRequiredService<StoryPreparationWorkflow>().PublishAsync(new() { RequestId = id }, Guid.NewGuid(), Ct);
        }
        var ready = (await StateAsync(host))!;
        if (boundary == "confirmation")
        {
            await Assert.ThrowsAsync<IOException>(() => bank.SaveAsync(StoryBank.ControlId(ready), StoryBank.Version(ready), Ct));
            Assert.Equal(ConversationStore.Capture(ready), ConversationStore.Capture((await StateAsync(host))!));
            await using var context = await pg.Db.CreateDbContextAsync(Ct);
            Assert.Empty(await context.Stories.ToListAsync(Ct));
            Assert.Empty(await context.StoryRevisions.ToListAsync(Ct));
        }
        await bank.SaveAsync(StoryBank.ControlId(ready), StoryBank.Version(ready), Ct);
        Assert.Equal(1, fault.Failures);
        Assert.Equal(1, steps.Calls);
        await using var final = await pg.Db.CreateDbContextAsync(Ct);
        Assert.Single(await final.Stories.ToListAsync(Ct));
        Assert.Single(await final.StoryRevisions.ToListAsync(Ct));
        foreach (var notice in await final.Notifications.ToListAsync(Ct)) await NotificationAdmissionAssertions.JoinedAsync(final, notice, Ct);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Damaged_checkpoints_quarantine_once_and_explicit_stop_does_not_erase_failed_root_evidence(bool result)
    {
        var steps = new Steps();
        var host = await HostAsync(steps);
        var id = await RequestAsync(host);
        var workflow = host.Services.GetRequiredService<StoryPreparationWorkflow>();
        if (result) await workflow.DraftAsync(new() { RequestId = id }, Guid.NewGuid(), Ct);
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        if (result) await context.StoryPreparationRequests.Where(row => row.Id == id).ExecuteUpdateAsync(setters => setters.SetProperty(row => row.ResultJson, "{}"), Ct);
        else await context.StoryPreparationRequests.Where(row => row.Id == id).ExecuteUpdateAsync(setters => setters.SetProperty(row => row.InputJson, "{}"), Ct);
        Task Run() => result ? workflow.PublishAsync(new() { RequestId = id }, Guid.NewGuid(), Ct) : workflow.DraftAsync(new() { RequestId = id }, Guid.NewGuid(), Ct);
        await Assert.ThrowsAsync<UnsupportedStoryPreparationException>(Run);
        await Run();
        var failed = await RootAsync(id);
        Assert.Equal(StoryPreparationStage.Failed, failed.Stage);
        Assert.Equal(result ? 1 : 0, steps.Calls);
        await host.Services.GetRequiredService<CareerChat>().StopAsync(Ct);
        Assert.Null(await StateAsync(host));
        Assert.Equal(failed.InputJson, (await RootAsync(id)).InputJson);
        Assert.Equal(failed.ResultJson, (await RootAsync(id)).ResultJson);
        Assert.Equal(failed.Error, (await RootAsync(id)).Error);
        Assert.Equal(StoryPreparationStage.Failed, (await RootAsync(id)).Stage);
    }

    [Fact]
    public async Task Explicit_redo_retains_the_old_draft_but_requires_the_new_exact_root_for_confirmation()
    {
        var steps = new Steps();
        var host = await HostAsync(steps);
        var originalId = await RequestAsync(host);
        await ReadyAsync(host, originalId);
        var original = (await StateAsync(host))!;
        var bank = host.Services.GetRequiredService<StoryBank>();
        Assert.Empty((await bank.RedoAsync(StoryBank.ControlId(original), StoryBank.Version(original), Ct)).Text);
        var waiting = (await StateAsync(host))!;
        var replacementId = waiting.StoryRequestId!.Value;
        Assert.NotEqual(originalId, replacementId);
        Assert.Equal(original.Draft, waiting.Draft);
        Assert.True(waiting.StoryPending);
        Assert.Contains("изменился", (await bank.SaveAsync(StoryBank.ControlId(original), StoryBank.Version(original), Ct)).Text);
        Assert.Contains("изменился", (await bank.RedoAsync(StoryBank.ControlId(original), StoryBank.Version(original), Ct)).Text);
        await ReadyAsync(host, replacementId);
        var replacement = (await StateAsync(host))!;
        Assert.Contains("История сохранена", (await bank.SaveAsync(StoryBank.ControlId(replacement), StoryBank.Version(replacement), Ct)).Text);
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        Assert.Equal(replacementId, (await context.Stories.SingleAsync(Ct)).PreparationRequestId);
        Assert.Equal(2, steps.Calls);
        Assert.Equal(StoryPreparationStage.Completed, (await RootAsync(originalId)).Stage);
    }

    [Fact]
    public async Task Captured_skip_cancels_pending_paid_work_and_cannot_clear_the_next_achievement()
    {
        var steps = new Steps();
        var host = await HostAsync(steps);
        var publisher = host.Services.GetRequiredService<ICandidatePublisher>();
        await publisher.PublishProfileAsync(new CandidateProfile { Name = "Synthetic Candidate", Reviewed = true,
            Achievements = ["Synthetic achievement", "Second synthetic achievement"] }, 1, "synthetic", Ct);
        var id = await RequestAsync(host);
        var waiting = (await StateAsync(host))!;
        var bank = host.Services.GetRequiredService<StoryBank>();
        Assert.Empty((await bank.SkipAsync(StoryBank.ControlId(waiting), StoryBank.Version(waiting), Ct)).Text);
        var next = (await StateAsync(host))!;
        Assert.Equal("Second synthetic achievement", next.Subject);
        Assert.Equal(0, next.Step);
        Assert.Contains("изменился", (await bank.StopAsync(StoryBank.ControlId(waiting), StoryBank.Version(waiting), Ct)).Text);
        Assert.Equal(ConversationStore.Capture(next), ConversationStore.Capture((await StateAsync(host))!));
        Assert.Equal(StoryPreparationStage.Cancelled, (await RootAsync(id)).Stage);
        await host.Services.GetRequiredService<StoryPreparationWorkflow>().DraftAsync(new() { RequestId = id }, Guid.NewGuid(), Ct);
        Assert.Equal(0, steps.Calls);
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        Assert.Contains(waiting.Subject!, ConversationStore.Strings((await context.Settings.SingleAsync(row => row.Key == StorySources.SkippedKey, Ct)).Value));
        Assert.Empty(await context.Stories.ToListAsync(Ct));
        foreach (var notice in await context.Notifications.ToListAsync(Ct)) await NotificationAdmissionAssertions.JoinedAsync(context, notice, Ct);
    }

    [Fact]
    public async Task Interactive_story_collection_does_not_require_employer_ready_profile_approval()
    {
        var steps = new Steps();
        var host = await HostAsync(steps);
        await host.Services.GetRequiredService<ICandidatePublisher>().PublishProfileAsync(new CandidateProfile
        {
            Name = "Synthetic Candidate", Reviewed = false, Achievements = ["Synthetic achievement"],
        }, 1, "synthetic", Ct);
        var id = await RequestAsync(host);
        Assert.False(StoryPreparationRequests.ReadInput(await RootAsync(id)).Candidate.Profile.Reviewed);
        await ReadyAsync(host, id);
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        Assert.Empty(await context.Stories.ToListAsync(Ct));
        var ready = (await StateAsync(host))!;
        Assert.Contains("История сохранена", (await host.Services.GetRequiredService<StoryBank>().SaveAsync(StoryBank.ControlId(ready), StoryBank.Version(ready), Ct)).Text);
        Assert.True((await context.Stories.SingleAsync(Ct)).Confirmed);
        Assert.Equal(1, steps.Calls);
    }

    [Fact]
    public async Task Exhausted_paid_checkpoint_retains_the_draft_and_does_not_schedule_paid_rework()
    {
        var fault = new Fault("checkpoint") { Limit = 3 };
        var steps = new Steps();
        var host = await HostAsync(steps, fault: fault);
        var id = await RequestAsync(host);
        fault.Armed = true;
        var workflow = host.Services.GetRequiredService<StoryPreparationWorkflow>();
        await Assert.ThrowsAsync<StoryPreparationCheckpointException>(() => workflow.DraftAsync(new() { RequestId = id }, Guid.NewGuid(), Ct));
        var failed = await RootAsync(id);
        Assert.Equal(StoryPreparationStage.Failed, failed.Stage);
        Assert.Equal("9999 ms", StoryPreparationRequests.ReadResult(failed).Draft.Result);
        Assert.Equal(3, fault.Failures);
        await workflow.DraftAsync(new() { RequestId = id }, Guid.NewGuid(), Ct);
        Assert.Equal(1, steps.Calls);
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        Assert.Empty(await context.Stories.ToListAsync(Ct));
        Assert.Single(await context.Notifications.Where(row => row.Kind == NotificationKind.Alert).ToListAsync(Ct));
    }

    [Theory]
    [InlineData("root")]
    [InlineData("queue")]
    [InlineData("scheduled")]
    [InlineData("incoming")]
    [InlineData("dead-letter")]
    [InlineData("source")]
    public async Task Downgrade_refuses_native_roots_source_markers_and_orphaned_future_story_commands(string evidence)
    {
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        if (evidence is "root" or "source")
        {
            var host = await HostAsync(new Steps());
            var id = await RequestAsync(host);
            if (evidence == "source")
            {
                await ReadyAsync(host, id);
                var state = (await StateAsync(host))!;
                await host.Services.GetRequiredService<StoryBank>().SaveAsync(StoryBank.ControlId(state), StoryBank.Version(state), Ct);
                await context.StoryPreparationRequests.ExecuteDeleteAsync(Ct);
                await context.Database.ExecuteSqlRawAsync("TRUNCATE jobfinder_queues.wolverine_queue_preparation, jobfinder_queues.wolverine_queue_preparation_scheduled", Ct);
            }
        }
        else
        {
            var id = Guid.NewGuid();
            const string type = "jobfinder.candidate.publish-story.v99";
            if (evidence == "queue") await context.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO jobfinder_queues.wolverine_queue_preparation (id,body,message_type) VALUES ({id},{Array.Empty<byte>()},{type})", Ct);
            else if (evidence == "scheduled") await context.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO jobfinder_queues.wolverine_queue_preparation_scheduled (id,body,message_type,execution_time) VALUES ({id},{Array.Empty<byte>()},{type},{DateTimeOffset.UtcNow.AddDays(1)})", Ct);
            else if (evidence == "incoming") await context.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO jobfinder_messages.wolverine_incoming_envelopes (id,status,owner_id,body,message_type) VALUES ({id},'Scheduled',0,{Array.Empty<byte>()},{type})", Ct);
            else await context.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO jobfinder_messages.wolverine_dead_letters (id,body,message_type) VALUES ({id},{Array.Empty<byte>()},{type})", Ct);
        }
        var error = await Assert.ThrowsAsync<Npgsql.PostgresException>(() => MigrationTestRunner.DowngradeAndRestoreAsync(context, "20261003235353_DurableOfferPreparation", Ct));
        Assert.Contains("Cannot downgrade retained story preparation", error.MessageText);
        Assert.Contains("20261004002414_DurableStoryPreparation", await context.Database.GetAppliedMigrationsAsync(Ct));
        Assert.False(context.Database.HasPendingModelChanges());
    }

    private sealed class Steps : IStoryPreparationSteps
    {
        public int Calls;
        public bool Unavailable;
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource? Release;
        public void EnsureCompatible(StoryPreparationInput input) { }
        public async Task<StoryPreparationResult> DraftAsync(StoryPreparationInput input, CancellationToken ct)
        {
            Interlocked.Increment(ref Calls);
            Entered.TrySetResult();
            if (Release is not null) await Release.Task.WaitAsync(ct);
            if (Unavailable) throw new LlmUnavailableException("synthetic story outage");
            return new() { Draft = new() { Title = "Synthetic story", Situation = "Captured context", Task = "Captured task", Action = "Captured action",
                Result = "9999 ms", Reflection = "Captured reflection", Skills = ["C#"] } };
        }
    }
    private sealed class Fault(string boundary) : SaveChangesInterceptor
    {
        public bool Armed;
        public int Failures;
        public int Limit = 1;
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData data, InterceptionResult<int> result, CancellationToken ct = default)
        {
            var context = data.Context as JobFinderDbContext;
            if (Armed && Failures < Limit && context is not null && (boundary == "begin" && context.ChangeTracker.Entries<AppSetting>().Any(entry => entry.State == EntityState.Added && entry.Entity.Key == ConversationStore.Key)
                || boundary == "answer" && context.ChangeTracker.Entries<AppSetting>().Any(entry => entry.State == EntityState.Modified && entry.Entity.Key == ConversationStore.Key)
                || boundary == "admission" && context.ChangeTracker.Entries<StoryPreparationRequest>().Any(entry => entry.State == EntityState.Added)
                || boundary == "checkpoint" && context.ChangeTracker.Entries<StoryPreparationRequest>().Any(entry => entry.State == EntityState.Modified && entry.Entity.Stage == StoryPreparationStage.Publishing)
                || boundary == "publication" && context.ChangeTracker.Entries<StoryPreparationRequest>().Any(entry => entry.Entity.Stage == StoryPreparationStage.Publishing)
                    && context.ChangeTracker.Entries<AppSetting>().Any(entry => entry.State == EntityState.Modified && entry.Entity.Key == ConversationStore.Key)
                || boundary == "confirmation" && context.ChangeTracker.Entries<Story>().Any(entry => entry.State == EntityState.Added)))
            {
                Failures++;
                throw new IOException("synthetic story boundary fault");
            }
            return base.SavingChangesAsync(data, result, ct);
        }
    }
}
