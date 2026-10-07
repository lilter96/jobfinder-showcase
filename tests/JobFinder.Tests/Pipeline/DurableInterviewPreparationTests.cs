using System.Security.Cryptography;
using System.Net;
using System.Text.Json;
using JobFinder.Core.Domain;
using JobFinder.Core.Messaging;
using JobFinder.Core.Options;
using JobFinder.Core.Profile;
using JobFinder.Data;
using JobFinder.Llm.Client;
using JobFinder.Llm.Tasks;
using JobFinder.Integrations.Http;
using JobFinder.Pipeline.Career;
using JobFinder.Pipeline.Modules.Candidate;
using JobFinder.Pipeline.Modules.Preparation;
using JobFinder.Pipeline.Profile;
using JobFinder.Pipeline;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using UglyToad.PdfPig.Writer;
using Wolverine;
using Wolverine.Postgresql;

namespace JobFinder.Tests.Pipeline;

public sealed partial class DurableInterviewPreparationTests(PostgresFixture pg) : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private readonly List<IHost> _hosts = [];

    public async ValueTask InitializeAsync()
    {
        Assert.SkipWhen(pg.SkipReason is not null, pg.SkipReason ?? "");
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        await context.InterviewPreparationRequests.ExecuteDeleteAsync(Ct);
        await context.Interviews.ExecuteDeleteAsync(Ct);
        await context.Applications.ExecuteDeleteAsync(Ct);
        await context.Vacancies.ExecuteDeleteAsync(Ct);
        await context.Stories.ExecuteDeleteAsync(Ct);
        await NotificationAdmissionAssertions.ResetAsync(context, Ct);
        await context.GeneratedAnswers.ExecuteDeleteAsync(Ct);
        await context.Database.ExecuteSqlRawAsync("TRUNCATE jobfinder_queues.wolverine_queue_preparation, jobfinder_queues.wolverine_queue_preparation_scheduled; DELETE FROM jobfinder_messages.wolverine_incoming_envelopes; DELETE FROM jobfinder_messages.wolverine_outgoing_envelopes; DELETE FROM jobfinder_messages.wolverine_dead_letters", Ct);
        var publisher = await pg.CandidateAsync();
        var profileVersion = await context.Documents.Where(row => row.Key == ProfileProvider.Key).Select(row => (long?)row.Version).SingleOrDefaultAsync(Ct) ?? 0;
        await publisher.PublishProfileAsync(new CandidateProfile { Name = "Synthetic Candidate", Title = "Backend", Reviewed = true }, profileVersion, "interview-test", Ct);
        var factsVersion = await context.Documents.Where(row => row.Key == FactsProvider.Key).Select(row => (long?)row.Version).SingleOrDefaultAsync(Ct) ?? 0;
        await publisher.PublishFactsAsync(new CandidateFacts { Facts = [new() { Key = "location", Question = "Location?", Answer = "Minsk", Confirmed = true }] }, factsVersion, "interview-test", Ct);
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var host in _hosts.AsEnumerable().Reverse())
        {
            try { await host.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(20)); }
            finally { host.Dispose(); }
        }
    }

    private async Task<IHost> HostAsync(bool consumer, Steps steps, SaveChangesInterceptor? fault = null, bool owned = false, Action<IServiceCollection>? configure = null)
    {
        var host = await PostProcessingTestHost.BuildAsync(pg, consumer, fault: fault, configure: services =>
        {
            if (owned && !consumer) services.AddHostedService<SingleInstanceGuard>();
            services.AddSingleton<IInterviewPreparationSteps>(provider => { steps.Paths = provider.GetRequiredService<PathsOptions>(); return steps; });
            configure?.Invoke(services);
        });
        _hosts.Add(host);
        await host.StartAsync(Ct);
        return host;
    }

    private async Task<Interview> InterviewAsync()
    {
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        var interview = new Interview
        {
            Kind = InterviewKind.Technical, Status = InterviewStatus.Scheduled, CreatedAt = DateTimeOffset.UtcNow, ScheduledAt = DateTimeOffset.UtcNow.AddDays(2),
            Application = new Application { Status = ApplicationStatus.Sent, CreatedAt = DateTimeOffset.UtcNow,
                Vacancy = new Vacancy { Title = "Backend Developer", CompanyName = "Synthetic company", Description = "C# backend service", Skills = ["C#"],
                    Url = "https://vacancy.invalid/1", Fingerprint = Guid.NewGuid().ToString("N"), FirstSeenAt = DateTimeOffset.UtcNow } },
        };
        context.Interviews.Add(interview);
        await context.SaveChangesAsync(Ct);
        return interview;
    }

    private async Task<Guid> RequestAsync(IHost host, long interviewId, bool force = false) =>
        (await host.Services.GetRequiredService<InterviewPreparationRequests>().RequestAsync(interviewId, Ct, force))!.Value;
    private async Task<InterviewPreparationRequest> RootAsync(Guid id)
    {
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        return await context.InterviewPreparationRequests.AsNoTracking().SingleAsync(row => row.Id == id, Ct);
    }
    private async Task WakeAsync(IHost host, Guid id)
    {
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        await context.InterviewPreparationRequests.Where(row => row.Id == id).ExecuteUpdateAsync(update => update.SetProperty(row => row.RetryAt, (DateTimeOffset?)null), Ct);
        var root = await RootAsync(id);
        await host.Services.GetRequiredService<IMessageBus>().PublishAsync(new PrepareInterview { RequestId = id, Stage = root.Stage });
    }
    private static async Task UntilAsync(Func<Task<bool>> condition)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(45);
        while (!await condition())
        {
            if (DateTimeOffset.UtcNow > deadline) throw new TimeoutException("native interview checkpoint did not arrive");
            await Task.Delay(100, Ct);
        }
    }

    [Fact]
    public async Task Producer_pack_facade_cancellation_only_stops_waiting_not_the_native_request()
    {
        var steps = new Steps();
        var producer = await HostAsync(false, steps);
        var interview = await InterviewAsync();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var waiting = producer.Services.GetRequiredService<InterviewPrep>().BuildPackAsync(interview.Id, cancellation.Token);
        await UntilAsync(async () =>
        {
            await using var context = await pg.Db.CreateDbContextAsync(Ct);
            return await context.InterviewPreparationRequests.AnyAsync(Ct);
        });
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
        Assert.Equal((0, 0, 0), (steps.Plans, steps.Answers, steps.Prints));
        var consumer = await HostAsync(true, steps);
        await consumer.Services.GetRequiredService<InterviewPrep>().BuildPackAsync(interview.Id, Ct).WaitAsync(TimeSpan.FromSeconds(45), Ct);
        Assert.Equal((1, 1, 1), (steps.Plans, steps.Answers, steps.Prints));
        await using var final = await pg.Db.CreateDbContextAsync(Ct);
        Assert.Single(await final.InterviewPreparationRequests.ToListAsync(Ct));
        Assert.NotNull((await final.Interviews.SingleAsync(Ct)).PackPdfPath);
        var notice = await final.Notifications.SingleAsync(row => row.EntityId == interview.Id && row.AttachmentPath != null, Ct);
        await NotificationAdmissionAssertions.JoinedAsync(final, notice, Ct);
    }

    [Fact]
    public async Task Actual_cli_entry_uses_the_producer_graph_and_warm_native_artifact()
    {
        var steps = new Steps();
        var producer = await HostAsync(false, steps);
        var interview = await InterviewAsync();
        Assert.Equal(2, await JobFinder.App.Cli.CliHost.InterviewAsync(producer.Services, "bad", false, Ct));
        var pending = JobFinder.App.Cli.CliHost.InterviewAsync(producer.Services, interview.Id.ToString(), false, Ct);
        var sharedPaths = producer.Services.GetRequiredService<PathsOptions>();
        var consumer = await HostAsync(true, steps, configure: services => services.AddSingleton(sharedPaths));
        Assert.Equal(0, await pending.WaitAsync(TimeSpan.FromSeconds(45), Ct));
        Assert.Equal(0, await JobFinder.App.Cli.CliHost.InterviewAsync(producer.Services, interview.Id.ToString(), false, Ct));
        Assert.Equal((1, 1, 1), (steps.Plans, steps.Answers, steps.Prints));
        Assert.Equal(0, await JobFinder.App.Cli.CliHost.InterviewAsync(consumer.Services, interview.Id.ToString(), true, Ct)
            .WaitAsync(TimeSpan.FromSeconds(45), Ct));
        Assert.Equal((2, 2, 2), (steps.Plans, steps.Answers, steps.Prints));
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        var source = await context.Interviews.AsNoTracking().SingleAsync(Ct);
        await File.WriteAllTextAsync(StoredPath.Resolve(source.PackPdfPath!), "damaged artifact", Ct);
        await Assert.ThrowsAsync<UnsupportedInterviewPreparationException>(() =>
            JobFinder.App.Cli.CliHost.InterviewAsync(producer.Services, interview.Id.ToString(), false, Ct));
        Assert.Equal((2, 2, 2), (steps.Plans, steps.Answers, steps.Prints));
    }

    [Fact]
    public async Task Duplicate_admission_and_replayed_stage_commands_publish_one_pack_and_notice()
    {
        var steps = new Steps();
        var producer = await HostAsync(false, steps);
        var interview = await InterviewAsync();
        var ids = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => RequestAsync(producer, interview.Id)));
        var id = Assert.Single(ids.Distinct());
        var consumer = await HostAsync(true, steps);
        await consumer.Services.GetRequiredService<InterviewPreparationRequests>().WaitAsync(id, Ct).WaitAsync(TimeSpan.FromSeconds(45), Ct);
        foreach (var stage in new[] { InterviewPreparationStage.Capturing, InterviewPreparationStage.Planning, InterviewPreparationStage.Answering, InterviewPreparationStage.Printing, InterviewPreparationStage.Publishing })
            await consumer.Services.GetRequiredService<IMessageBus>().PublishAsync(new PrepareInterview { RequestId = id, Stage = stage });
        await Task.Delay(1500, Ct);
        Assert.Equal((1, 1, 1), (steps.Plans, steps.Answers, steps.Prints));
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        var source = await context.Interviews.AsNoTracking().SingleAsync(row => row.Id == interview.Id, Ct);
        Assert.NotNull(source.PackJson);
        Assert.Equal(id, source.PreparationRequestId);
        Assert.Equal(1, await context.Notifications.CountAsync(row => row.EntityId == interview.Id && row.AttachmentPath != null, Ct));
        Assert.Equal(InterviewPreparationStage.Completed, (await RootAsync(id)).Stage);
    }

    [Fact]
    public async Task Answer_outage_resumes_saved_capture_and_plan_without_consuming_the_failure_budget()
    {
        var steps = new Steps { UnavailableAnswers = true };
        var host = await HostAsync(true, steps);
        var interview = await InterviewAsync();
        var id = await RequestAsync(host, interview.Id);
        await UntilAsync(async () => (await RootAsync(id)).RetryAt != null);
        var saved = await RootAsync(id);
        Assert.Equal(InterviewPreparationStage.Answering, saved.Stage);
        Assert.NotNull(saved.InputJson);
        Assert.NotNull(saved.PlanJson);
        Assert.Equal(0, saved.Failures);
        steps.UnavailableAnswers = false;
        await WakeAsync(host, id);
        await UntilAsync(async () => (await RootAsync(id)).Stage == InterviewPreparationStage.Completed);
        var result = await RootAsync(id);
        Assert.Equal(saved.InputJson, result.InputJson);
        Assert.Equal(saved.PlanJson, result.PlanJson);
        Assert.Equal((1, 2, 1), (steps.Plans, steps.Answers, steps.Prints));
    }

    [Fact]
    public async Task Missing_candidate_is_a_saved_prerequisite_pause_then_capture_resumes()
    {
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        var row = await context.Documents.AsNoTracking().SingleAsync(row => row.Key == ProfileProvider.Key, Ct);
        var profile = ProfileProvider.FromJson(row.Content);
        profile.Reviewed = false;
        var publisher = await pg.CandidateAsync();
        var unreviewed = await publisher.PublishProfileAsync(profile, row.Version, "interview-test", Ct);
        var steps = new Steps();
        var host = await HostAsync(true, steps);
        var interview = await InterviewAsync();
        var id = await RequestAsync(host, interview.Id);
        await UntilAsync(async () => (await RootAsync(id)).RetryAt != null);
        var saved = await RootAsync(id);
        Assert.Equal(InterviewPreparationStage.Capturing, saved.Stage);
        Assert.Equal(0, saved.Failures);
        Assert.Equal(0, steps.Plans);
        profile.Reviewed = true;
        await publisher.PublishProfileAsync(profile, unreviewed.Version, "interview-test", Ct);
        await WakeAsync(host, id);
        await UntilAsync(async () => (await RootAsync(id)).Stage == InterviewPreparationStage.Completed);
    }

    [Theory]
    [InlineData("time")]
    [InlineData("kind")]
    [InlineData("candidate")]
    [InlineData("facts")]
    [InlineData("vacancy")]
    [InlineData("conversation")]
    public async Task Inputs_changed_during_paid_work_cancel_instead_of_publishing_a_late_pack(string mutation)
    {
        var steps = new Steps { HoldAnswers = true };
        var host = await HostAsync(true, steps);
        var interview = await InterviewAsync();
        var id = await RequestAsync(host, interview.Id);
        await steps.Entered.Task.WaitAsync(TimeSpan.FromSeconds(45), Ct);
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        if (mutation == "time") await context.Interviews.Where(row => row.Id == interview.Id).ExecuteUpdateAsync(update => update.SetProperty(row => row.ScheduledAt, DateTimeOffset.UtcNow.AddDays(3)), Ct);
        if (mutation == "kind") await context.Interviews.Where(row => row.Id == interview.Id).ExecuteUpdateAsync(update => update.SetProperty(row => row.Kind, InterviewKind.Final), Ct);
        if (mutation == "vacancy") await context.Vacancies.Where(row => row.Id == interview.Application!.VacancyId).ExecuteUpdateAsync(update => update.SetProperty(row => row.Description, "Changed source"), Ct);
        if (mutation == "conversation")
        {
            context.ApplicationEvents.Add(new() { ApplicationId = interview.ApplicationId, At = DateTimeOffset.UtcNow, Kind = EventKind.MessageIn, Text = "Changed conversation" });
            await context.SaveChangesAsync(Ct);
        }
        var publisher = await pg.CandidateAsync();
        if (mutation == "candidate")
        {
            var row = await context.Documents.AsNoTracking().SingleAsync(row => row.Key == ProfileProvider.Key, Ct);
            var profile = ProfileProvider.FromJson(row.Content);
            profile.Name = "Changed candidate";
            await publisher.PublishProfileAsync(profile, row.Version, "interview-test", Ct);
        }
        if (mutation == "facts")
        {
            var row = await context.Documents.AsNoTracking().SingleAsync(row => row.Key == FactsProvider.Key, Ct);
            var facts = JsonSerializer.Deserialize(row.Content, FactsJsonContext.Default.CandidateFacts)!;
            facts.Facts[0].Answer = "Changed fact";
            await publisher.PublishFactsAsync(facts, row.Version, "interview-test", Ct);
        }
        steps.Release.TrySetResult();
        await UntilAsync(async () => (await RootAsync(id)).Stage == InterviewPreparationStage.Cancelled);
        var source = await context.Interviews.AsNoTracking().SingleAsync(row => row.Id == interview.Id, Ct);
        Assert.Null(source.PackJson);
        Assert.Null(source.PreparationRequestId);
        Assert.Equal(1, source.PreparationRevision);
        Assert.Equal(0, steps.Prints);
        Assert.Equal(1, await context.Notifications.CountAsync(row => row.EntityId == interview.Id && row.AttachmentPath == null, Ct));
    }

    [Fact]
    public async Task Legacy_import_admits_without_waiting_for_paid_preparation_and_does_not_repeat_owned_work()
    {
        var steps = new Steps { HoldAnswers = true };
        var host = await HostAsync(true, steps);
        var source = await InterviewAsync();
        var requests = host.Services.GetRequiredService<InterviewPreparationRequests>();
        Assert.Equal(1, await requests.ImportAsync(20, Ct).WaitAsync(TimeSpan.FromSeconds(10), Ct));
        await steps.Entered.Task.WaitAsync(TimeSpan.FromSeconds(45), Ct);
        Assert.Equal(0, await requests.ImportAsync(20, Ct));
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        var id = (await context.Interviews.AsNoTracking().SingleAsync(row => row.Id == source.Id, Ct)).PreparationRequestId!.Value;
        Assert.Null((await context.Interviews.AsNoTracking().SingleAsync(row => row.Id == source.Id, Ct)).PackJson);
        steps.HoldAnswers = false;
        steps.Release.TrySetResult();
        await UntilAsync(async () => (await RootAsync(id)).Stage == InterviewPreparationStage.Completed);
        Assert.Equal(1, steps.Plans);
        Assert.Equal(1, steps.Prints);
    }

    [Fact]
    public async Task Paused_oldest_root_does_not_starve_the_next_legacy_source()
    {
        var steps = new Steps { UnavailableAnswers = true };
        var host = await HostAsync(true, steps);
        var first = await InterviewAsync();
        var second = await InterviewAsync();
        var requests = host.Services.GetRequiredService<InterviewPreparationRequests>();
        Assert.Equal(1, await requests.ImportAsync(1, Ct));
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        var firstId = (await context.Interviews.AsNoTracking().SingleAsync(row => row.Id == first.Id, Ct)).PreparationRequestId!.Value;
        await UntilAsync(async () => (await RootAsync(firstId)).RetryAt != null);
        Assert.Equal(1, await requests.ImportAsync(1, Ct));
        var secondId = (await context.Interviews.AsNoTracking().SingleAsync(row => row.Id == second.Id, Ct)).PreparationRequestId!.Value;
        Assert.NotEqual(firstId, secondId);
        await UntilAsync(async () => (await RootAsync(secondId)).RetryAt != null);
        Assert.Equal(0, (await RootAsync(firstId)).Failures);
        Assert.Equal(0, (await RootAsync(secondId)).Failures);
        Assert.Equal(0, await requests.ImportAsync(1, Ct));
        Assert.Equal(2, steps.Plans);
        Assert.Equal(0, steps.Prints);
    }

    [Fact]
    public async Task Scheduling_publishes_a_root_consumed_by_the_native_interview_executor()
    {
        var steps = new Steps();
        var host = await HostAsync(true, steps);
        var old = await InterviewAsync();
        await host.Services.GetRequiredService<InterviewTransitions>().SetStageAsync(old.ApplicationId, ApplicationStage.HrScreen, "Invitation", Ct);
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        var source = await context.Interviews.AsNoTracking().SingleAsync(row => row.ApplicationId == old.ApplicationId && row.Kind == InterviewKind.HrScreen, Ct);
        var id = source.PreparationRequestId!.Value;
        await UntilAsync(async () => (await RootAsync(id)).Stage == InterviewPreparationStage.Completed);
        source = await context.Interviews.AsNoTracking().SingleAsync(row => row.Id == source.Id, Ct);
        Assert.NotNull(source.PackJson);
        Assert.NotNull(source.PackPdfPath);
        Assert.Equal(id, source.PreparationRequestId);
        Assert.Equal(ApplicationStage.HrScreen, (await context.Applications.AsNoTracking().SingleAsync(row => row.Id == old.ApplicationId, Ct)).Stage);
        Assert.Equal(1, steps.Plans);
        Assert.Equal(1, steps.Prints);
        Assert.Equal(1, await context.Notifications.CountAsync(row => row.EntityId == source.Id && row.AttachmentPath != null, Ct));
    }

    [Fact]
    public async Task Debrief_retires_inflight_work_and_the_next_interview_publishes_once()
    {
        var steps = new Steps { HoldAnswers = true };
        var host = await HostAsync(true, steps);
        var interview = await InterviewAsync();
        var first = await RequestAsync(host, interview.Id);
        await steps.Entered.Task.WaitAsync(TimeSpan.FromSeconds(45), Ct);
        var snapshot = (await host.Services.GetRequiredService<InterviewSources>().CaptureAsync(interview.Id, Ct))!;
        await host.Services.GetRequiredService<InterviewTransitions>().CompleteDebriefAsync(snapshot,
            new DebriefResult { Summary = "Interview completed", QuestionsAsked = ["Transactions"], Stage = "final" }, Ct);
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        var next = await context.Interviews.AsNoTracking().SingleAsync(row => row.ApplicationId == interview.ApplicationId && row.Kind == InterviewKind.Final, Ct);
        var second = next.PreparationRequestId!.Value;
        Assert.Equal(InterviewPreparationStage.Cancelled, (await RootAsync(first)).Stage);
        steps.HoldAnswers = false;
        steps.Release.TrySetResult();
        await UntilAsync(async () => (await RootAsync(second)).Stage == InterviewPreparationStage.Completed);
        var closed = await context.Interviews.AsNoTracking().SingleAsync(row => row.Id == interview.Id, Ct);
        Assert.Equal(InterviewStatus.Done, closed.Status);
        Assert.Null(closed.PreparationRequestId);
        Assert.Null(closed.PackJson);
        Assert.Contains("Transactions", closed.Debrief);
        Assert.Equal(1, steps.Prints);
        Assert.Equal(1, await context.Notifications.CountAsync(row => row.EntityId == next.Id && row.AttachmentPath != null, Ct));
        Assert.Equal(0, await context.Notifications.CountAsync(row => row.EntityId == interview.Id && row.AttachmentPath != null, Ct));
    }

    [Fact]
    public async Task Source_edit_replaces_inflight_preparation_and_only_the_new_owner_publishes()
    {
        var steps = new Steps { HoldAnswers = true };
        var host = await HostAsync(true, steps);
        var interview = await InterviewAsync();
        var first = await RequestAsync(host, interview.Id);
        await steps.Entered.Task.WaitAsync(TimeSpan.FromSeconds(45), Ct);
        var commands = host.Services.GetRequiredService<InterviewSources>();
        var snapshot = (await commands.CaptureAsync(interview.Id, Ct))!;
        await commands.EditAsync(snapshot, InterviewKind.Final, 90, snapshot.Source.ScheduledAt, Ct);
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        var second = (await context.Interviews.AsNoTracking().SingleAsync(row => row.Id == interview.Id, Ct)).PreparationRequestId!.Value;
        Assert.NotEqual(first, second);
        Assert.Equal(InterviewPreparationStage.Cancelled, (await RootAsync(first)).Stage);
        steps.HoldAnswers = false;
        steps.Release.TrySetResult();
        await UntilAsync(async () => (await RootAsync(second)).Stage == InterviewPreparationStage.Completed);
        var source = await context.Interviews.AsNoTracking().SingleAsync(row => row.Id == interview.Id, Ct);
        Assert.Equal(InterviewKind.Final, source.Kind);
        Assert.Equal(90, source.DurationMinutes);
        Assert.Equal(second, source.PreparationRequestId);
        Assert.Equal(InterviewPreparationStage.Cancelled, (await RootAsync(first)).Stage);
        Assert.Equal(1, await context.Notifications.CountAsync(row => row.EntityId == interview.Id && row.AttachmentPath != null, Ct));
        Assert.Equal(1, steps.Prints);
    }

    [Fact]
    public async Task Regeneration_supersedes_an_inflight_generation_and_only_the_new_root_can_publish()
    {
        var steps = new Steps { HoldAnswers = true };
        var host = await HostAsync(true, steps);
        var interview = await InterviewAsync();
        var first = await RequestAsync(host, interview.Id);
        await steps.Entered.Task.WaitAsync(TimeSpan.FromSeconds(45), Ct);
        var second = await RequestAsync(host, interview.Id, force: true);
        steps.HoldAnswers = false;
        steps.Release.TrySetResult();
        Assert.NotEqual(first, second);
        await UntilAsync(async () => (await RootAsync(second)).Stage == InterviewPreparationStage.Completed);
        Assert.Equal(InterviewPreparationStage.Cancelled, (await RootAsync(first)).Stage);
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        Assert.Equal(second, (await context.Interviews.AsNoTracking().SingleAsync(row => row.Id == interview.Id, Ct)).PreparationRequestId);
        Assert.Equal(1, await context.Notifications.CountAsync(row => row.EntityId == interview.Id && row.AttachmentPath != null, Ct));
    }

    [Fact]
    public async Task Ordinary_failures_are_durable_across_retries_and_retire_once_after_three()
    {
        var steps = new Steps { FailPlans = true };
        var host = await HostAsync(true, steps);
        var interview = await InterviewAsync();
        var id = await RequestAsync(host, interview.Id);
        for (var count = 1; count <= 3; count++)
        {
            var expected = count;
            await UntilAsync(async () => (await RootAsync(id)).Failures == expected);
            if (count < 3) await WakeAsync(host, id);
        }
        Assert.Equal(InterviewPreparationStage.Failed, (await RootAsync(id)).Stage);
        Assert.Equal(3, steps.Plans);
        await WakeAsync(host, id);
        await Task.Delay(1000, Ct);
        Assert.Equal(3, steps.Plans);
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        Assert.Equal(1, await context.Notifications.CountAsync(row => row.EntityId == interview.Id, Ct));
    }

    [Theory]
    [InlineData("admission")]
    [InlineData("publication")]
    public async Task Business_state_root_command_and_notice_roll_back_together_on_commit_fault(string phase)
    {
        var steps = new Steps();
        var host = await HostAsync(phase != "admission", steps, new Fault(phase));
        var interview = await InterviewAsync();
        if (phase == "admission")
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => RequestAsync(host, interview.Id));
            await using var context = await pg.Db.CreateDbContextAsync(Ct);
            Assert.Empty(await context.InterviewPreparationRequests.ToListAsync(Ct));
            Assert.Null((await context.Interviews.AsNoTracking().SingleAsync(row => row.Id == interview.Id, Ct)).PreparationRequestId);
        }
        else
        {
            var id = await RequestAsync(host, interview.Id);
            await UntilAsync(async () => (await RootAsync(id)).RetryAt != null);
            await using var context = await pg.Db.CreateDbContextAsync(Ct);
            Assert.Equal(InterviewPreparationStage.Publishing, (await RootAsync(id)).Stage);
            Assert.Null((await context.Interviews.AsNoTracking().SingleAsync(row => row.Id == interview.Id, Ct)).PackJson);
            Assert.Equal(0, await context.Notifications.CountAsync(row => row.EntityId == interview.Id && row.AttachmentPath != null, Ct));
            await WakeAsync(host, id);
            await UntilAsync(async () => (await RootAsync(id)).Stage == InterviewPreparationStage.Completed);
            Assert.Equal((1, 1, 1), (steps.Plans, steps.Answers, steps.Prints));
        }
    }

    private async Task RunStageAsync(IHost host, Guid id, InterviewPreparationStage stage, int version = 1) =>
        await host.Services.GetRequiredService<InterviewPreparationWorkflow>().RunAsync(new() { RequestId = id, Stage = stage, Version = version }, Guid.NewGuid(), Ct);

    private async Task ReachPublishingAsync(IHost host, Guid id)
    {
        foreach (var stage in new[] { InterviewPreparationStage.Capturing, InterviewPreparationStage.Planning, InterviewPreparationStage.Answering, InterviewPreparationStage.Printing })
            await RunStageAsync(host, id, stage);
        Assert.Equal(InterviewPreparationStage.Publishing, (await RootAsync(id)).Stage);
    }

    [Fact]
    public async Task Damaged_committed_pdf_reprints_saved_copilot_without_replanning_or_answering()
    {
        var steps = new Steps();
        var host = await HostAsync(false, steps, owned: true);
        var interview = await InterviewAsync();
        var id = await RequestAsync(host, interview.Id);
        await ReachPublishingAsync(host, id);
        var saved = await RootAsync(id);
        await File.WriteAllTextAsync(StoredPath.Resolve(saved.PdfPath!), "damaged bytes", Ct);
        await RunStageAsync(host, id, InterviewPreparationStage.Publishing);
        var rewound = await RootAsync(id);
        Assert.Equal(InterviewPreparationStage.Printing, rewound.Stage);
        Assert.Equal(saved.CopilotJson, rewound.CopilotJson);
        Assert.Null(rewound.PdfHash);
        await RunStageAsync(host, id, InterviewPreparationStage.Printing);
        await RunStageAsync(host, id, InterviewPreparationStage.Publishing);
        Assert.Equal(InterviewPreparationStage.Completed, (await RootAsync(id)).Stage);
        Assert.Equal((1, 1, 2), (steps.Plans, steps.Answers, steps.Prints));
        await host.Services.GetRequiredService<InterviewPreparationRequests>().WaitAsync(id, Ct);
    }

    [Fact]
    public async Task Unsupported_late_command_cannot_retire_a_newer_stage()
    {
        var host = await HostAsync(false, new Steps(), owned: true);
        var interview = await InterviewAsync();
        var id = await RequestAsync(host, interview.Id);
        await RunStageAsync(host, id, InterviewPreparationStage.Capturing);
        await Assert.ThrowsAsync<UnsupportedInterviewPreparationException>(() => RunStageAsync(host, id, InterviewPreparationStage.Capturing, 2));
        Assert.Equal(InterviewPreparationStage.Planning, (await RootAsync(id)).Stage);
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        Assert.Equal(0, await context.Notifications.CountAsync(row => row.EntityId == interview.Id, Ct));
    }

    [Theory]
    [InlineData("seed")]
    [InlineData("input")]
    [InlineData("command")]
    public async Task Damaged_or_unsupported_generation_retires_once_without_paid_work(string damage)
    {
        var steps = new Steps();
        var host = await HostAsync(false, steps, owned: true);
        var interview = await InterviewAsync();
        var id = await RequestAsync(host, interview.Id);
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        var stage = InterviewPreparationStage.Capturing;
        if (damage == "seed") await context.InterviewPreparationRequests.Where(row => row.Id == id).ExecuteUpdateAsync(update => update.SetProperty(row => row.SeedHash, "invalid"), Ct);
        if (damage == "input")
        {
            await RunStageAsync(host, id, stage);
            stage = InterviewPreparationStage.Planning;
            await context.InterviewPreparationRequests.Where(row => row.Id == id).ExecuteUpdateAsync(update => update.SetProperty(row => row.InputHash, "invalid"), Ct);
        }
        await Assert.ThrowsAsync<UnsupportedInterviewPreparationException>(() => RunStageAsync(host, id, stage, damage == "command" ? 2 : 1));
        Assert.Equal(InterviewPreparationStage.Failed, (await RootAsync(id)).Stage);
        await RunStageAsync(host, id, stage);
        Assert.Equal((0, 0, 0), (steps.Plans, steps.Answers, steps.Prints));
        Assert.Equal(1, await context.Notifications.CountAsync(row => row.EntityId == interview.Id, Ct));
    }

    [Fact]
    public async Task Publication_requires_a_complete_saved_question_set_and_cannot_certify_an_empty_pack()
    {
        var steps = new Steps();
        var host = await HostAsync(false, steps, owned: true);
        var interview = await InterviewAsync();
        var id = await RequestAsync(host, interview.Id);
        await ReachPublishingAsync(host, id);
        var saved = await RootAsync(id);
        var copilot = JsonSerializer.Deserialize(saved.CopilotJson!, InterviewPreparationJsonContext.Default.InterviewCopilot)!;
        copilot.Preparation.Clear();
        var json = JsonSerializer.Serialize(copilot, InterviewPreparationJsonContext.Default.InterviewCopilot);
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        await context.InterviewPreparationRequests.Where(row => row.Id == id).ExecuteUpdateAsync(update => update.SetProperty(row => row.CopilotJson, json)
            .SetProperty(row => row.CopilotHash, JobFinder.Core.Text.CanonicalJson.Hash(json)), Ct);
        await Assert.ThrowsAsync<UnsupportedInterviewPreparationException>(() => RunStageAsync(host, id, InterviewPreparationStage.Publishing));
        Assert.Null((await context.Interviews.AsNoTracking().SingleAsync(row => row.Id == interview.Id, Ct)).PackJson);
        Assert.Equal(InterviewPreparationStage.Failed, (await RootAsync(id)).Stage);
    }

    [Fact]
    public async Task Root_cost_includes_recorded_failed_or_partially_cached_attempts()
    {
        var steps = new Steps();
        var host = await HostAsync(false, steps, owned: true);
        var interview = await InterviewAsync();
        var id = await RequestAsync(host, interview.Id);
        await RunStageAsync(host, id, InterviewPreparationStage.Capturing);
        await RunStageAsync(host, id, InterviewPreparationStage.Planning);
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        context.LlmCalls.Add(new() { OperationId = id, Purpose = "Interview", Model = "synthetic", CostUsd = 0.42m, Ok = false, At = DateTimeOffset.UtcNow });
        await context.SaveChangesAsync(Ct);
        await RunStageAsync(host, id, InterviewPreparationStage.Answering);
        var root = await RootAsync(id);
        Assert.Equal(0.42m, JsonSerializer.Deserialize(root.CopilotJson!, InterviewPreparationJsonContext.Default.InterviewCopilot)!.CostUsd);
    }

    [Fact]
    public async Task Captured_optional_research_uses_standard_html_parser_and_is_not_repeated_after_answer_outage()
    {
        var pages = new Pages();
        var steps = new Steps { UnavailableAnswers = true };
        var host = await HostAsync(true, steps, configure: services => ConfigurePages(services, pages));
        var interview = await InterviewAsync();
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        var vacancy = await context.Vacancies.SingleAsync(row => row.Id == interview.Application!.VacancyId, Ct);
        vacancy.Company = new() { Name = "Synthetic company", Site = "https://company.invalid/about" };
        await context.SaveChangesAsync(Ct);
        var id = await RequestAsync(host, interview.Id);
        await UntilAsync(async () => (await RootAsync(id)).RetryAt != null);
        var input = InterviewPreparationRequests.ReadCheckpoint((await RootAsync(id)).InputJson, (await RootAsync(id)).InputHash, InterviewPreparationJsonContext.Default.InterviewPreparationInput);
        Assert.Contains("Company research details", input.CompanyResearch);
        Assert.DoesNotContain("SCRIPT CONTENT", input.CompanyResearch);
        Assert.DoesNotContain("FOOTER CONTENT", input.CompanyResearch);
        Assert.Equal(1, pages.Calls);
        steps.UnavailableAnswers = false;
        await WakeAsync(host, id);
        await UntilAsync(async () => (await RootAsync(id)).Stage == InterviewPreparationStage.Completed);
        Assert.Equal(1, pages.Calls);
    }

    [Fact]
    public async Task Research_does_not_hold_candidate_locks_and_a_concurrent_publication_fences_the_capture()
    {
        var pages = new Pages { Hold = true };
        var steps = new Steps();
        var host = await HostAsync(true, steps, configure: services => ConfigurePages(services, pages));
        var interview = await InterviewAsync();
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        var vacancy = await context.Vacancies.SingleAsync(row => row.Id == interview.Application!.VacancyId, Ct);
        vacancy.Company = new() { Name = "Synthetic company", Site = "https://company.invalid/about" };
        await context.SaveChangesAsync(Ct);
        var id = await RequestAsync(host, interview.Id);
        await pages.Entered.Task.WaitAsync(TimeSpan.FromSeconds(45), Ct);
        var row = await context.Documents.AsNoTracking().SingleAsync(row => row.Key == ProfileProvider.Key, Ct);
        var profile = ProfileProvider.FromJson(row.Content);
        profile.Name = "Changed while researching";
        await (await pg.CandidateAsync()).PublishProfileAsync(profile, row.Version, "interview-test", Ct).WaitAsync(TimeSpan.FromSeconds(10), Ct);
        pages.Release.TrySetResult();
        await UntilAsync(async () => (await RootAsync(id)).Stage == InterviewPreparationStage.Cancelled);
        Assert.Equal(0, steps.Plans);
    }

    private static void ConfigurePages(IServiceCollection services, Pages pages)
    {
        services.RemoveAll<PoliteHttp>();
        services.AddSingleton(new PoliteHttp(new HttpClient(pages, disposeHandler: false), new ScrapeOptions { MinDelayMs = 1, MaxDelayMs = 1 }, TimeProvider.System));
    }

    private sealed class Pages : HttpMessageHandler
    {
        private int _calls;
        public int Calls => Volatile.Read(ref _calls);
        public bool Hold { get; set; }
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Assert.Equal("company.invalid", request.RequestUri!.Host);
            Interlocked.Increment(ref _calls);
            if (Hold) { Entered.TrySetResult(); await Release.Task.WaitAsync(ct); }
            return new(HttpStatusCode.OK) { Content = new StringContent("<html><body><main>Company research details</main><script>SCRIPT CONTENT</script><footer>FOOTER CONTENT</footer></body></html>", System.Text.Encoding.UTF8, "text/html") };
        }
    }

    private sealed class Fault(string phase) : SaveChangesInterceptor
    {
        private int _failed;
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData data, InterceptionResult<int> result, CancellationToken ct = default)
        {
            if (data.Context!.ChangeTracker.Entries<InterviewPreparationRequest>().Any(entry => phase == "admission" ? entry.State == EntityState.Added
                : entry.State == EntityState.Modified && entry.Entity.Stage == InterviewPreparationStage.Completed) && Interlocked.Exchange(ref _failed, 1) == 0)
                throw new InvalidOperationException("synthetic interview commit fault");
            return ValueTask.FromResult(result);
        }
    }

    private sealed class Steps : IInterviewPreparationSteps
    {
        public PathsOptions Paths { get; set; } = new();
        public bool UnavailableAnswers { get; set; }
        public bool HoldAnswers { get; set; }
        public bool FailPlans { get; set; }
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _plans, _answers, _prints;
        public int Plans => Volatile.Read(ref _plans);
        public int Answers => Volatile.Read(ref _answers);
        public int Prints => Volatile.Read(ref _prints);
        public Task<InterviewPlanCheckpoint> PlanAsync(InterviewPreparationSeed seed, InterviewPreparationInput input, CancellationToken ct)
        {
            Interlocked.Increment(ref _plans);
            if (FailPlans) throw new InvalidOperationException("synthetic plan failure");
            return Task.FromResult(new InterviewPlanCheckpoint { Model = "synthetic", Plan = new() { Role = "Backend", OverallRisk = "low",
                Questions = Enumerable.Range(1, 6).Select(index => new PlannedQuestion { Id = "question-" + index, Question = "Question " + index, Category = "hr" }).ToList() } });
        }
        public async Task<InterviewCopilot> AnswerAsync(InterviewPreparationSeed seed, InterviewPreparationInput input, InterviewPlanCheckpoint checkpoint, CancellationToken ct)
        {
            Interlocked.Increment(ref _answers);
            if (HoldAnswers) { Entered.TrySetResult(); await Release.Task.WaitAsync(ct); }
            if (UnavailableAnswers) throw new LlmUnavailableException("synthetic answer outage");
            var (questions, _) = CopilotRules.Normalize(checkpoint.Plan, input.AnswerSource.Catalog);
            var items = questions.Select(question => new PrepItem { Q = question, A = new() { Start = "Synthetic start", Core = "Synthetic core", EvidenceLevel = "verified" } }).ToList();
            return new() { Version = InterviewCopilot.CurrentVersion, GeneratedAt = seed.RenderedAt, Role = checkpoint.Plan.Role, OverallRisk = checkpoint.Plan.OverallRisk,
                Preparation = items.Where(item => item.Q.Id != CopilotRules.SalaryId).ToList(), Salary = items.Single(item => item.Q.Id == CopilotRules.SalaryId) };
        }
        public async Task<InterviewPrintCheckpoint> PrintAsync(InterviewPreparationSeed seed, InterviewPreparationInput input, InterviewCopilot copilot, CancellationToken ct)
        {
            Interlocked.Increment(ref _prints);
            var builder = new PdfDocumentBuilder();
            builder.AddPage(PageSize.A4).AddText("Synthetic interview pack", 12, new PdfPoint(40, 800), builder.AddStandard14Font(Standard14Font.Helvetica));
            var bytes = builder.Build();
            var path = Path.GetFullPath(Path.Combine(Paths.Data, "interviews", seed.Source.Id.ToString(System.Globalization.CultureInfo.InvariantCulture), seed.Operation.OperationId.ToString("N"), "pack.pdf"));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllBytesAsync(path, bytes, ct);
            return new() { Path = StoredPath.ToStored(path), Hash = Convert.ToHexStringLower(SHA256.HashData(bytes)) };
        }
    }
}
