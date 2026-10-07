using System.Text.Json;
using JobFinder.Core.Domain;
using JobFinder.Core.Profile;
using JobFinder.Core.Text;
using JobFinder.Data;
using JobFinder.Llm.Client;
using JobFinder.Pipeline;
using JobFinder.Pipeline.Career;
using JobFinder.Pipeline.Messaging;
using JobFinder.Pipeline.Modules.Candidate;
using JobFinder.Pipeline.Modules.Hiring;
using JobFinder.Pipeline.Notifications;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace JobFinder.Tests.Pipeline;

public sealed class DurableOfferPreparationTests(PostgresFixture pg) : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private readonly List<IHost> _hosts = [];
    private const string Text = "Synthetic Company offers 5000 USD net";

    public async ValueTask InitializeAsync()
    {
        Assert.SkipWhen(pg.SkipReason is not null, pg.SkipReason ?? "");
        await pg.SeedPoliciesAsync();
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        await context.OfferPreparationRequests.ExecuteDeleteAsync(Ct);
        await context.Offers.ExecuteDeleteAsync(Ct);
        await context.EmployerMessages.ExecuteDeleteAsync(Ct);
        await context.ApplicationEvents.ExecuteDeleteAsync(Ct);
        await context.Applications.ExecuteDeleteAsync(Ct);
        await context.Vacancies.ExecuteDeleteAsync(Ct);
        await context.Documents.Where(row => row.Key.StartsWith("candidate.")).ExecuteDeleteAsync(Ct);
        await context.CandidateRevisions.ExecuteDeleteAsync(Ct);
        await context.Settings.Where(row => row.Key == ConversationStore.Key).ExecuteDeleteAsync(Ct);
        await NotificationAdmissionAssertions.ResetAsync(context, Ct);
        await context.Database.ExecuteSqlRawAsync("TRUNCATE jobfinder_queues.wolverine_queue_hiring_prepare, jobfinder_queues.wolverine_queue_hiring_prepare_scheduled, jobfinder_queues.wolverine_queue_preparation, jobfinder_queues.wolverine_queue_preparation_scheduled; DELETE FROM jobfinder_messages.wolverine_incoming_envelopes; DELETE FROM jobfinder_messages.wolverine_outgoing_envelopes; DELETE FROM jobfinder_messages.wolverine_dead_letters", Ct);
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var host in _hosts.AsEnumerable().Reverse())
        {
            try { await host.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(20)); }
            finally { host.Dispose(); }
        }
    }

    private async Task<IHost> HostAsync(Steps steps, bool consumer = false, SaveChangesInterceptor? fault = null, bool seed = true)
    {
        var host = await PostProcessingTestHost.BuildAsync(pg, consumer, fault: fault, configure: services =>
        {
            services.RemoveAll<IOfferPreparationSteps>();
            services.AddSingleton<IOfferPreparationSteps>(steps);
            if (!consumer) services.AddHostedService<SingleInstanceGuard>();
        });
        _hosts.Add(host);
        await host.StartAsync(Ct);
        if (seed)
        {
            var publisher = host.Services.GetRequiredService<ICandidatePublisher>();
            await publisher.PublishProfileAsync(new CandidateProfile { Name = "Synthetic Candidate", Reviewed = true, Summary = "Synthetic facts" }, 0, "synthetic", Ct);
            await publisher.PublishFactsAsync(new CandidateFacts(), 0, "synthetic", Ct);
            await using var context = await pg.Db.CreateDbContextAsync(Ct);
            await NotificationAdmissionAssertions.ResetAsync(context, Ct);
        }
        return host;
    }

    private async Task<ConversationState> DialogueAsync(IHost host)
    {
        var dialogue = new ConversationState(OfferAdvisor.Mode, 0, 0, null, [], null);
        await host.Services.GetRequiredService<ConversationStore>().SetAsync(dialogue, Ct);
        return dialogue;
    }
    private async Task<OfferPreparationRequest> RootAsync(Guid id)
    {
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        return await context.OfferPreparationRequests.AsNoTracking().SingleAsync(row => row.Id == id, Ct);
    }
    private async Task<Application> ApplicationAsync()
    {
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        var application = new Application { Vacancy = new Vacancy { Title = "Senior .NET", CompanyName = "Synthetic Company", Description = "Synthetic vacancy",
            Url = "https://example.invalid/offer", Fingerprint = Guid.NewGuid().ToString("N"), Status = VacancyStatus.Applied },
            Status = ApplicationStatus.Sent, Stage = ApplicationStage.TechInterview, Revision = 4, Channel = ApplicationChannel.Email,
            CoverLetter = "Sent historical text", Target = "synthetic@example.invalid", CreatedAt = DateTimeOffset.UtcNow, SentAt = DateTimeOffset.UtcNow };
        context.Applications.Add(application);
        await context.SaveChangesAsync(Ct);
        return application;
    }

    [Fact]
    public async Task Concurrent_admission_freezes_dialogue_text_and_inputs_and_has_one_native_command()
    {
        var steps = new Steps();
        var host = await HostAsync(steps);
        var conversation = await DialogueAsync(host);
        var requests = host.Services.GetRequiredService<OfferPreparationRequests>();
        var ids = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => requests.RequestAsync(Text, Ct, conversation)));
        Assert.Single(ids.Distinct());
        Assert.NotNull(ids[0]);
        var root = await RootAsync(ids[0]!.Value);
        var input = OfferPreparationRequests.ReadInput(root);
        var waiting = await host.Services.GetRequiredService<ConversationStore>().GetAsync(Ct);
        Assert.Equal(ConversationStore.Capture(input.Source.Conversation!), ConversationStore.Capture(waiting!));
        Assert.Equal(Text, waiting!.Draft);
        Assert.Equal(root.Id, waiting.OfferRequestId);
        Assert.Equal(1, waiting.Step);
        Assert.Contains("maximum", JsonSerializer.Serialize(input.Operation.Policy));
        Assert.DoesNotContain("synthetic-key", root.InputJson);
        Assert.Equal(0, steps.Calls);
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        Assert.Single(await context.OfferPreparationRequests.ToListAsync(Ct));
        Assert.Equal(1, await context.Database.SqlQueryRaw<int>("SELECT count(*)::int AS \"Value\" FROM jobfinder_queues.wolverine_queue_hiring_prepare").SingleAsync(Ct));
        await NotificationAdmissionAssertions.JoinedAsync(context, await context.Notifications.SingleAsync(Ct), Ct);
        Assert.Contains("уже сохранён", (await host.Services.GetRequiredService<CareerChat>().HandleTextAsync("new text", Ct))!.Text);
        Assert.Equal(Text, (await host.Services.GetRequiredService<ConversationStore>().GetAsync(Ct))!.Draft);
    }

    [Fact]
    public async Task Producer_returns_before_paid_work_and_saved_analysis_and_final_card_resume_after_host_restart()
    {
        var steps = new Steps();
        var producer = await HostAsync(steps);
        var application = await ApplicationAsync();
        var conversation = await DialogueAsync(producer);
        Assert.Empty((await producer.Services.GetRequiredService<OfferAdvisor>().AnalyzeAsync(Text, Ct, conversation)).Text);
        var id = (await producer.Services.GetRequiredService<ConversationStore>().GetAsync(Ct))!.OfferRequestId!.Value;
        var workflow = producer.Services.GetRequiredService<OfferPreparationWorkflow>();
        await workflow.AnalyzeAsync(new() { RequestId = id }, Guid.NewGuid(), Ct);
        var checkpoint = await RootAsync(id);
        Assert.Equal(OfferPreparationStage.Publishing, checkpoint.Stage);
        Assert.Equal(1, steps.Calls);
        await producer.StopAsync(Ct);
        var consumer = await HostAsync(steps, consumer: true, seed: false);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(40));
        await consumer.Services.GetRequiredService<OfferPreparationRequests>().WaitAsync(id, timeout.Token);
        Assert.Equal(1, steps.Calls);
        var final = await RootAsync(id);
        Assert.Equal(checkpoint.ResultHash, final.ResultHash);
        Assert.Equal(OfferPreparationStage.Completed, final.Stage);
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        Assert.Equal(final.OfferId, (await context.Offers.SingleAsync(Ct)).Id);
        var saved = await context.Applications.SingleAsync(Ct);
        Assert.Equal(ApplicationStage.Offer, saved.Stage);
        Assert.Equal(application.CoverLetter, saved.CoverLetter);
        Assert.Equal(ApplicationStatus.Sent, saved.Status);
        Assert.Null(await consumer.Services.GetRequiredService<ConversationStore>().GetAsync(Ct));
        var card = await context.Notifications.SingleAsync(row => row.Text.Contains("Контрпредложение"), Ct);
        Assert.Contains("Условия", card.Text);
        Assert.Contains("Риски", card.Text);
        Assert.Contains("скопируйте", card.Text);
        await NotificationAdmissionAssertions.JoinedAsync(context, card, Ct);
        Assert.Empty(await context.EmployerMessages.ToListAsync(Ct));
        Assert.Empty(await context.ApplicationDeliveryRequests.ToListAsync(Ct));
        await consumer.Services.GetRequiredService<OfferPreparationWorkflow>().PublishAsync(new() { RequestId = id }, Guid.NewGuid(), Ct);
        Assert.Single(await context.Offers.ToListAsync(Ct));
        Assert.Equal(1, steps.Calls);
    }

    [Fact]
    public async Task Competing_commands_cannot_pay_twice_and_owner_stop_fences_a_late_paid_result()
    {
        var steps = new Steps { Release = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        var host = await HostAsync(steps);
        var dialogue = await DialogueAsync(host);
        var id = (await host.Services.GetRequiredService<OfferPreparationRequests>().RequestAsync(Text, Ct, dialogue))!.Value;
        var workflow = host.Services.GetRequiredService<OfferPreparationWorkflow>();
        var running = workflow.AnalyzeAsync(new() { RequestId = id }, Guid.NewGuid(), Ct);
        try
        {
            await steps.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);
            await workflow.AnalyzeAsync(new() { RequestId = id }, Guid.NewGuid(), Ct);
            Assert.Equal(1, steps.Calls);
            Assert.Contains("Остановлено", (await host.Services.GetRequiredService<CareerChat>().StopAsync(Ct)).Text);
            Assert.Null(await host.Services.GetRequiredService<ConversationStore>().GetAsync(Ct));
        }
        finally { steps.Release.TrySetResult(); }
        await running;
        Assert.Equal(OfferPreparationStage.Cancelled, (await RootAsync(id)).Stage);
        await workflow.PublishAsync(new() { RequestId = id }, Guid.NewGuid(), Ct);
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        Assert.Empty(await context.Offers.ToListAsync(Ct));
        Assert.Equal(Text, OfferPreparationRequests.ReadInput(await RootAsync(id)).Source.Text);
    }

    [Theory]
    [InlineData("profile")]
    [InlineData("dialogue")]
    [InlineData("application")]
    [InlineData("delivery")]
    public async Task Changed_witness_refuses_before_paid_work_without_overwriting_new_state(string change)
    {
        var steps = new Steps();
        var host = await HostAsync(steps);
        var application = await ApplicationAsync();
        var dialogue = await DialogueAsync(host);
        var id = (await host.Services.GetRequiredService<OfferPreparationRequests>().RequestAsync(Text, Ct, dialogue))!.Value;
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        if (change == "profile")
            await context.Documents.Where(row => row.Key == "candidate.profile").ExecuteUpdateAsync(setters => setters.SetProperty(row => row.Version, row => row.Version + 1), Ct);
        else if (change == "dialogue")
            await host.Services.GetRequiredService<ConversationStore>().SetAsync(new("story", 99, 0, null, [], "new draft"), Ct);
        else if (change == "application")
            await context.Applications.Where(row => row.Id == application.Id).ExecuteUpdateAsync(setters => setters.SetProperty(row => row.Revision, row => row.Revision + 1), Ct);
        else
        {
            context.EmployerMessages.Add(new() { VacancyId = application.VacancyId, ApplicationId = application.Id, Status = EmployerMessageStatus.Ready,
                Id = Guid.NewGuid(), BusinessKey = "synthetic-pending", InputJson = "{}", CreatedAt = DateTimeOffset.UtcNow });
            await context.SaveChangesAsync(Ct);
        }
        var before = await host.Services.GetRequiredService<ConversationStore>().GetAsync(Ct);
        await host.Services.GetRequiredService<OfferPreparationWorkflow>().AnalyzeAsync(new() { RequestId = id }, Guid.NewGuid(), Ct);
        Assert.Equal(0, steps.Calls);
        Assert.Equal(OfferPreparationStage.Cancelled, (await RootAsync(id)).Stage);
        Assert.Equal(ConversationStore.Capture(before!), ConversationStore.Capture((await host.Services.GetRequiredService<ConversationStore>().GetAsync(Ct))!));
        Assert.Empty(await context.Offers.ToListAsync(Ct));
        if (change == "dialogue") Assert.False((await host.Services.GetRequiredService<OfferPreparationRequests>().StopAsync(dialogue, Ct)).Ok);
    }

    [Fact]
    public async Task Outage_preserves_stage_inputs_and_cancellation_of_wait_does_not_cancel_native_work()
    {
        var steps = new Steps { Unavailable = true };
        var host = await HostAsync(steps);
        var requests = host.Services.GetRequiredService<OfferPreparationRequests>();
        var id = (await requests.RequestAsync(Text, Ct))!.Value;
        var workflow = host.Services.GetRequiredService<OfferPreparationWorkflow>();
        await workflow.AnalyzeAsync(new() { RequestId = id }, Guid.NewGuid(), Ct);
        var paused = await RootAsync(id);
        Assert.Equal(OfferPreparationStage.Analyzing, paused.Stage);
        Assert.NotNull(paused.RetryAt);
        Assert.Equal(0, paused.Failures);
        Assert.Null(paused.ClaimEnvelopeId);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => requests.WaitAsync(id, cancelled.Token));
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        await context.OfferPreparationRequests.Where(row => row.Id == id).ExecuteUpdateAsync(setters => setters.SetProperty(row => row.RetryAt, (DateTimeOffset?)null), Ct);
        steps.Unavailable = false;
        await workflow.AnalyzeAsync(new() { RequestId = id }, Guid.NewGuid(), Ct);
        await workflow.PublishAsync(new() { RequestId = id }, Guid.NewGuid(), Ct);
        Assert.Equal(paused.InputHash, (await RootAsync(id)).InputHash);
        Assert.Equal(OfferPreparationStage.Completed, (await RootAsync(id)).Stage);
        Assert.Equal(2, steps.Calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Source_race_during_paid_work_or_after_checkpoint_never_publishes_an_offer(bool afterCheckpoint)
    {
        var steps = new Steps { Release = afterCheckpoint ? null : new(TaskCreationOptions.RunContinuationsAsynchronously) };
        var host = await HostAsync(steps);
        var application = await ApplicationAsync();
        var id = (await host.Services.GetRequiredService<OfferPreparationRequests>().RequestAsync(Text, Ct))!.Value;
        var workflow = host.Services.GetRequiredService<OfferPreparationWorkflow>();
        var running = workflow.AnalyzeAsync(new() { RequestId = id }, Guid.NewGuid(), Ct);
        try
        {
            await steps.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);
            if (afterCheckpoint) await running;
            await using var context = await pg.Db.CreateDbContextAsync(Ct);
            await context.Applications.Where(row => row.Id == application.Id).ExecuteUpdateAsync(setters => setters.SetProperty(row => row.Revision, row => row.Revision + 1), Ct);
        }
        finally { steps.Release?.TrySetResult(); }
        await running;
        await workflow.PublishAsync(new() { RequestId = id }, Guid.NewGuid(), Ct);
        Assert.Equal(1, steps.Calls);
        Assert.Equal(OfferPreparationStage.Cancelled, (await RootAsync(id)).Stage);
        await using var final = await pg.Db.CreateDbContextAsync(Ct);
        Assert.Empty(await final.Offers.ToListAsync(Ct));
        Assert.Empty(await final.ApplicationEvents.ToListAsync(Ct));
        Assert.Equal(ApplicationStage.TechInterview, (await final.Applications.SingleAsync(Ct)).Stage);
    }

    [Fact]
    public async Task Repeated_provider_stage_failure_retires_only_the_request_and_preserves_text_and_original_dialogue()
    {
        var steps = new Steps { Failure = true };
        var host = await HostAsync(steps);
        var dialogue = await DialogueAsync(host);
        var requests = host.Services.GetRequiredService<OfferPreparationRequests>();
        var id = (await requests.RequestAsync(Text, Ct, dialogue))!.Value;
        var workflow = host.Services.GetRequiredService<OfferPreparationWorkflow>();
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        for (var attempt = 0; attempt < 4; attempt++)
        {
            await context.OfferPreparationRequests.Where(row => row.Id == id).ExecuteUpdateAsync(setters => setters.SetProperty(row => row.RetryAt, (DateTimeOffset?)null), Ct);
            await workflow.AnalyzeAsync(new() { RequestId = id }, Guid.NewGuid(), Ct);
        }
        Assert.Equal(OfferPreparationStage.Failed, (await RootAsync(id)).Stage);
        Assert.Equal(4, (await RootAsync(id)).Failures);
        Assert.Equal(Text, (await host.Services.GetRequiredService<ConversationStore>().GetAsync(Ct))!.Draft);
        Assert.Equal(id, await requests.RequestAsync(Text, Ct, dialogue));
        Assert.Equal(4, steps.Calls);
        Assert.Empty(await context.Offers.ToListAsync(Ct));
        Assert.Single(await context.Notifications.Where(row => row.Kind == NotificationKind.Alert).ToListAsync(Ct));
    }

    [Fact]
    public async Task Old_pending_stop_cannot_cancel_a_root_through_a_newer_dialogue()
    {
        var host = await HostAsync(new Steps());
        var dialogue = await DialogueAsync(host);
        var requests = host.Services.GetRequiredService<OfferPreparationRequests>();
        var id = (await requests.RequestAsync(Text, Ct, dialogue))!.Value;
        var waiting = (await host.Services.GetRequiredService<ConversationStore>().GetAsync(Ct))!;
        var newer = new ConversationState("story", 1, 0, null, [], "new text");
        await host.Services.GetRequiredService<ConversationStore>().SetAsync(newer, Ct);
        Assert.False((await requests.StopAsync(waiting, Ct)).Ok);
        Assert.Equal(OfferPreparationStage.Analyzing, (await RootAsync(id)).Stage);
        Assert.Equal(ConversationStore.Capture(newer), ConversationStore.Capture((await host.Services.GetRequiredService<ConversationStore>().GetAsync(Ct))!));
    }

    [Fact]
    public async Task Exhausted_paid_checkpoint_retries_retain_the_analysis_and_never_schedule_another_paid_call()
    {
        var fault = new Fault("checkpoint") { Limit = 3 };
        var steps = new Steps();
        var host = await HostAsync(steps, fault: fault);
        var id = (await host.Services.GetRequiredService<OfferPreparationRequests>().RequestAsync(Text, Ct))!.Value;
        fault.Armed = true;
        var workflow = host.Services.GetRequiredService<OfferPreparationWorkflow>();
        await Assert.ThrowsAsync<OfferPreparationCheckpointException>(() => workflow.AnalyzeAsync(new() { RequestId = id }, Guid.NewGuid(), Ct));
        var failed = await RootAsync(id);
        Assert.Equal(OfferPreparationStage.Failed, failed.Stage);
        Assert.Equal(5000, OfferPreparationRequests.ReadResult(failed).MonthlyNetUsd);
        Assert.Equal(3, fault.Failures);
        await workflow.AnalyzeAsync(new() { RequestId = id }, Guid.NewGuid(), Ct);
        Assert.Equal(1, steps.Calls);
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        Assert.Empty(await context.Offers.ToListAsync(Ct));
        Assert.Single(await context.Notifications.Where(row => row.Kind == NotificationKind.Alert).ToListAsync(Ct));
    }

    [Fact]
    public async Task Producer_before_any_consumer_is_recovered_by_the_real_native_queue()
    {
        var steps = new Steps();
        var producer = await HostAsync(steps);
        var id = (await producer.Services.GetRequiredService<OfferPreparationRequests>().RequestAsync(Text, Ct))!.Value;
        await producer.StopAsync(Ct);
        var consumer = await HostAsync(steps, consumer: true, seed: false);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(40));
        await consumer.Services.GetRequiredService<OfferPreparationRequests>().WaitAsync(id, timeout.Token);
        Assert.Equal(1, steps.Calls);
        Assert.Equal(OfferPreparationStage.Completed, (await RootAsync(id)).Stage);
    }

    [Theory]
    [InlineData("admission")]
    [InlineData("checkpoint")]
    [InlineData("publication")]
    public async Task Atomic_admission_and_checkpoint_and_final_publication_retain_the_same_retry_witness(string boundary)
    {
        var fault = new Fault(boundary);
        var steps = new Steps();
        var host = await HostAsync(steps, fault: fault);
        var dialogue = await DialogueAsync(host);
        var requests = host.Services.GetRequiredService<OfferPreparationRequests>();
        fault.Armed = true;
        if (boundary == "admission")
        {
            await Assert.ThrowsAsync<IOException>(() => requests.RequestAsync(Text, Ct, dialogue));
            await using var before = await pg.Db.CreateDbContextAsync(Ct);
            Assert.Empty(await before.OfferPreparationRequests.ToListAsync(Ct));
            Assert.Equal(ConversationStore.Capture(dialogue), ConversationStore.Capture((await host.Services.GetRequiredService<ConversationStore>().GetAsync(Ct))!));
            Assert.Empty(await before.Notifications.ToListAsync(Ct));
        }
        var id = (await requests.RequestAsync(Text, Ct, dialogue))!.Value;
        var workflow = host.Services.GetRequiredService<OfferPreparationWorkflow>();
        await workflow.AnalyzeAsync(new() { RequestId = id }, Guid.NewGuid(), Ct);
        await workflow.PublishAsync(new() { RequestId = id }, Guid.NewGuid(), Ct);
        if (boundary == "publication")
        {
            Assert.Equal(OfferPreparationStage.Publishing, (await RootAsync(id)).Stage);
            await using var before = await pg.Db.CreateDbContextAsync(Ct);
            Assert.Empty(await before.Offers.ToListAsync(Ct));
            Assert.NotNull(await host.Services.GetRequiredService<ConversationStore>().GetAsync(Ct));
            await before.OfferPreparationRequests.Where(row => row.Id == id).ExecuteUpdateAsync(setters => setters.SetProperty(row => row.RetryAt, (DateTimeOffset?)null), Ct);
            await workflow.PublishAsync(new() { RequestId = id }, Guid.NewGuid(), Ct);
        }
        Assert.Equal(1, fault.Failures);
        Assert.Equal(1, steps.Calls);
        Assert.Equal(OfferPreparationStage.Completed, (await RootAsync(id)).Stage);
        await using var final = await pg.Db.CreateDbContextAsync(Ct);
        Assert.Single(await final.Offers.ToListAsync(Ct));
        foreach (var notice in await final.Notifications.ToListAsync(Ct)) await NotificationAdmissionAssertions.JoinedAsync(final, notice, Ct);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Damaged_input_or_result_is_quarantined_once_without_paid_replay(bool result)
    {
        var steps = new Steps();
        var host = await HostAsync(steps);
        var id = (await host.Services.GetRequiredService<OfferPreparationRequests>().RequestAsync(Text, Ct))!.Value;
        var workflow = host.Services.GetRequiredService<OfferPreparationWorkflow>();
        if (result) await workflow.AnalyzeAsync(new() { RequestId = id }, Guid.NewGuid(), Ct);
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        if (result) await context.OfferPreparationRequests.Where(row => row.Id == id).ExecuteUpdateAsync(setters => setters.SetProperty(row => row.ResultJson, "{}"), Ct);
        else await context.OfferPreparationRequests.Where(row => row.Id == id).ExecuteUpdateAsync(setters => setters.SetProperty(row => row.InputJson, "{}"), Ct);
        Task Run() => result ? workflow.PublishAsync(new() { RequestId = id }, Guid.NewGuid(), Ct) : workflow.AnalyzeAsync(new() { RequestId = id }, Guid.NewGuid(), Ct);
        await Assert.ThrowsAsync<UnsupportedOfferPreparationException>(Run);
        await Run();
        Assert.Equal(result ? 1 : 0, steps.Calls);
        Assert.Equal(OfferPreparationStage.Failed, (await RootAsync(id)).Stage);
        Assert.Empty(await context.Offers.ToListAsync(Ct));
        Assert.Single(await context.Notifications.Where(row => row.Kind == NotificationKind.Alert).ToListAsync(Ct));
    }

    [Theory]
    [InlineData("root")]
    [InlineData("incoming")]
    [InlineData("outgoing")]
    [InlineData("dead-letter")]
    [InlineData("queue")]
    [InlineData("scheduled-queue")]
    public async Task Downgrade_refuses_retained_roots_and_orphaned_future_offer_envelopes(string evidence)
    {
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        if (evidence == "root")
        {
            var host = await HostAsync(new Steps());
            await host.Services.GetRequiredService<OfferPreparationRequests>().RequestAsync(Text, Ct);
        }
        else
        {
            var id = Guid.NewGuid();
            const string type = "jobfinder.hiring.publish-offer.v99";
            if (evidence == "queue") await context.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO jobfinder_queues.wolverine_queue_hiring_prepare (id,body,message_type) VALUES ({id},{Array.Empty<byte>()},{type})", Ct);
            else if (evidence == "scheduled-queue") await context.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO jobfinder_queues.wolverine_queue_hiring_prepare_scheduled (id,body,message_type,execution_time) VALUES ({id},{Array.Empty<byte>()},{type},{DateTimeOffset.UtcNow.AddDays(1)})", Ct);
            else if (evidence == "incoming") await context.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO jobfinder_messages.wolverine_incoming_envelopes (id,status,owner_id,body,message_type) VALUES ({id},'Scheduled',0,{Array.Empty<byte>()},{type})", Ct);
            else if (evidence == "outgoing") await context.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO jobfinder_messages.wolverine_outgoing_envelopes (id,owner_id,destination,body,message_type) VALUES ({id},0,'postgresql://preparation/',{Array.Empty<byte>()},{type})", Ct);
            else await context.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO jobfinder_messages.wolverine_dead_letters (id,body,message_type) VALUES ({id},{Array.Empty<byte>()},{type})", Ct);
        }
        var error = await Assert.ThrowsAsync<Npgsql.PostgresException>(() => MigrationTestRunner.DowngradeAndRestoreAsync(context, "20261003232114_VersionedOfferPublication", Ct));
        Assert.Contains("Cannot roll back offer preparation", error.MessageText);
        Assert.Contains("20261003235353_DurableOfferPreparation", await context.Database.GetAppliedMigrationsAsync(Ct));
    }

    private sealed class Steps : IOfferPreparationSteps
    {
        public int Calls;
        public bool Unavailable;
        public bool Failure;
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource? Release;
        public void EnsureCompatible(OfferPreparationInput input) { }
        public async Task<OfferPreparationResult> AnalyzeAsync(OfferPreparationInput input, CancellationToken ct)
        {
            Interlocked.Increment(ref Calls);
            Entered.TrySetResult();
            if (Release is not null) await Release.Task.WaitAsync(ct);
            if (Unavailable) throw new LlmUnavailableException("synthetic unavailable", false);
            if (Failure) throw new InvalidOperationException("synthetic model-stage failure");
            var analysis = new JobFinder.Llm.Tasks.OfferAnalysis { Company = "Synthetic Company", Verdict = "Synthetic assessment", MonthlyAmount = 5000, Currency = "USD", Gross = false,
                Terms = [new() { Name = "Work", Value = "Remote" }], Risks = ["Verify contract"], CounterTarget = "5500 USD", WalkAway = "4800 USD", CounterScript = "Synthetic counteroffer" };
            return new() { Analysis = analysis, MonthlyNetUsd = input.Conversion.Convert(5000, "USD", false) };
        }
    }
    private sealed class Fault(string boundary) : SaveChangesInterceptor
    {
        public bool Armed;
        public int Failures;
        public int Limit = 1;
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData, InterceptionResult<int> result, CancellationToken ct = default)
        {
            var context = eventData.Context as JobFinderDbContext;
            if (Armed && Failures < Limit && context is not null && (boundary == "admission" && context.ChangeTracker.Entries<OfferPreparationRequest>().Any(entry => entry.State == EntityState.Added)
                || boundary == "checkpoint" && context.ChangeTracker.Entries<OfferPreparationRequest>().Any(entry => entry.State == EntityState.Modified && entry.Entity.Stage == OfferPreparationStage.Publishing)
                || boundary == "publication" && context.ChangeTracker.Entries<Offer>().Any(entry => entry.State == EntityState.Added)))
            {
                Failures++;
                throw new IOException("synthetic offer boundary fault");
            }
            return base.SavingChangesAsync(eventData, result, ct);
        }
    }
}
