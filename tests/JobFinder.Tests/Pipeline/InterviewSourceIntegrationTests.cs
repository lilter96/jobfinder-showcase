using System.Text.Json;
using JobFinder.Core.Domain;
using JobFinder.Core.Messaging;
using JobFinder.Core.Profile;
using JobFinder.Core.Text;
using JobFinder.Data;
using JobFinder.Pipeline.Career;
using JobFinder.Llm.Tasks;
using JobFinder.Pipeline.Modules.Candidate;
using JobFinder.Pipeline.Modules.Preparation;
using JobFinder.Pipeline.Profile;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Wolverine;
using Wolverine.Postgresql;
using Wolverine.EntityFrameworkCore;

namespace JobFinder.Tests.Pipeline;

public sealed partial class InterviewSourceIntegrationTests(PostgresFixture pg) : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private IHost? _host;

    public async ValueTask InitializeAsync()
    {
        Assert.SkipWhen(pg.SkipReason is not null, pg.SkipReason ?? "");
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        await context.InterviewPreparationRequests.ExecuteDeleteAsync(Ct);
        await context.ReplyProcessingRequests.ExecuteDeleteAsync(Ct);
        await context.Interviews.ExecuteDeleteAsync(Ct);
        await context.Applications.ExecuteDeleteAsync(Ct);
        await context.Vacancies.ExecuteDeleteAsync(Ct);
        await context.Documents.ExecuteDeleteAsync(Ct);
        await context.PolicyRevisions.ExecuteDeleteAsync(Ct);
        await context.CandidateRevisions.ExecuteDeleteAsync(Ct);
        await NotificationAdmissionAssertions.ResetAsync(context, Ct);
        await context.LlmCalls.ExecuteDeleteAsync(Ct);
        await context.Settings.Where(row => row.Key == ConversationStore.Key).ExecuteDeleteAsync(Ct);
        await context.Database.ExecuteSqlRawAsync("TRUNCATE jobfinder_queues.wolverine_queue_preparation, jobfinder_queues.wolverine_queue_preparation_scheduled; DELETE FROM jobfinder_messages.wolverine_incoming_envelopes; DELETE FROM jobfinder_messages.wolverine_outgoing_envelopes; DELETE FROM jobfinder_messages.wolverine_dead_letters", Ct);
        var publisher = await pg.CandidateAsync();
        await publisher.PublishProfileAsync(new CandidateProfile { Name = "Synthetic Candidate", Reviewed = true }, 0, "source-test", Ct);
        await publisher.PublishFactsAsync(new CandidateFacts { Facts = [new() { Key = "location", Question = "Location?", Answer = "Minsk", Confirmed = true }] }, 0, "source-test", Ct);
        await NotificationAdmissionAssertions.ResetAsync(context, Ct);
    }

    public async ValueTask DisposeAsync()
    {
        if (_host is null) return;
        try { await _host.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(20)); }
        finally { _host.Dispose(); }
    }

    private async Task<InterviewSources> SourcesAsync(SaveChangesInterceptor? fault = null, Action<IServiceCollection>? configure = null, bool consumer = false)
    {
        _host = await PostProcessingTestHost.BuildAsync(pg, consumer: consumer, fault: fault, configure: configure);
        await _host.StartAsync(Ct);
        await _host.Services.GetRequiredService<JobFinderLlm>().ForOperationAsync(Ct);
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        await NotificationAdmissionAssertions.ResetAsync(context, Ct);
        return _host.Services.GetRequiredService<InterviewSources>();
    }

    private async Task<Interview> InterviewAsync(bool pack = false)
    {
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        var source = new Interview
        {
            Kind = InterviewKind.Technical, Status = InterviewStatus.Scheduled, CreatedAt = DateTimeOffset.UtcNow,
            ScheduledAt = DateTimeOffset.UtcNow.AddDays(2), DurationMinutes = 60,
            Application = new Application
            {
                Status = ApplicationStatus.Sent, CreatedAt = DateTimeOffset.UtcNow,
                Vacancy = new Vacancy { Title = "Backend", CompanyName = "Synthetic", Url = "https://vacancy.invalid/1",
                    Description = "C# backend", Skills = ["C#"], Fingerprint = Guid.NewGuid().ToString("N"), FirstSeenAt = DateTimeOffset.UtcNow },
            },
        };
        if (pack)
        {
            source.PackJson = JsonSerializer.Serialize(new InterviewCopilot
            {
                Version = InterviewCopilot.CurrentVersion,
                Preparation = [new() { Q = new PlannedQuestion { Id = "about", Question = "Tell me about your experience", Category = "hr" } }],
            }, InterviewJsonContext.Default.InterviewCopilot);
            source.PackPdfPath = "data/interviews/synthetic.pdf";
        }
        context.Interviews.Add(source);
        await context.SaveChangesAsync(Ct);
        return source;
    }

    private async Task<Interview> ReadAsync(long id)
    {
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        return await context.Interviews.AsNoTracking().SingleAsync(row => row.Id == id, Ct);
    }

    private async Task<int> CommandsAsync()
    {
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        return await context.Database.SqlQueryRaw<int>("SELECT count(*)::int AS \"Value\" FROM jobfinder_queues.wolverine_queue_preparation").SingleAsync(Ct);
    }

    [Fact]
    public async Task Edit_commits_source_revision_root_notice_and_native_command_together()
    {
        var source = await InterviewAsync(pack: true);
        var commands = await SourcesAsync();
        var snapshot = (await commands.CaptureAsync(source.Id, Ct))!;
        var at = new DateTimeOffset(2026, 10, 10, 14, 0, 0, TimeSpan.FromHours(3)).AddTicks(7);
        Assert.True((await commands.EditAsync(snapshot, InterviewKind.SystemDesign, 90, at, Ct)).Ok);
        source = await ReadAsync(source.Id);
        Assert.Equal(1, source.PreparationRevision);
        Assert.Equal(90, source.DurationMinutes);
        Assert.Equal(InterviewKind.SystemDesign, source.Kind);
        Assert.Equal(at.UtcTicks - at.UtcTicks % 10, source.ScheduledAt!.Value.UtcTicks);
        Assert.Null(source.PackJson);
        Assert.Null(source.PackPdfPath);
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        var root = await context.InterviewPreparationRequests.SingleAsync(Ct);
        Assert.Equal(source.PreparationRequestId, root.Id);
        Assert.Equal(source.PreparationRevision, root.Revision);
        Assert.True(InterviewPreparationRequests.ReadSeed(root).Source.Matches(source, root.Id));
        Assert.Single(await context.Notifications.Where(row => row.EntityId == source.Id).ToListAsync(Ct));
        Assert.Equal(1, await CommandsAsync());
    }

    [Theory]
    [InlineData("time")]
    [InlineData("kind")]
    [InlineData("duration")]
    [InlineData("status")]
    [InlineData("regeneration")]
    public async Task Old_displayed_snapshot_cannot_overwrite_another_command(string first)
    {
        var source = await InterviewAsync();
        var commands = await SourcesAsync();
        var snapshot = (await commands.CaptureAsync(source.Id, Ct))!;
        switch (first)
        {
            case "time": await commands.SetTimeAsync(snapshot, null, Ct); break;
            case "kind": await commands.EditAsync(snapshot, InterviewKind.HrScreen, 60, snapshot.Source.ScheduledAt, Ct); break;
            case "duration": await commands.EditAsync(snapshot, source.Kind, 90, snapshot.Source.ScheduledAt, Ct); break;
            case "status": await commands.SetStatusAsync(snapshot, InterviewStatus.Cancelled, Ct); break;
            default: await commands.RebuildAsync(snapshot, Ct); break;
        }
        var winner = await ReadAsync(source.Id);
        await Assert.ThrowsAsync<InterviewEditConflictException>(() => commands.SetTimeAsync(snapshot, DateTimeOffset.UtcNow.AddDays(5), Ct));
        Assert.Equal(InterviewEditSnapshot.Capture(winner), InterviewEditSnapshot.Capture(await ReadAsync(source.Id)));
    }

    [Fact]
    public async Task Concurrent_commands_have_one_winner_under_the_source_lock()
    {
        var source = await InterviewAsync();
        var commands = await SourcesAsync();
        var snapshot = (await commands.CaptureAsync(source.Id, Ct))!;
        async Task<bool> SaveAsync(int duration)
        {
            try { await commands.EditAsync(snapshot, source.Kind, duration, snapshot.Source.ScheduledAt, Ct); return true; }
            catch (InterviewEditConflictException) { return false; }
        }
        var results = await Task.WhenAll(SaveAsync(90), SaveAsync(120));
        Assert.Single(results, value => value);
        Assert.Equal(1, (await ReadAsync(source.Id)).PreparationRevision);
        Assert.Equal(1, await CommandsAsync());
    }

    [Fact]
    public async Task Rebuild_cancels_the_previous_claim_and_assigns_only_one_new_owner()
    {
        var source = await InterviewAsync();
        var commands = await SourcesAsync();
        await commands.RebuildAsync((await commands.CaptureAsync(source.Id, Ct))!, Ct);
        var previousId = (await ReadAsync(source.Id)).PreparationRequestId!.Value;
        await using (var context = await pg.Db.CreateDbContextAsync(Ct))
            await context.InterviewPreparationRequests.Where(row => row.Id == previousId).ExecuteUpdateAsync(update => update.SetProperty(row => row.ClaimEnvelopeId, Guid.NewGuid()), Ct);
        await commands.RebuildAsync((await commands.CaptureAsync(source.Id, Ct))!, Ct);
        source = await ReadAsync(source.Id);
        await using var final = await pg.Db.CreateDbContextAsync(Ct);
        var previous = await final.InterviewPreparationRequests.SingleAsync(row => row.Id == previousId, Ct);
        Assert.Equal(InterviewPreparationStage.Cancelled, previous.Stage);
        Assert.Null(previous.ClaimEnvelopeId);
        Assert.NotNull(previous.CompletedAt);
        Assert.NotEqual(previousId, source.PreparationRequestId);
        Assert.Equal(2, source.PreparationRevision);
        Assert.Equal(2, await CommandsAsync());
    }

    [Fact]
    public async Task Closing_cancels_active_preparation_without_enqueuing_a_new_root()
    {
        var source = await InterviewAsync();
        var commands = await SourcesAsync();
        await commands.RebuildAsync((await commands.CaptureAsync(source.Id, Ct))!, Ct);
        await commands.SetStatusAsync((await commands.CaptureAsync(source.Id, Ct))!, InterviewStatus.Done, Ct);
        source = await ReadAsync(source.Id);
        Assert.Equal(InterviewStatus.Done, source.Status);
        Assert.Null(source.PreparationRequestId);
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        Assert.Equal(InterviewPreparationStage.Cancelled, (await context.InterviewPreparationRequests.SingleAsync(Ct)).Stage);
        Assert.Equal(1, await CommandsAsync());
    }

    [Fact]
    public async Task Noop_and_reminder_flags_do_not_supersede_preparation()
    {
        var source = await InterviewAsync();
        var commands = await SourcesAsync();
        await commands.RebuildAsync((await commands.CaptureAsync(source.Id, Ct))!, Ct);
        var snapshot = (await commands.CaptureAsync(source.Id, Ct))!;
        await using (var context = await pg.Db.CreateDbContextAsync(Ct))
            await context.Interviews.Where(row => row.Id == source.Id).ExecuteUpdateAsync(update => update.SetProperty(row => row.Reminded, true).SetProperty(row => row.DebriefAsked, true), Ct);
        await commands.SetTimeAsync(snapshot, snapshot.Source.ScheduledAt, Ct);
        Assert.Equal(snapshot, InterviewEditSnapshot.Capture(await ReadAsync(source.Id)));
        Assert.Equal(1, await CommandsAsync());
    }

    [Fact]
    public async Task Source_commit_failure_rolls_back_revision_root_notice_and_command()
    {
        var source = await InterviewAsync(pack: true);
        var commands = await SourcesAsync(new Fault());
        var snapshot = (await commands.CaptureAsync(source.Id, Ct))!;
        await Assert.ThrowsAsync<InvalidOperationException>(() => commands.RebuildAsync(snapshot, Ct));
        Assert.Equal(snapshot, InterviewEditSnapshot.Capture(await ReadAsync(source.Id)));
        Assert.Equal(CanonicalJson.Hash(source.PackJson!), CanonicalJson.Hash((await ReadAsync(source.Id)).PackJson!));
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        Assert.Empty(await context.InterviewPreparationRequests.ToListAsync(Ct));
        Assert.Empty(await context.Notifications.ToListAsync(Ct));
        Assert.Equal(0, await CommandsAsync());
    }

    [Fact]
    public async Task Failed_replacement_does_not_cancel_the_existing_owner()
    {
        var source = await InterviewAsync();
        var fault = new Fault { Enabled = false };
        var commands = await SourcesAsync(fault);
        await commands.RebuildAsync((await commands.CaptureAsync(source.Id, Ct))!, Ct);
        var snapshot = (await commands.CaptureAsync(source.Id, Ct))!;
        fault.Enabled = true;
        await Assert.ThrowsAsync<InvalidOperationException>(() => commands.RebuildAsync(snapshot, Ct));
        Assert.Equal(snapshot, InterviewEditSnapshot.Capture(await ReadAsync(source.Id)));
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        Assert.Equal(InterviewPreparationStage.Capturing, (await context.InterviewPreparationRequests.SingleAsync(Ct)).Stage);
        Assert.Single(await context.Notifications.ToListAsync(Ct));
        Assert.Equal(1, await CommandsAsync());
    }

    [Fact]
    public async Task Position_commits_fact_history_notice_invalidation_and_new_root_together()
    {
        var source = await InterviewAsync(pack: true);
        var commands = await SourcesAsync();
        var snapshot = (await commands.CapturePositionAsync(source.Id, "about", Ct))!;
        Assert.True((await commands.SavePositionAsync(snapshot, "My own factual answer", Ct)).Ok);
        source = await ReadAsync(source.Id);
        Assert.Equal(1, source.PreparationRevision);
        Assert.NotNull(source.PreparationRequestId);
        Assert.Null(source.PackJson);
        Assert.Null(source.PackPdfPath);
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        var document = await context.Documents.SingleAsync(row => row.Key == FactsProvider.Key, Ct);
        Assert.Equal(snapshot.FactsVersion + 1, document.Version);
        var facts = JsonSerializer.Deserialize(document.Content, FactsJsonContext.Default.CandidateFacts)!;
        Assert.Equal("My own factual answer", facts.Get("hr_about")!.Answer);
        Assert.True(facts.Get("hr_about")!.Confirmed);
        Assert.Equal("Minsk", facts.Get("location")!.Answer);
        Assert.Equal(document.Content, (await context.CandidateRevisions.SingleAsync(row => row.Key == FactsProvider.Key && row.Version == document.Version, Ct)).Content);
        Assert.Single(await context.Notifications.ToListAsync(Ct));
        Assert.Equal(1, await CommandsAsync());
    }

    [Fact]
    public async Task Position_commit_failure_rolls_back_candidate_history_and_handoff()
    {
        var source = await InterviewAsync(pack: true);
        var commands = await SourcesAsync(new Fault());
        var snapshot = (await commands.CapturePositionAsync(source.Id, "about", Ct))!;
        await Assert.ThrowsAsync<InvalidOperationException>(() => commands.SavePositionAsync(snapshot, "Keep this editor text", Ct));
        source = await ReadAsync(source.Id);
        Assert.NotNull(source.PackJson);
        Assert.Null(source.PreparationRequestId);
        Assert.Equal(0, source.PreparationRevision);
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        var facts = await context.Documents.SingleAsync(row => row.Key == FactsProvider.Key, Ct);
        Assert.Equal(snapshot.FactsVersion, facts.Version);
        Assert.DoesNotContain("hr_about", facts.Content);
        Assert.Single(await context.CandidateRevisions.Where(row => row.Key == FactsProvider.Key).ToListAsync(Ct));
        Assert.Empty(await context.Notifications.ToListAsync(Ct));
        Assert.Empty(await context.InterviewPreparationRequests.ToListAsync(Ct));
        Assert.Equal(0, await CommandsAsync());
    }

    [Fact]
    public async Task Position_refuses_a_changed_facts_version_instead_of_rebasing_the_editor()
    {
        var source = await InterviewAsync(pack: true);
        var commands = await SourcesAsync();
        var snapshot = (await commands.CapturePositionAsync(source.Id, "about", Ct))!;
        await (await pg.CandidateAsync()).PublishFactsAsync(new CandidateFacts
        {
            Facts = [new() { Key = "hr_about", Question = "My current position", Answer = "Newer answer", Confirmed = true }],
        }, snapshot.FactsVersion, "other-editor", Ct);
        await Assert.ThrowsAsync<CandidateConflictException>(() => commands.SavePositionAsync(snapshot, "Old editor answer", Ct));
        Assert.NotNull((await ReadAsync(source.Id)).PackJson);
        Assert.Equal(0, await CommandsAsync());
    }

    [Theory]
    [InlineData("pack")]
    [InlineData("question")]
    [InlineData("time")]
    [InlineData("owner")]
    public async Task Position_refuses_changed_pack_question_source_or_owner(string changed)
    {
        var source = await InterviewAsync(pack: true);
        var commands = await SourcesAsync();
        var snapshot = (await commands.CapturePositionAsync(source.Id, "about", Ct))!;
        if (changed == "question") snapshot.Question.Question = "A different question";
        else
        {
            await using var context = await pg.Db.CreateDbContextAsync(Ct);
            var current = await context.Interviews.SingleAsync(row => row.Id == source.Id, Ct);
            if (changed == "pack") current.PackJson = "{}";
            if (changed == "time") current.ScheduledAt = current.ScheduledAt!.Value.AddHours(1);
            if (changed == "owner") current.PreparationRequestId = Guid.NewGuid();
            await context.SaveChangesAsync(Ct);
        }
        await Assert.ThrowsAsync<InterviewEditConflictException>(() => commands.SavePositionAsync(snapshot, "Old answer", Ct));
        await using var final = await pg.Db.CreateDbContextAsync(Ct);
        Assert.Equal(snapshot.FactsVersion, (await final.Documents.SingleAsync(row => row.Key == FactsProvider.Key, Ct)).Version);
        Assert.Equal(0, await CommandsAsync());
    }

    [Fact]
    public async Task Editor_snapshots_roundtrip_and_unknown_versions_fail_closed()
    {
        var source = await InterviewAsync(pack: true);
        var commands = await SourcesAsync();
        var snapshot = (await commands.CapturePositionAsync(source.Id, "about", Ct))!;
        var json = JsonSerializer.Serialize(snapshot, InterviewEditJsonContext.Default.InterviewPositionSnapshot);
        var copy = JsonSerializer.Deserialize(json, InterviewEditJsonContext.Default.InterviewPositionSnapshot)!;
        Assert.Equal(snapshot.Interview, copy.Interview);
        Assert.Equal(snapshot.PackHash, copy.PackHash);
        Assert.Equal(JsonSerializer.Serialize(snapshot.Question, InterviewJsonContext.Default.PlannedQuestion),
            JsonSerializer.Serialize(copy.Question, InterviewJsonContext.Default.PlannedQuestion));
        copy.Interview.Version = 2;
        await Assert.ThrowsAsync<InterviewEditConflictException>(() => commands.RebuildAsync(copy.Interview, Ct));
        Assert.Equal(0, await CommandsAsync());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(481)]
    public async Task Invalid_duration_does_not_write_or_enqueue(int duration)
    {
        var source = await InterviewAsync();
        var commands = await SourcesAsync();
        var snapshot = (await commands.CaptureAsync(source.Id, Ct))!;
        await Assert.ThrowsAsync<ArgumentException>(() => commands.EditAsync(snapshot, source.Kind, duration, null, Ct));
        Assert.Equal(snapshot, InterviewEditSnapshot.Capture(await ReadAsync(source.Id)));
        Assert.Equal(0, await CommandsAsync());
    }

    [Fact]
    public async Task Position_refuses_a_changed_profile_version_without_writing_the_facts()
    {
        var source = await InterviewAsync(pack: true);
        var commands = await SourcesAsync();
        var snapshot = (await commands.CapturePositionAsync(source.Id, "about", Ct))!;
        await (await pg.CandidateAsync()).PublishProfileAsync(new CandidateProfile { Name = "Newer profile", Reviewed = true },
            snapshot.ProfileVersion, "other-editor", Ct);
        await Assert.ThrowsAsync<CandidateConflictException>(() => commands.SavePositionAsync(snapshot, "Old editor answer", Ct));
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        Assert.Equal(snapshot.FactsVersion, (await context.Documents.SingleAsync(row => row.Key == FactsProvider.Key, Ct)).Version);
        Assert.Equal(0, await CommandsAsync());
    }

    [Fact]
    public async Task Closing_keeps_historical_pack_but_retires_its_preparation_owner()
    {
        var source = await InterviewAsync(pack: true);
        var commands = await SourcesAsync();
        await commands.SetStatusAsync((await commands.CaptureAsync(source.Id, Ct))!, InterviewStatus.Done, Ct);
        var closed = await ReadAsync(source.Id);
        Assert.Equal(CanonicalJson.Hash(source.PackJson!), CanonicalJson.Hash(closed.PackJson!));
        Assert.Equal(source.PackPdfPath, closed.PackPdfPath);
        Assert.Null(closed.PreparationRequestId);
        Assert.Equal(1, closed.PreparationRevision);
        Assert.Null(await commands.CapturePositionAsync(source.Id, "about", Ct));
        Assert.Equal(0, await CommandsAsync());
    }

    [Fact]
    public async Task Two_joined_reschedules_retire_the_staged_root_without_duplicating_the_stage_event()
    {
        var source = await InterviewAsync();
        await SourcesAsync();
        await using (var context = await pg.Db.CreateDbContextAsync(Ct))
        {
            await using var transaction = await context.Database.BeginTransactionAsync(Ct);
            await using var scope = _host!.Services.CreateAsyncScope();
            var outbox = scope.ServiceProvider.GetRequiredService<IDbContextOutbox>();
            outbox.Enroll(context);
            var app = await context.Applications.SingleAsync(Ct);
            var transitions = _host.Services.GetRequiredService<InterviewTransitions>();
            await transitions.AdvanceAsync(context, outbox, app, ApplicationStage.TechInterview, EventChannel.Telegram, "First time", false, Ct, DateTimeOffset.UtcNow.AddDays(4));
            await transitions.AdvanceAsync(context, outbox, app, ApplicationStage.TechInterview, EventChannel.Telegram, "Corrected time", false, Ct, DateTimeOffset.UtcNow.AddDays(5));
            await outbox.SaveChangesAndFlushMessagesAsync(Ct);
        }
        await using var final = await pg.Db.CreateDbContextAsync(Ct);
        var roots = await final.InterviewPreparationRequests.ToListAsync(Ct);
        Assert.Equal(2, roots.Count);
        Assert.Single(roots, row => row.Stage == InterviewPreparationStage.Cancelled);
        Assert.Equal((await ReadAsync(source.Id)).PreparationRequestId, Assert.Single(roots, row => row.Stage == InterviewPreparationStage.Capturing).Id);
        Assert.Single(await final.ApplicationEvents.Where(row => row.Kind == EventKind.StageChanged).ToListAsync(Ct));
        Assert.Equal(2, await CommandsAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_corrupted_marker_cannot_cancel_a_different_interviews_root(bool requestFacade)
    {
        var first = await InterviewAsync();
        var second = await InterviewAsync();
        var commands = await SourcesAsync();
        await commands.RebuildAsync((await commands.CaptureAsync(first.Id, Ct))!, Ct);
        await commands.RebuildAsync((await commands.CaptureAsync(second.Id, Ct))!, Ct);
        var otherRootId = (await ReadAsync(second.Id)).PreparationRequestId!.Value;
        await using (var context = await pg.Db.CreateDbContextAsync(Ct))
            await context.Interviews.Where(row => row.Id == first.Id).ExecuteUpdateAsync(update => update.SetProperty(row => row.PreparationRequestId, otherRootId), Ct);
        if (requestFacade) await _host!.Services.GetRequiredService<InterviewPreparationRequests>().RequestAsync(first.Id, Ct, force: true);
        else await commands.RebuildAsync((await commands.CaptureAsync(first.Id, Ct))!, Ct);
        await using var final = await pg.Db.CreateDbContextAsync(Ct);
        Assert.Equal(InterviewPreparationStage.Capturing, (await final.InterviewPreparationRequests.SingleAsync(row => row.Id == otherRootId, Ct)).Stage);
        Assert.Equal(otherRootId, (await ReadAsync(second.Id)).PreparationRequestId);
        Assert.NotEqual(otherRootId, (await ReadAsync(first.Id)).PreparationRequestId);
    }

    [Fact]
    public async Task Scheduling_commits_stage_event_source_root_and_command_and_reuses_a_duplicate()
    {
        var source = await InterviewAsync();
        await using (var context = await pg.Db.CreateDbContextAsync(Ct)) await context.Interviews.Where(row => row.Id == source.Id).ExecuteDeleteAsync(Ct);
        await SourcesAsync();
        var transitions = _host!.Services.GetRequiredService<InterviewTransitions>();
        Assert.True((await transitions.SetStageAsync(source.ApplicationId, ApplicationStage.HrScreen, "Interview invitation", Ct)).Ok);
        Assert.True((await transitions.SetStageAsync(source.ApplicationId, ApplicationStage.HrScreen, "Duplicate invitation", Ct)).Ok);
        await using var final = await pg.Db.CreateDbContextAsync(Ct);
        var created = await final.Interviews.SingleAsync(Ct);
        var root = await final.InterviewPreparationRequests.SingleAsync(Ct);
        Assert.Equal(InterviewKind.HrScreen, created.Kind);
        Assert.Equal(created.PreparationRequestId, root.Id);
        Assert.True(InterviewPreparationRequests.ReadSeed(root).Source.Matches(created, root.Id));
        Assert.Equal(ApplicationStage.HrScreen, (await final.Applications.SingleAsync(Ct)).Stage);
        Assert.Single(await final.ApplicationEvents.Where(row => row.Kind == EventKind.StageChanged).ToListAsync(Ct));
        Assert.Equal(1, await CommandsAsync());
    }

    [Fact]
    public async Task Concurrent_scheduling_has_one_source_and_one_root()
    {
        var source = await InterviewAsync();
        await using (var context = await pg.Db.CreateDbContextAsync(Ct)) await context.Interviews.Where(row => row.Id == source.Id).ExecuteDeleteAsync(Ct);
        await SourcesAsync();
        var transitions = _host!.Services.GetRequiredService<InterviewTransitions>();
        await Task.WhenAll(transitions.SetStageAsync(source.ApplicationId, ApplicationStage.TechInterview, null, Ct),
            transitions.SetStageAsync(source.ApplicationId, ApplicationStage.TechInterview, null, Ct));
        await using var final = await pg.Db.CreateDbContextAsync(Ct);
        Assert.Single(await final.Interviews.ToListAsync(Ct));
        Assert.Single(await final.InterviewPreparationRequests.ToListAsync(Ct));
        Assert.Single(await final.ApplicationEvents.Where(row => row.Kind == EventKind.StageChanged).ToListAsync(Ct));
        Assert.Equal(1, await CommandsAsync());
    }

    [Fact]
    public async Task Scheduling_failure_rolls_back_stage_event_and_generated_source_id()
    {
        var source = await InterviewAsync();
        await using (var context = await pg.Db.CreateDbContextAsync(Ct)) await context.Interviews.Where(row => row.Id == source.Id).ExecuteDeleteAsync(Ct);
        await SourcesAsync(new Fault());
        var transitions = _host!.Services.GetRequiredService<InterviewTransitions>();
        await Assert.ThrowsAsync<InvalidOperationException>(() => transitions.SetStageAsync(source.ApplicationId, ApplicationStage.TechInterview, "Not committed", Ct));
        await using var final = await pg.Db.CreateDbContextAsync(Ct);
        Assert.Null((await final.Applications.SingleAsync(Ct)).Stage);
        Assert.Empty(await final.ApplicationEvents.ToListAsync(Ct));
        Assert.Empty(await final.Interviews.ToListAsync(Ct));
        Assert.Empty(await final.InterviewPreparationRequests.ToListAsync(Ct));
        Assert.Empty(await final.Notifications.ToListAsync(Ct));
        Assert.Equal(0, await CommandsAsync());
    }

    [Fact]
    public async Task Joined_automatic_stage_change_reads_current_stage_and_cannot_reopen_a_rejection()
    {
        var source = await InterviewAsync();
        await SourcesAsync();
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        var app = await context.Applications.SingleAsync(Ct);
        await using (var writer = await pg.Db.CreateDbContextAsync(Ct))
            await writer.Applications.Where(row => row.Id == app.Id).ExecuteUpdateAsync(update => update.SetProperty(row => row.Stage, ApplicationStage.Rejected), Ct);
        await using var transaction = await context.Database.BeginTransactionAsync(Ct);
        await using var scope = _host!.Services.CreateAsyncScope();
        var outbox = scope.ServiceProvider.GetRequiredService<IDbContextOutbox>();
        outbox.Enroll(context);
        Assert.False(await _host.Services.GetRequiredService<InterviewTransitions>().AdvanceAsync(context, outbox, app, ApplicationStage.HrScreen,
            EventChannel.Telegram, "Old invitation", false, Ct));
        await outbox.SaveChangesAndFlushMessagesAsync(Ct);
        await using var final = await pg.Db.CreateDbContextAsync(Ct);
        Assert.Equal(ApplicationStage.Rejected, (await final.Applications.SingleAsync(Ct)).Stage);
        Assert.Empty(await final.ApplicationEvents.ToListAsync(Ct));
        Assert.Empty(await final.InterviewPreparationRequests.ToListAsync(Ct));
        Assert.Equal(0, await CommandsAsync());
    }

    [Fact]
    public async Task Joined_scheduling_and_an_application_event_roll_back_with_the_callers_transaction()
    {
        var source = await InterviewAsync();
        await SourcesAsync();
        await using (var context = await pg.Db.CreateDbContextAsync(Ct))
        {
            await using var transaction = await context.Database.BeginTransactionAsync(Ct);
            await using var scope = _host!.Services.CreateAsyncScope();
            var outbox = scope.ServiceProvider.GetRequiredService<IDbContextOutbox>();
            outbox.Enroll(context);
            var app = await context.Applications.SingleAsync(Ct);
            context.ApplicationEvents.Add(new() { ApplicationId = app.Id, At = DateTimeOffset.UtcNow, Kind = EventKind.MessageIn, Text = "Invitation" });
            await _host.Services.GetRequiredService<InterviewTransitions>().AdvanceAsync(context, outbox, app, ApplicationStage.TechInterview,
                EventChannel.Telegram, "Invite", false, Ct, DateTimeOffset.UtcNow.AddDays(4));
            await context.SaveChangesAsync(Ct);
            await transaction.RollbackAsync(Ct);
        }
        await using var final = await pg.Db.CreateDbContextAsync(Ct);
        Assert.Null((await final.Applications.SingleAsync(Ct)).Stage);
        Assert.Empty(await final.ApplicationEvents.ToListAsync(Ct));
        Assert.Empty(await final.InterviewPreparationRequests.ToListAsync(Ct));
        Assert.Equal(0, await CommandsAsync());
    }

    [Fact]
    public async Task Invitation_with_a_new_time_replaces_old_preparation_but_missing_time_preserves_it()
    {
        var source = await InterviewAsync();
        var commands = await SourcesAsync();
        await commands.RebuildAsync((await commands.CaptureAsync(source.Id, Ct))!, Ct);
        var oldId = (await ReadAsync(source.Id)).PreparationRequestId!.Value;
        await using (var context = await pg.Db.CreateDbContextAsync(Ct))
        {
            await using var transaction = await context.Database.BeginTransactionAsync(Ct);
            await using var scope = _host!.Services.CreateAsyncScope();
            var outbox = scope.ServiceProvider.GetRequiredService<IDbContextOutbox>();
            outbox.Enroll(context);
            var app = await context.Applications.SingleAsync(Ct);
            await _host.Services.GetRequiredService<InterviewTransitions>().AdvanceAsync(context, outbox, app, ApplicationStage.TechInterview,
                EventChannel.Telegram, "New time", false, Ct, DateTimeOffset.UtcNow.AddDays(5));
            await outbox.SaveChangesAndFlushMessagesAsync(Ct);
        }
        var rescheduled = await ReadAsync(source.Id);
        Assert.NotEqual(oldId, rescheduled.PreparationRequestId);
        await _host!.Services.GetRequiredService<InterviewTransitions>().SetStageAsync(source.ApplicationId, ApplicationStage.TechInterview, "No time", Ct);
        Assert.Equal(rescheduled.ScheduledAt, (await ReadAsync(source.Id)).ScheduledAt);
        Assert.Equal(rescheduled.PreparationRequestId, (await ReadAsync(source.Id)).PreparationRequestId);
        await using var final = await pg.Db.CreateDbContextAsync(Ct);
        Assert.Equal(InterviewPreparationStage.Cancelled, (await final.InterviewPreparationRequests.SingleAsync(row => row.Id == oldId, Ct)).Stage);
        Assert.Equal(2, await CommandsAsync());
    }

    [Fact]
    public async Task Scheduling_refuses_a_source_loaded_before_another_edit()
    {
        var source = await InterviewAsync();
        var commands = await SourcesAsync();
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        await context.Interviews.SingleAsync(Ct);
        await commands.SetTimeAsync((await commands.CaptureAsync(source.Id, Ct))!, DateTimeOffset.UtcNow.AddDays(5), Ct);
        var winner = await ReadAsync(source.Id);
        await using var transaction = await context.Database.BeginTransactionAsync(Ct);
        await using var scope = _host!.Services.CreateAsyncScope();
        var outbox = scope.ServiceProvider.GetRequiredService<IDbContextOutbox>();
        outbox.Enroll(context);
        var app = await context.Applications.SingleAsync(Ct);
        await Assert.ThrowsAsync<InterviewEditConflictException>(() => _host.Services.GetRequiredService<InterviewTransitions>().AdvanceAsync(context,
            outbox, app, ApplicationStage.TechInterview, EventChannel.Telegram, "Old time", false, Ct, DateTimeOffset.UtcNow.AddDays(2)));
        Assert.Equal(InterviewEditSnapshot.Capture(winner), InterviewEditSnapshot.Capture(await ReadAsync(source.Id)));
        Assert.Equal(1, await CommandsAsync());
    }

    [Fact]
    public async Task Debrief_commits_closure_note_next_stage_and_next_interview_root_together()
    {
        var source = await InterviewAsync(pack: true);
        var commands = await SourcesAsync();
        var snapshot = (await commands.CaptureAsync(source.Id, Ct))!;
        await _host!.Services.GetRequiredService<InterviewTransitions>().CompleteDebriefAsync(snapshot,
            new DebriefResult { Summary = "Discussed systems", QuestionsAsked = ["Transactions"], ToImprove = ["Indexes"], Stage = "final" }, Ct);
        var completed = await ReadAsync(source.Id);
        Assert.Equal(InterviewStatus.Done, completed.Status);
        Assert.True(completed.DebriefAsked);
        Assert.Contains("Transactions", completed.Debrief);
        Assert.Equal(CanonicalJson.Hash(source.PackJson!), CanonicalJson.Hash(completed.PackJson!));
        await using var final = await pg.Db.CreateDbContextAsync(Ct);
        var next = await final.Interviews.SingleAsync(row => row.Id != source.Id, Ct);
        Assert.Equal(InterviewKind.Final, next.Kind);
        Assert.NotNull(next.PreparationRequestId);
        Assert.Equal(ApplicationStage.Final, (await final.Applications.SingleAsync(Ct)).Stage);
        Assert.Single(await final.ApplicationEvents.Where(row => row.Kind == EventKind.Note).ToListAsync(Ct));
        Assert.Single(await final.ApplicationEvents.Where(row => row.Kind == EventKind.StageChanged).ToListAsync(Ct));
        Assert.Equal(1, await CommandsAsync());
    }

    [Fact]
    public async Task Stale_debrief_cannot_close_a_rescheduled_interview_or_advance_the_funnel()
    {
        var source = await InterviewAsync();
        var commands = await SourcesAsync();
        var snapshot = (await commands.CaptureAsync(source.Id, Ct))!;
        await commands.SetTimeAsync(snapshot, DateTimeOffset.UtcNow.AddDays(5), Ct);
        await Assert.ThrowsAsync<InterviewEditConflictException>(() => _host!.Services.GetRequiredService<InterviewTransitions>().CompleteDebriefAsync(snapshot,
            new DebriefResult { Summary = "Old debrief", Stage = "final" }, Ct));
        await using var final = await pg.Db.CreateDbContextAsync(Ct);
        Assert.Null((await final.Applications.SingleAsync(Ct)).Stage);
        Assert.Equal(InterviewStatus.Scheduled, (await ReadAsync(source.Id)).Status);
        Assert.Null((await ReadAsync(source.Id)).Debrief);
        Assert.Empty(await final.ApplicationEvents.ToListAsync(Ct));
        Assert.Equal(1, await CommandsAsync());
    }

    [Fact]
    public async Task Debrief_and_its_following_interview_roll_back_if_admission_fails()
    {
        var source = await InterviewAsync(pack: true);
        var commands = await SourcesAsync(new Fault());
        var snapshot = (await commands.CaptureAsync(source.Id, Ct))!;
        await Assert.ThrowsAsync<InvalidOperationException>(() => _host!.Services.GetRequiredService<InterviewTransitions>().CompleteDebriefAsync(snapshot,
            new DebriefResult { Summary = "Not committed", Stage = "final" }, Ct));
        Assert.Equal(snapshot, InterviewEditSnapshot.Capture(await ReadAsync(source.Id)));
        await using var final = await pg.Db.CreateDbContextAsync(Ct);
        Assert.Null((await final.Applications.SingleAsync(Ct)).Stage);
        Assert.Single(await final.Interviews.ToListAsync(Ct));
        Assert.Empty(await final.ApplicationEvents.ToListAsync(Ct));
        Assert.Empty(await final.InterviewPreparationRequests.ToListAsync(Ct));
        Assert.Empty(await final.Notifications.ToListAsync(Ct));
        Assert.Equal(0, await CommandsAsync());
    }

    [Fact]
    public async Task Import_is_bounded_and_never_requeues_owned_work()
    {
        for (var index = 0; index < 4; index++) await InterviewAsync();
        await SourcesAsync();
        var requests = _host!.Services.GetRequiredService<InterviewPreparationRequests>();
        Assert.Equal(2, await requests.ImportAsync(2, Ct));
        Assert.Equal(2, await CommandsAsync());
        Assert.Equal(2, await requests.ImportAsync(2, Ct));
        Assert.Equal(4, await CommandsAsync());
        Assert.Equal(0, await requests.ImportAsync(2, Ct));
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        Assert.Empty(await InterviewPreparationRequests.Waiting(context).ToListAsync(Ct));
        Assert.Equal(4, await context.InterviewPreparationRequests.CountAsync(Ct));
    }

    [Fact]
    public async Task Orphan_repair_counts_progress_then_admits_once_without_alert_spam()
    {
        var source = await InterviewAsync();
        await using (var context = await pg.Db.CreateDbContextAsync(Ct))
            await context.Interviews.Where(row => row.Id == source.Id).ExecuteUpdateAsync(update => update.SetProperty(row => row.PreparationRequestId, Guid.NewGuid()), Ct);
        await SourcesAsync();
        var requests = _host!.Services.GetRequiredService<InterviewPreparationRequests>();
        Assert.Equal(1, await requests.ImportAsync(1, Ct));
        Assert.Equal(0, await CommandsAsync());
        Assert.Equal(1, await requests.ImportAsync(1, Ct));
        Assert.Equal(0, await requests.ImportAsync(1, Ct));
        await using var final = await pg.Db.CreateDbContextAsync(Ct);
        Assert.Single(await final.Notifications.Where(row => row.Kind == NotificationKind.Alert).ToListAsync(Ct));
        Assert.Single(await final.InterviewPreparationRequests.ToListAsync(Ct));
        Assert.Equal(1, await CommandsAsync());
    }

    [Theory]
    [InlineData("duration")]
    [InlineData("too-long")]
    [InlineData("kind")]
    [InlineData("revision")]
    [InlineData("overflow")]
    [InlineData("created")]
    public async Task Malformed_legacy_source_is_retired_once_without_starving_valid_work(string malformed)
    {
        var bad = await InterviewAsync();
        var good = await InterviewAsync();
        await using (var context = await pg.Db.CreateDbContextAsync(Ct))
        {
            var source = await context.Interviews.SingleAsync(row => row.Id == bad.Id, Ct);
            if (malformed == "duration") source.DurationMinutes = 0;
            if (malformed == "too-long") source.DurationMinutes = 481;
            if (malformed == "kind") source.Kind = (InterviewKind)999;
            if (malformed == "revision") source.PreparationRevision = -1;
            if (malformed == "overflow") source.PreparationRevision = long.MaxValue;
            if (malformed == "created") source.CreatedAt = default;
            await context.SaveChangesAsync(Ct);
        }
        await SourcesAsync();
        var requests = _host!.Services.GetRequiredService<InterviewPreparationRequests>();
        Assert.Equal(1, await requests.ImportAsync(1, Ct));
        Assert.Equal(InterviewStatus.Cancelled, (await ReadAsync(bad.Id)).Status);
        Assert.Equal(1, await requests.ImportAsync(1, Ct));
        Assert.Equal(0, await requests.ImportAsync(1, Ct));
        await using var final = await pg.Db.CreateDbContextAsync(Ct);
        Assert.Equal(good.Id, (await final.InterviewPreparationRequests.SingleAsync(Ct)).InterviewId);
        Assert.Single(await final.Notifications.Where(row => row.Kind == NotificationKind.Alert).ToListAsync(Ct));
        Assert.Equal(1, await CommandsAsync());
    }

    [Fact]
    public async Task Import_ignores_closed_historical_and_valid_failed_owned_sources()
    {
        await InterviewAsync(pack: true);
        var closed = await InterviewAsync();
        var failed = await InterviewAsync();
        var commands = await SourcesAsync();
        await commands.SetStatusAsync((await commands.CaptureAsync(closed.Id, Ct))!, InterviewStatus.Done, Ct);
        await commands.RebuildAsync((await commands.CaptureAsync(failed.Id, Ct))!, Ct);
        await using (var context = await pg.Db.CreateDbContextAsync(Ct))
            await context.InterviewPreparationRequests.ExecuteUpdateAsync(update => update.SetProperty(row => row.Stage, InterviewPreparationStage.Failed), Ct);
        Assert.Equal(0, await _host!.Services.GetRequiredService<InterviewPreparationRequests>().ImportAsync(20, Ct));
        Assert.Equal(1, await CommandsAsync());
    }

    [Fact]
    public async Task Joined_candidate_publisher_does_not_commit_the_callers_transaction()
    {
        await using (var context = await pg.Db.CreateDbContextAsync(Ct))
        {
            await using var transaction = await context.Database.BeginTransactionAsync(Ct);
            await using var scope = await pg.PublicationScopeAsync();
            var outbox = scope.ServiceProvider.GetRequiredService<IDbContextOutbox>();
            outbox.Enroll(context);
            await (await pg.CandidateAsync()).PublishFactsAsync(context, outbox, new CandidateFacts
            {
                Facts = [new() { Key = "hr_about", Question = "Experience?", Answer = "Uncommitted answer", Confirmed = true }],
            }, 1, "joined-test", Ct);
            await context.SaveChangesAsync(Ct);
            await transaction.RollbackAsync(Ct);
        }
        await using var final = await pg.Db.CreateDbContextAsync(Ct);
        Assert.Equal(1, (await final.Documents.SingleAsync(row => row.Key == FactsProvider.Key, Ct)).Version);
        Assert.Single(await final.CandidateRevisions.Where(row => row.Key == FactsProvider.Key).ToListAsync(Ct));
        Assert.Empty(await final.Notifications.ToListAsync(Ct));
    }

    [Fact]
    public async Task Joined_candidate_publisher_requires_a_caller_transaction()
    {
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        var publisher = await pg.CandidateAsync();
        await using var scope = await pg.PublicationScopeAsync();
        var outbox = scope.ServiceProvider.GetRequiredService<IDbContextOutbox>();
        await Assert.ThrowsAsync<InvalidOperationException>(() => publisher.PublishFactsAsync(context, outbox, new CandidateFacts(), 1, "joined-test", Ct));
    }

    [Fact]
    public async Task Batch_position_capture_uses_one_source_pack_and_candidate_witness_for_all_questions()
    {
        var source = await InterviewAsync(pack: true);
        var commands = await SourcesAsync();
        var snapshots = await commands.CapturePositionsAsync(source.Id, ["about", "about", "missing", ""], Ct);
        var snapshot = Assert.Single(snapshots).Value;
        Assert.True(snapshot.Interview.Matches(source));
        Assert.Equal("about", snapshot.Question.Id);
        Assert.Equal(1, snapshot.ProfileVersion);
        Assert.Equal(1, snapshot.FactsVersion);
        Assert.Equal(CanonicalJson.Hash(source.PackJson!), snapshot.PackHash);
        Assert.Empty(await commands.CapturePositionsAsync(source.Id, [], Ct));
    }

    [Fact]
    public async Task Funnel_manual_stage_facade_uses_the_native_owner()
    {
        var source = await InterviewAsync();
        await SourcesAsync();
        Assert.True((await _host!.Services.GetRequiredService<FunnelService>()
            .SetStageAsync(source.ApplicationId, ApplicationStage.HrScreen, "Manual stage", Ct)).Ok);
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        var created = await context.Interviews.SingleAsync(row => row.Kind == InterviewKind.HrScreen, Ct);
        Assert.NotNull(created.PreparationRequestId);
        Assert.Single(await context.InterviewPreparationRequests.ToListAsync(Ct));
        Assert.Equal(1, await CommandsAsync());
    }

    [Fact]
    public async Task Debrief_rejects_a_changed_paid_header_without_closing_or_handing_off()
    {
        var source = await InterviewAsync(pack: true);
        await SourcesAsync();
        var header = Fingerprint.Of(InterviewPrep.Header(source.Application!.Vacancy!.CompanyName, source.Application.Vacancy.Title, source.Kind));
        await using (var context = await pg.Db.CreateDbContextAsync(Ct))
            await context.Vacancies.ExecuteUpdateAsync(update => update.SetProperty(row => row.Title, "Changed role"), Ct);
        await Assert.ThrowsAsync<InterviewEditConflictException>(() => _host!.Services.GetRequiredService<InterviewTransitions>()
            .CompleteDebriefAsync(InterviewEditSnapshot.Capture(source), new DebriefResult { Summary = "Paid summary", Stage = "final" }, Ct, header));
        Assert.Equal(InterviewStatus.Scheduled, (await ReadAsync(source.Id)).Status);
        Assert.Null((await ReadAsync(source.Id)).Debrief);
        Assert.Equal(0, await CommandsAsync());
        await using var final = await pg.Db.CreateDbContextAsync(Ct);
        Assert.Empty(await final.ApplicationEvents.ToListAsync(Ct));
        Assert.Empty(await final.Notifications.ToListAsync(Ct));
    }

    [Fact]
    public async Task Bot_debrief_rejects_a_changed_displayed_header_before_paid_inference_and_preserves_the_transcript()
    {
        var source = await InterviewAsync();
        await SourcesAsync();
        var chat = _host!.Services.GetRequiredService<CareerChat>();
        await chat.AskDebriefAsync(source.Id, Ct);
        var store = _host.Services.GetRequiredService<ConversationStore>();
        var prompt = (await store.GetAsync(Ct))!;
        await using (var context = await pg.Db.CreateDbContextAsync(Ct))
            await context.Vacancies.ExecuteUpdateAsync(update => update.SetProperty(row => row.CompanyName, "Changed company"), Ct);
        var reply = await chat.HandleTextAsync("My interview transcript", Ct);
        Assert.Contains("уже изменено", reply!.Text);
        var preserved = (await store.GetAsync(Ct))!;
        Assert.Equal("My interview transcript", preserved.Draft);
        Assert.Equal(prompt.Id, preserved.Id);
        Assert.Equal(prompt.InterviewHeaderHash, preserved.InterviewHeaderHash);
        await using var final = await pg.Db.CreateDbContextAsync(Ct);
        Assert.Empty(await final.LlmCalls.ToListAsync(Ct));
        Assert.Equal(InterviewStatus.Scheduled, (await ReadAsync(source.Id)).Status);
    }

    [Fact]
    public async Task Bot_time_conflict_preserves_the_text_and_the_original_prompt_witness()
    {
        var source = await InterviewAsync();
        var commands = await SourcesAsync();
        var chat = _host!.Services.GetRequiredService<CareerChat>();
        var store = _host.Services.GetRequiredService<ConversationStore>();
        await chat.AskTimeAsync(source.Id, Ct);
        var prompt = (await store.GetAsync(Ct))!;
        await commands.EditAsync(prompt.Interview!, InterviewKind.SystemDesign, 90, source.ScheduledAt, Ct);
        var reply = await chat.HandleTextAsync("завтра 15:00", Ct);
        Assert.Contains("уже изменено", reply!.Text);
        var preserved = (await store.GetAsync(Ct))!;
        Assert.Equal("завтра 15:00", preserved.Draft);
        Assert.Equal(prompt.Id, preserved.Id);
        Assert.Equal(prompt.Interview!.Source.Hash(), preserved.Interview!.Source.Hash());
        Assert.False(preserved.Interview.Matches(await ReadAsync(source.Id)));
        await chat.HandleTextAsync("завтра 16:00", Ct);
        Assert.Equal("завтра 16:00", (await store.GetAsync(Ct))!.Draft);
        Assert.Equal(InterviewKind.SystemDesign, (await ReadAsync(source.Id)).Kind);
    }

    [Fact]
    public async Task Bot_time_success_commits_preparation_and_clears_only_the_consumed_prompt()
    {
        var source = await InterviewAsync();
        await SourcesAsync();
        var chat = _host!.Services.GetRequiredService<CareerChat>();
        await chat.AskTimeAsync(source.Id, Ct);
        Assert.Contains("🗓", (await chat.HandleTextAsync("завтра 15:00", Ct))!.Text);
        Assert.Null(await _host.Services.GetRequiredService<ConversationStore>().GetAsync(Ct));
        Assert.NotNull((await ReadAsync(source.Id)).PreparationRequestId);
        Assert.Equal(1, await CommandsAsync());
    }

    [Fact]
    public async Task Conversation_compare_exchange_never_erases_a_newer_prompt_even_with_identical_fields()
    {
        await SourcesAsync();
        var store = _host!.Services.GetRequiredService<ConversationStore>();
        var prompt = new ConversationState("debrief", 123, 0, null, [], null);
        await store.SetAsync(prompt, Ct);
        var captured = (await store.GetAsync(Ct))!;
        var draft = captured with { Step = 1, Draft = "Saved transcript" };
        Assert.True(await store.TrySetAsync(captured, draft, Ct));
        Assert.False(await store.TrySetAsync(captured, draft, Ct));
        await store.SetAsync(new ConversationState("debrief", 123, 0, null, [], null), Ct);
        Assert.False(await store.TryClearAsync(draft, Ct));
        Assert.False(await store.TrySetAsync(draft, draft with { Draft = "Late paid result" }, Ct));
        var newer = (await store.GetAsync(Ct))!;
        Assert.NotEqual(captured.Id, newer.Id);
        Assert.Null(newer.Draft);
        Assert.True(await store.TryClearAsync(newer, Ct));
    }

    [Fact]
    public async Task Reminder_flags_and_notices_commit_once_without_revising_preparation()
    {
        var source = await InterviewAsync();
        await SourcesAsync();
        var now = DateTimeOffset.UtcNow;
        await using (var context = await pg.Db.CreateDbContextAsync(Ct))
            await context.Interviews.ExecuteUpdateAsync(update => update.SetProperty(row => row.ScheduledAt, now.AddMinutes(30)), Ct);
        var snapshot = InterviewEditSnapshot.Capture(await ReadAsync(source.Id));
        var job = ActivatorUtilities.CreateInstance<JobFinder.Pipeline.Jobs.InterviewReminderJob>(_host!.Services);
        var results = await Task.WhenAll(job.NotifyAsync(snapshot, now, false, Ct), job.NotifyAsync(snapshot, now, false, Ct));
        Assert.Single(results, result => result);
        Assert.True(snapshot.Matches(await ReadAsync(source.Id)));
        await using var final = await pg.Db.CreateDbContextAsync(Ct);
        var notice = Assert.Single(await final.Notifications.ToListAsync(Ct));
        await NotificationAdmissionAssertions.JoinedAsync(final, notice, Ct);
        Assert.Empty(await final.InterviewPreparationRequests.ToListAsync(Ct));
    }

    [Fact]
    public async Task Reminder_rejects_a_rescheduled_source_without_an_obsolete_notice()
    {
        var source = await InterviewAsync();
        var commands = await SourcesAsync();
        var now = DateTimeOffset.UtcNow;
        var snapshot = InterviewEditSnapshot.Capture(source);
        await commands.SetTimeAsync(snapshot, now.AddDays(3), Ct);
        await using (var context = await pg.Db.CreateDbContextAsync(Ct)) await context.Notifications.ExecuteDeleteAsync(Ct);
        var job = ActivatorUtilities.CreateInstance<JobFinder.Pipeline.Jobs.InterviewReminderJob>(_host!.Services);
        Assert.False(await job.NotifyAsync(snapshot, now, false, Ct));
        await using var final = await pg.Db.CreateDbContextAsync(Ct);
        Assert.Empty(await final.Notifications.ToListAsync(Ct));
        Assert.False((await ReadAsync(source.Id)).Reminded);
    }

    private sealed class Fault : SaveChangesInterceptor
    {
        public bool Enabled { get; set; } = true;
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData data, InterceptionResult<int> result, CancellationToken ct = default)
        {
            if (Enabled && data.Context!.ChangeTracker.Entries<InterviewPreparationRequest>().Any(entry => entry.State == EntityState.Added))
                throw new InvalidOperationException("synthetic interview source commit fault");
            return ValueTask.FromResult(result);
        }
    }
}
