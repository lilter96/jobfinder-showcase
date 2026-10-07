using System.Text.Json;
using JobFinder.Core.Domain;
using JobFinder.Core.Profile;
using JobFinder.Core.Text;
using JobFinder.Data;
using JobFinder.Llm.Client;
using JobFinder.Llm.Tasks;
using JobFinder.Pipeline;
using JobFinder.Pipeline.Career;
using JobFinder.Pipeline.Modules.Candidate;
using JobFinder.Pipeline.Modules.Preparation;
using JobFinder.Pipeline.Notifications;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace JobFinder.Tests.Pipeline;

public sealed class DurableMockPreparationTests(PostgresFixture pg) : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private readonly List<IHost> _hosts = [];
    public async ValueTask InitializeAsync()
    {
        Assert.SkipWhen(pg.SkipReason is not null, pg.SkipReason ?? "");
        await pg.SeedPoliciesAsync();
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        await context.MockPreparationRequests.ExecuteDeleteAsync(Ct);
        await context.MockSessionRevisions.ExecuteDeleteAsync(Ct);
        await context.MockSessions.ExecuteDeleteAsync(Ct);
        await context.LearningProgressRevisions.ExecuteDeleteAsync(Ct);
        await context.Documents.Where(row => row.Key.StartsWith("candidate.")).ExecuteDeleteAsync(Ct);
        await context.CandidateRevisions.ExecuteDeleteAsync(Ct);
        await context.Settings.Where(row => row.Key == ConversationStore.Key || row.Key.StartsWith("hr.") || row.Key.StartsWith("interview.")).ExecuteDeleteAsync(Ct);
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
            services.RemoveAll<IMockPreparationSteps>();
            services.AddSingleton<IMockPreparationSteps>(steps);
            if (!consumer) services.AddHostedService<SingleInstanceGuard>();
        });
        _hosts.Add(host);
        await host.StartAsync(Ct);
        if (seed)
        {
            await host.Services.GetRequiredService<ICandidatePublisher>().PublishProfileAsync(new CandidateProfile { Name = "Synthetic Candidate", Reviewed = false }, 0, "synthetic", Ct);
            await using var context = await pg.Db.CreateDbContextAsync(Ct);
            await NotificationAdmissionAssertions.ResetAsync(context, Ct);
        }
        return host;
    }
    private static Task<ConversationState?> StateAsync(IHost host) => host.Services.GetRequiredService<ConversationStore>().GetAsync(Ct);
    private async Task<MockPreparationRequest> RootAsync(Guid id)
    {
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        return await context.MockPreparationRequests.AsNoTracking().SingleAsync(row => row.Id == id, Ct);
    }
    private async Task<MockSession> SourceAsync(long id)
    {
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        return await context.MockSessions.AsNoTracking().SingleAsync(row => row.Id == id, Ct);
    }
    private static async Task<Guid> BeginAsync(IHost host)
    {
        await host.Services.GetRequiredService<MockInterviews>().StartAsync(InterviewKind.Technical, null, Ct);
        return (await StateAsync(host))!.MockRequestId!.Value;
    }
    private static async Task RunAsync(IHost host, Guid id)
    {
        var workflow = host.Services.GetRequiredService<MockPreparationWorkflow>();
        await workflow.PrepareAsync(new() { RequestId = id }, Guid.NewGuid(), Ct);
        await workflow.PublishAsync(new() { RequestId = id }, Guid.NewGuid(), Ct);
    }
    private async Task DueAsync(Guid id)
    {
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        await context.MockPreparationRequests.Where(row => row.Id == id).ExecuteUpdateAsync(update => update.SetProperty(row => row.RetryAt, (DateTimeOffset?)null), Ct);
    }

    [Fact]
    public async Task Source_questions_answer_review_last_turn_and_child_summary_checkpoint_and_publish_without_inline_calls()
    {
        var steps = new Steps();
        var host = await HostAsync(steps);
        var questions = await BeginAsync(host);
        Assert.Equal(0, steps.Calls);
        Assert.DoesNotContain("synthetic-key", (await RootAsync(questions)).InputJson);
        await RunAsync(host, questions);
        var state = (await StateAsync(host))!;
        Assert.Null(state.MockPending);
        Assert.Equal(2, (await SourceAsync(state.EntityId)).Revision);
        for (var index = 0; index < 2; index++)
        {
            var captured = state;
            var results = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => host.Services.GetRequiredService<MockSources>().AnswerAsync(captured, "Captured answer " + index, Ct)));
            Assert.All(results, result => Assert.True(result.Result.Ok));
            Assert.Single(results.Select(result => result.RequestId).Distinct());
            var review = results[0].RequestId!.Value;
            var raw = await SourceAsync(state.EntityId);
            Assert.Equal("Captured answer " + index, ConversationStore.Turns(raw.TurnsJson)[index].Answer);
            Assert.Null(ConversationStore.Turns(raw.TurnsJson)[index].Score);
            await RunAsync(host, review);
            state = (await StateAsync(host))!;
        }
        Assert.True(state.MockPending);
        var summary = state.MockRequestId!.Value;
        Assert.Equal(MockPreparationKind.Summary, MockPreparationRequests.ReadInput(await RootAsync(summary)).Kind);
        await RunAsync(host, summary);
        Assert.Null(await StateAsync(host));
        var source = await SourceAsync(state.EntityId);
        Assert.Equal(MockStatus.Finished, source.Status);
        Assert.Contains("Synthetic summary", source.Summary);
        Assert.Equal(4, steps.Calls);
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        Assert.Equal(7, await context.MockSessionRevisions.CountAsync(Ct));
        Assert.Equal(4, await context.MockPreparationRequests.CountAsync(Ct));
        foreach (var notice in await context.Notifications.ToListAsync(Ct)) await NotificationAdmissionAssertions.JoinedAsync(context, notice, Ct);
        Assert.Empty(await context.DeliveryAttempts.ToListAsync(Ct));
    }

    [Fact]
    public async Task Prepared_hr_rehearsal_uses_native_source_cards_and_frozen_learning_before_review()
    {
        var steps = new Steps();
        var host = await HostAsync(steps);
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        context.Settings.Add(new() { Key = HrPlaybookService.LatestKey, Value = """{"items":[{"item":{"q":{"id":"first","question":"Synthetic prepared question"},"a":{"start":"Prepared real speech"}}},{"item":{"q":{"id":"second","question":"Synthetic next question"},"a":{"start":"Second real speech"}}}]}""", UpdatedAt = DateTimeOffset.UtcNow });
        await context.SaveChangesAsync(Ct);
        await host.Services.GetRequiredService<MockInterviews>().RehearseHrAsync(Ct);
        Assert.Equal(0, steps.Calls);
        var state = (await StateAsync(host))!;
        var admission = await host.Services.GetRequiredService<MockSources>().AnswerAsync(state, "Captured speech", Ct);
        var learning = host.Services.GetRequiredService<LearningProgressSources>();
        Assert.True((await learning.MarkAsync((await learning.ReadAsync("hr", Ct)).Item("first"), "again", Ct)).Result.Ok);
        await RunAsync(host, admission.RequestId!.Value);
        Assert.Equal("again", (await learning.ReadAsync("hr", Ct)).Learned["first"]);
        var source = await SourceAsync(state.EntityId);
        Assert.Contains("не перезаписана", ConversationStore.Turns(source.TurnsJson)[0].Feedback);
        Assert.Equal(1, steps.Calls);
    }

    [Theory]
    [InlineData("questions")]
    [InlineData("answer")]
    public async Task Admission_failure_rolls_back_source_raw_answer_history_dialogue_and_native_root(string action)
    {
        var steps = new Steps();
        var fault = new Fault();
        var host = await HostAsync(steps, fault: fault);
        var owner = host.Services.GetRequiredService<MockSources>();
        MockStartSnapshot? start = null;
        ConversationState? state = null;
        if (action == "questions") start = await owner.CaptureStartAsync(InterviewKind.Technical, null, Ct);
        else { await RunAsync(host, await BeginAsync(host)); state = await StateAsync(host); }
        var before = state is null ? null : MockSources.Hash(MockSessionSnapshot.Capture(await SourceAsync(state.EntityId)));
        fault.Admission = true;
        if (start is not null) await Assert.ThrowsAsync<IOException>(() => owner.BeginAsync(start, Ct));
        else await Assert.ThrowsAsync<IOException>(() => owner.AnswerAsync(state!, "Captured answer", Ct));
        Assert.Equal(state is null ? null : ConversationStore.Capture(state), (await StateAsync(host)) is { } current ? ConversationStore.Capture(current) : null);
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        if (state is null) { Assert.Empty(await context.MockSessions.ToListAsync(Ct)); Assert.Empty(await context.MockPreparationRequests.ToListAsync(Ct)); }
        else Assert.Equal(before, MockSources.Hash(MockSessionSnapshot.Capture(await SourceAsync(state.EntityId))));
        fault.Admission = false;
        var retried = start is not null ? await owner.BeginAsync(start, Ct) : await owner.AnswerAsync(state!, "Captured answer", Ct);
        Assert.True(retried.Result.Ok);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Paid_result_save_retries_or_retains_a_quarantined_result_without_paid_replay(bool exhaust)
    {
        var fault = new Fault();
        var steps = new Steps();
        var host = await HostAsync(steps, fault: fault);
        var id = await BeginAsync(host);
        fault.Checkpoints = exhaust ? 3 : 1;
        if (exhaust) await Assert.ThrowsAsync<MockPreparationCheckpointException>(() => host.Services.GetRequiredService<MockPreparationWorkflow>().PrepareAsync(new() { RequestId = id }, Guid.NewGuid(), Ct));
        else await host.Services.GetRequiredService<MockPreparationWorkflow>().PrepareAsync(new() { RequestId = id }, Guid.NewGuid(), Ct);
        Assert.Equal(1, steps.Calls);
        var root = await RootAsync(id);
        Assert.Equal(exhaust ? MockPreparationStage.Failed : MockPreparationStage.Publishing, root.Stage);
        Assert.NotNull(root.ResultJson);
        MockPreparationRequests.ReadResult(root);
        await host.Services.GetRequiredService<MockPreparationWorkflow>().PrepareAsync(new() { RequestId = id }, Guid.NewGuid(), Ct);
        Assert.Equal(1, steps.Calls);
    }

    [Fact]
    public async Task Publication_failure_resumes_saved_result_without_repeating_preparation()
    {
        var steps = new Steps();
        var fault = new Fault();
        var host = await HostAsync(steps, fault: fault);
        var id = await BeginAsync(host);
        var workflow = host.Services.GetRequiredService<MockPreparationWorkflow>();
        await workflow.PrepareAsync(new() { RequestId = id }, Guid.NewGuid(), Ct);
        var waiting = (await StateAsync(host))!;
        fault.Completion = true;
        await workflow.PublishAsync(new() { RequestId = id }, Guid.NewGuid(), Ct);
        Assert.Equal(MockPreparationStage.Publishing, (await RootAsync(id)).Stage);
        Assert.Equal(ConversationStore.Capture(waiting), ConversationStore.Capture((await StateAsync(host))!));
        fault.Completion = false;
        await DueAsync(id);
        await workflow.PublishAsync(new() { RequestId = id }, Guid.NewGuid(), Ct);
        Assert.Equal(1, steps.Calls);
        Assert.Equal(MockPreparationStage.Completed, (await RootAsync(id)).Stage);
    }

    [Fact]
    public async Task Llm_outage_retains_raw_answer_and_resumes_review_with_the_original_learning_witness()
    {
        var steps = new Steps();
        var host = await HostAsync(steps);
        await RunAsync(host, await BeginAsync(host));
        var state = (await StateAsync(host))!;
        var admission = await host.Services.GetRequiredService<MockSources>().AnswerAsync(state, "Captured answer", Ct);
        steps.Unavailable = true;
        await host.Services.GetRequiredService<MockPreparationWorkflow>().PrepareAsync(new() { RequestId = admission.RequestId!.Value }, Guid.NewGuid(), Ct);
        var root = await RootAsync(admission.RequestId!.Value);
        Assert.Equal(MockPreparationStage.Preparing, root.Stage);
        Assert.Equal(0, root.Failures);
        Assert.NotNull(root.RetryAt);
        Assert.Equal("Captured answer", MockPreparationRequests.ReadInput(root).Answer);
        steps.Unavailable = false;
        await DueAsync(root.Id);
        await RunAsync(host, root.Id);
        Assert.Equal(1, (await SourceAsync(state.EntityId)).Current);
    }

    [Fact]
    public async Task Stop_cancels_only_the_captured_pending_root_and_stale_buttons_cannot_close_a_new_session()
    {
        var steps = new Steps();
        var host = await HostAsync(steps);
        var old = await BeginAsync(host);
        var oldState = (await StateAsync(host))!;
        await host.Services.GetRequiredService<MockInterviews>().StopAsync(oldState.EntityId, 0, Ct);
        Assert.Equal(MockPreparationStage.Preparing, (await RootAsync(old)).Stage);
        await host.Services.GetRequiredService<MockInterviews>().StopAsync(oldState.EntityId, 1, Ct);
        Assert.Equal(MockPreparationStage.Cancelled, (await RootAsync(old)).Stage);
        Assert.Null(await StateAsync(host));
        var newer = await BeginAsync(host);
        var newState = (await StateAsync(host))!;
        await host.Services.GetRequiredService<MockInterviews>().StopAsync(oldState.EntityId, 1, Ct);
        Assert.Equal(ConversationStore.Capture(newState), ConversationStore.Capture((await StateAsync(host))!));
        await RunAsync(host, old);
        Assert.Equal(0, steps.Calls);
        Assert.Equal(MockPreparationStage.Preparing, (await RootAsync(newer)).Stage);
    }

    [Fact]
    public async Task Stop_after_scored_answer_admits_one_summary_and_does_not_discard_unreviewed_raw_answer()
    {
        var steps = new Steps();
        var host = await HostAsync(steps);
        await RunAsync(host, await BeginAsync(host));
        var owner = host.Services.GetRequiredService<MockSources>();
        var answer = await owner.AnswerAsync((await StateAsync(host))!, "First answer", Ct);
        await RunAsync(host, answer.RequestId!.Value);
        var next = await owner.AnswerAsync((await StateAsync(host))!, "Second raw answer", Ct);
        var stopped = await owner.FinishAsync((await StateAsync(host))!, Ct);
        Assert.Equal(MockPreparationStage.Cancelled, (await RootAsync(next.RequestId!.Value)).Stage);
        Assert.Equal(stopped.RequestId, (await owner.FinishAsync((await StateAsync(host))!, Ct)).RequestId);
        await RunAsync(host, stopped.RequestId!.Value);
        var turns = ConversationStore.Turns((await SourceAsync((await RootAsync(stopped.RequestId.Value)).SessionId)).TurnsJson);
        Assert.Equal("Second raw answer", turns[1].Answer);
        Assert.Null(turns[1].Score);
        Assert.Equal(3, steps.Calls);
    }

    [Theory]
    [InlineData("candidate")]
    [InlineData("dialogue")]
    public async Task Changed_capture_refuses_late_publication_and_preserves_a_newer_dialogue(string changed)
    {
        var steps = new Steps();
        var host = await HostAsync(steps);
        var id = await BeginAsync(host);
        if (changed == "candidate")
            await host.Services.GetRequiredService<ICandidatePublisher>().PublishProfileAsync(new CandidateProfile { Name = "New Candidate" }, 1, "synthetic", Ct);
        else await host.Services.GetRequiredService<ConversationStore>().SetAsync(new("different", 77, 0, "New owner intent", [], null), Ct);
        var current = await StateAsync(host);
        await RunAsync(host, id);
        Assert.Equal(MockPreparationStage.Cancelled, (await RootAsync(id)).Stage);
        Assert.Equal(0, steps.Calls);
        Assert.Equal(ConversationStore.Capture(current!), ConversationStore.Capture((await StateAsync(host))!));
    }

    [Theory]
    [InlineData("version")]
    [InlineData("source")]
    [InlineData("waiting")]
    [InlineData("hash")]
    public async Task Damaged_checkpoint_quarantines_once_without_paid_work(string damage)
    {
        var steps = new Steps();
        var host = await HostAsync(steps);
        var id = await BeginAsync(host);
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        var root = await context.MockPreparationRequests.SingleAsync(Ct);
        var input = MockPreparationRequests.ReadInput(root);
        if (damage == "version") input.Version = 99;
        if (damage == "source") input.Source.Id++;
        if (damage == "waiting") input.Waiting.MockRequestId = Guid.NewGuid();
        root.InputJson = JsonSerializer.Serialize(input, MockPreparationJsonContext.Default.MockPreparationInput);
        root.InputHash = damage == "hash" ? "damaged" : CanonicalJson.Hash(root.InputJson);
        await context.SaveChangesAsync(Ct);
        await Assert.ThrowsAsync<UnsupportedMockPreparationException>(() => host.Services.GetRequiredService<MockPreparationWorkflow>().PrepareAsync(new() { RequestId = id }, Guid.NewGuid(), Ct));
        await host.Services.GetRequiredService<MockPreparationWorkflow>().PrepareAsync(new() { RequestId = id }, Guid.NewGuid(), Ct);
        Assert.Equal(MockPreparationStage.Failed, (await RootAsync(id)).Stage);
        Assert.Equal(0, steps.Calls);
    }

    [Fact]
    public async Task Native_listener_restart_publishes_saved_questions_without_a_second_paid_call()
    {
        var steps = new Steps();
        var producer = await HostAsync(steps);
        var id = await BeginAsync(producer);
        await producer.Services.GetRequiredService<MockPreparationWorkflow>().PrepareAsync(new() { RequestId = id }, Guid.NewGuid(), Ct);
        await producer.StopAsync(Ct);
        await HostAsync(steps, consumer: true, seed: false);
        var deadline = DateTimeOffset.UtcNow.AddSeconds(25);
        while ((await RootAsync(id)).Stage != MockPreparationStage.Completed && DateTimeOffset.UtcNow < deadline) await Task.Delay(100, Ct);
        Assert.Equal(MockPreparationStage.Completed, (await RootAsync(id)).Stage);
        Assert.Equal(1, steps.Calls);
    }

    [Fact]
    public async Task Migration_refuses_retained_source_and_native_roots()
    {
        var host = await HostAsync(new Steps());
        await BeginAsync(host);
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        var error = await Assert.ThrowsAsync<Npgsql.PostgresException>(() => MigrationTestRunner.DowngradeAndRestoreAsync(context, "20261004014358_OwnedLearningProgress", Ct));
        Assert.Contains("Cannot downgrade retained mock", error.MessageText);
        Assert.False(context.Database.HasPendingModelChanges());
    }

    private sealed class Steps : IMockPreparationSteps
    {
        public int Calls { get; private set; }
        public bool Unavailable { get; set; }
        public Func<MockPreparationInput, Task>? During { get; set; }
        public void EnsureCompatible(MockPreparationInput input) { }
        public async Task<MockPreparationResult> PrepareAsync(MockPreparationInput input, CancellationToken ct)
        {
            Calls++;
            if (Unavailable) throw new LlmUnavailableException("synthetic unavailable");
            if (During is not null) await During(input);
            return input.Kind switch
            {
                MockPreparationKind.Questions => new MockPreparationResult { Questions = [new() { Question = "Synthetic first question", Ideal = "Supported first answer" }, new() { Question = "Synthetic second question", Ideal = "Supported second answer" }] },
                MockPreparationKind.Review => new() { Review = new() { Score = 4, Good = ["Supported answer"], Tip = "Synthetic next step" } },
                _ => new() { Summary = new() { Score = 4, Verdict = "Synthetic summary", Strengths = ["Supported evidence"], Focus = ["Synthetic focus"] } },
            };
        }
    }

    [Fact]
    public async Task Last_review_and_child_summary_admission_roll_back_together_and_retry_does_not_repeat_review()
    {
        var steps = new Steps();
        var fault = new Fault();
        var host = await HostAsync(steps, fault: fault);
        await RunAsync(host, await BeginAsync(host));
        var owner = host.Services.GetRequiredService<MockSources>();
        var first = await owner.AnswerAsync((await StateAsync(host))!, "First answer", Ct);
        await RunAsync(host, first.RequestId!.Value);
        var last = await owner.AnswerAsync((await StateAsync(host))!, "Last answer", Ct);
        var workflow = host.Services.GetRequiredService<MockPreparationWorkflow>();
        await workflow.PrepareAsync(new() { RequestId = last.RequestId!.Value }, Guid.NewGuid(), Ct);
        fault.Admission = true;
        await workflow.PublishAsync(new() { RequestId = last.RequestId.Value }, Guid.NewGuid(), Ct);
        Assert.Equal(1, (await SourceAsync((await RootAsync(last.RequestId.Value)).SessionId)).Current);
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        Assert.Equal(3, await context.MockPreparationRequests.CountAsync(Ct));
        fault.Admission = false;
        await DueAsync(last.RequestId.Value);
        await workflow.PublishAsync(new() { RequestId = last.RequestId.Value }, Guid.NewGuid(), Ct);
        Assert.Equal(3, steps.Calls);
        Assert.Equal(4, await context.MockPreparationRequests.CountAsync(Ct));
        await RunAsync(host, (await StateAsync(host))!.MockRequestId!.Value);
        Assert.Equal(4, steps.Calls);
        Assert.Null(await StateAsync(host));
    }

    [Fact]
    public async Task Late_paid_questions_retain_the_result_but_cannot_overwrite_a_changed_candidate_or_dialogue()
    {
        var steps = new Steps();
        var host = await HostAsync(steps);
        var id = await BeginAsync(host);
        steps.During = async _ => await host.Services.GetRequiredService<ICandidatePublisher>().PublishProfileAsync(new CandidateProfile { Name = "Changed after paid admission" }, 1, "synthetic", Ct);
        await RunAsync(host, id);
        var root = await RootAsync(id);
        Assert.Equal(MockPreparationStage.Cancelled, root.Stage);
        Assert.Equal(1, steps.Calls);
        Assert.NotNull(root.ResultHash);
        MockPreparationRequests.ReadResult(root);
        Assert.True((await StateAsync(host))!.MockPending);
    }

    [Fact]
    public async Task Review_source_learning_mark_and_history_roll_back_on_native_card_admission_failure()
    {
        var steps = new Steps();
        var fault = new Fault();
        var host = await HostAsync(steps, fault: fault);
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        context.Settings.Add(new() { Key = HrPlaybookService.LatestKey, Value = """{"items":[{"item":{"q":{"id":"first","question":"Prepared first"},"a":{"start":"Prepared speech"}}},{"item":{"q":{"id":"second","question":"Prepared second"},"a":{"start":"Second prepared speech"}}}]}""", UpdatedAt = DateTimeOffset.UtcNow });
        await context.SaveChangesAsync(Ct);
        await host.Services.GetRequiredService<MockInterviews>().RehearseHrAsync(Ct);
        var admission = await host.Services.GetRequiredService<MockSources>().AnswerAsync((await StateAsync(host))!, "Captured answer", Ct);
        var workflow = host.Services.GetRequiredService<MockPreparationWorkflow>();
        await workflow.PrepareAsync(new() { RequestId = admission.RequestId!.Value }, Guid.NewGuid(), Ct);
        fault.Completion = true;
        await workflow.PublishAsync(new() { RequestId = admission.RequestId.Value }, Guid.NewGuid(), Ct);
        var learning = host.Services.GetRequiredService<LearningProgressSources>();
        Assert.Empty((await learning.ReadAsync("hr", Ct)).Learned);
        Assert.Empty(await context.LearningProgressRevisions.ToListAsync(Ct));
        Assert.Equal(0, (await SourceAsync((await RootAsync(admission.RequestId.Value)).SessionId)).Current);
        fault.Completion = false;
        await DueAsync(admission.RequestId.Value);
        await workflow.PublishAsync(new() { RequestId = admission.RequestId.Value }, Guid.NewGuid(), Ct);
        Assert.Equal("known", (await learning.ReadAsync("hr", Ct)).Learned["first"]);
        Assert.Equal(1, steps.Calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Downgrade_refuses_orphaned_future_native_commands_without_a_source_row(bool scheduled)
    {
        var steps = new Steps();
        var host = await HostAsync(steps);
        var id = await BeginAsync(host);
        if (scheduled)
        {
            steps.Unavailable = true;
            await host.Services.GetRequiredService<MockPreparationWorkflow>().PrepareAsync(new() { RequestId = id }, Guid.NewGuid(), Ct);
        }
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        await context.MockPreparationRequests.ExecuteDeleteAsync(Ct);
        await context.MockSessionRevisions.ExecuteDeleteAsync(Ct);
        await context.MockSessions.ExecuteDeleteAsync(Ct);
        await context.Settings.Where(row => row.Key == ConversationStore.Key).ExecuteDeleteAsync(Ct);
        if (scheduled) await context.Database.ExecuteSqlRawAsync("TRUNCATE jobfinder_queues.wolverine_queue_preparation; UPDATE jobfinder_queues.wolverine_queue_preparation_scheduled SET message_type='jobfinder.preparation.prepare-mock.v99'", Ct);
        else await context.Database.ExecuteSqlRawAsync("UPDATE jobfinder_queues.wolverine_queue_preparation SET message_type='jobfinder.preparation.prepare-mock.v99'", Ct);
        var error = await Assert.ThrowsAsync<Npgsql.PostgresException>(() => MigrationTestRunner.DowngradeAndRestoreAsync(context, "20261004014358_OwnedLearningProgress", Ct));
        Assert.Contains("Cannot downgrade retained mock preparation envelopes", error.MessageText);
    }
    private sealed class Fault : SaveChangesInterceptor
    {
        public bool Admission { get; set; }
        public bool Completion { get; set; }
        public int Checkpoints { get; set; }
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (eventData.Context is JobFinderDbContext context)
            {
                var roots = context.ChangeTracker.Entries<MockPreparationRequest>();
                if (Admission && roots.Any(entry => entry.State == EntityState.Added)) throw new IOException("synthetic admission fault");
                if (Completion && roots.Any(entry => entry.State == EntityState.Modified && entry.Entity.Stage == MockPreparationStage.Completed)) throw new IOException("synthetic publication fault");
                if (Checkpoints > 0 && roots.Any(entry => entry.State == EntityState.Modified && entry.Entity.Stage == MockPreparationStage.Publishing && entry.Entity.ResultJson is not null))
                { Checkpoints--; throw new IOException("synthetic checkpoint fault"); }
            }
            return ValueTask.FromResult(result);
        }
    }

    [Fact]
    public async Task Captured_prepared_rehearsal_refuses_changed_or_duplicate_speech_before_source_publication()
    {
        var host = await HostAsync(new Steps());
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        context.Settings.Add(new() { Key = HrPlaybookService.LatestKey, Value = """{"items":[{"item":{"q":{"id":"first","question":"Original question"},"a":{"start":"Original prepared speech"}}}]}""", UpdatedAt = DateTimeOffset.UtcNow });
        await context.SaveChangesAsync(Ct);
        var owner = host.Services.GetRequiredService<MockSources>();
        var start = await owner.CaptureStartAsync(InterviewKind.HrScreen, null, Ct, hr: true);
        start.Turns[0].Ideal = "Uncaptured replacement speech";
        Assert.False((await owner.BeginAsync(start, Ct)).Result.Ok);
        Assert.Empty(await context.MockSessions.ToListAsync(Ct));
        start = await owner.CaptureStartAsync(InterviewKind.HrScreen, null, Ct, hr: true);
        start.Turns.Add(start.Turns[0] with { });
        Assert.False((await owner.BeginAsync(start, Ct)).Result.Ok);
        Assert.Null(await StateAsync(host));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Prepared_interview_rehearsal_keeps_vacancy_and_kind_and_refuses_a_changed_pack_revision(bool changed)
    {
        var steps = new Steps();
        var host = await HostAsync(steps);
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        var vacancy = new Vacancy { Title = "Synthetic backend role", CompanyName = "Synthetic interview company", Url = "https://synthetic.invalid/role", Fingerprint = "synthetic-interview", FirstSeenAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow };
        var application = new Application { Vacancy = vacancy, Status = ApplicationStatus.Sent, CreatedAt = DateTimeOffset.UtcNow };
        var pack = new InterviewCopilot { Version = InterviewCopilot.CurrentVersion, Preparation = [new() { Q = new() { Id = "first", Question = "Original interview question" }, A = new() { Start = "Original prepared interview speech" } }] };
        var interview = new Interview { Application = application, Kind = InterviewKind.SystemDesign, PackJson = JsonSerializer.Serialize(pack, InterviewJsonContext.Default.InterviewCopilot) };
        context.Interviews.Add(interview);
        await context.SaveChangesAsync(Ct);
        var owner = host.Services.GetRequiredService<MockSources>();
        var captured = await owner.CaptureStartAsync(InterviewKind.HrScreen, null, Ct, interview.Id);
        if (changed) await context.Interviews.Where(row => row.Id == interview.Id).ExecuteUpdateAsync(update => update.SetProperty(row => row.PreparationRevision, 1L), Ct);
        var result = await owner.BeginAsync(captured, Ct);
        Assert.Equal(!changed, result.Result.Ok);
        if (!changed)
        {
            var source = await SourceAsync((await StateAsync(host))!.EntityId);
            Assert.Equal(InterviewKind.SystemDesign, source.Kind);
            Assert.Equal(vacancy.Id, source.VacancyId);
            var turn = Assert.Single(ConversationStore.Turns(source.TurnsJson));
            Assert.Equal(InterviewProgress.ItemKey(InterviewProgress.Scope(interview.Id), "first"), turn.ItemKey);
            Assert.Equal("Original prepared interview speech", turn.Ideal);
            Assert.Contains(await context.Notifications.ToListAsync(Ct), notice => notice.Text.Contains(vacancy.Title, StringComparison.Ordinal));
        }
        else Assert.Null(await StateAsync(host));
        Assert.Equal(0, steps.Calls);
    }
}
