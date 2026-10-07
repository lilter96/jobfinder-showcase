using System.Collections.Concurrent;
using System.Text.Json;
using JobFinder.Core.Domain;
using JobFinder.Core.Messaging;
using JobFinder.Core.Profile;
using JobFinder.Data;
using JobFinder.Integrations.Email;
using JobFinder.Integrations.HeadHunter;
using JobFinder.Llm;
using JobFinder.Llm.Configuration;
using JobFinder.MessagingProbe;
using JobFinder.Pipeline;
using JobFinder.Pipeline.Career;
using JobFinder.Pipeline.Documents;
using JobFinder.Pipeline.Messaging;
using JobFinder.Pipeline.Modules.Candidate;
using JobFinder.Pipeline.Modules.Discovery;
using JobFinder.Pipeline.Modules.Hiring;
using JobFinder.Pipeline.Settings;
using JobFinder.Pipeline.Workers;
using JobFinder.Pipeline.Notifications;
using JobFinder.Pipeline.Actions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Npgsql;
using Wolverine;
using Wolverine.EntityFrameworkCore;

namespace JobFinder.Tests.Pipeline;

public sealed partial class DurableApplicationDeliveryIntegrationTests(PostgresFixture pg) : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(45);
    private readonly List<IHost> _hosts = [];
    private readonly List<Gate> _gates = [];
    private readonly CandidateProfile _profile = new() { Name = "Synthetic Candidate", Title = "Senior .NET Developer", Reviewed = true };
    private readonly CandidateFacts _facts = new() { Facts = [new CandidateFact { Key = "notice", Question = "Notice?", Answer = "Two weeks", Confirmed = true }] };

    public async ValueTask InitializeAsync()
    {
        Assert.SkipWhen(pg.SkipReason is not null, pg.SkipReason ?? "");
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        await context.Database.MigrateAsync(Ct);
        await context.CommentDeliveryAttempts.ExecuteDeleteAsync(Ct);
        await context.CommentDeliveryRequests.ExecuteDeleteAsync(Ct);
        await context.CommentSourceRequests.ExecuteDeleteAsync(Ct);
        await context.CommentPreparationRequests.ExecuteDeleteAsync(Ct);
        await context.CommentDrafts.ExecuteDeleteAsync(Ct);
        await context.ApplicationBlockRevisions.ExecuteDeleteAsync(Ct);
        await context.DeliveryAttempts.ExecuteDeleteAsync(Ct);
        await context.ApplicationDeliveryRequests.ExecuteDeleteAsync(Ct);
        await context.Applications.ExecuteDeleteAsync(Ct);
        await context.Vacancies.ExecuteDeleteAsync(Ct);
        await context.CompanyContacts.ExecuteDeleteAsync(Ct);
        await context.PolicyRevisions.ExecuteDeleteAsync(Ct);
        await context.CandidateRevisions.ExecuteDeleteAsync(Ct);
        await context.Documents.ExecuteDeleteAsync(Ct);
        await context.Settings.ExecuteDeleteAsync(Ct);
        await NotificationAdmissionAssertions.ResetAsync(context, Ct);
        foreach (var queue in DurableQueues.All)
        {
            var cleanup = $"DELETE FROM jobfinder_queues.wolverine_queue_{queue}; DELETE FROM jobfinder_queues.wolverine_queue_{queue}_scheduled";
            await context.Database.ExecuteSqlRawAsync(cleanup, Ct);
        }
        await context.Database.ExecuteSqlRawAsync("DELETE FROM jobfinder_messages.wolverine_incoming_envelopes; DELETE FROM jobfinder_messages.wolverine_outgoing_envelopes; DELETE FROM jobfinder_messages.wolverine_dead_letters", Ct);
        using var bootstrap = await BuildAsync(false, new Gate(), new Recipient());
        await bootstrap.StartAsync(Ct);
        try
        {
            await bootstrap.Services.GetRequiredService<RuntimeSettings>().Automation.ReadAsync(Ct);
            await bootstrap.Services.GetRequiredService<LlmPolicyStore>().ReadAsync(Ct);
            await bootstrap.Services.GetRequiredService<SearchPolicyStore>().ReadAsync(Ct);
            await bootstrap.Services.GetRequiredService<JobFinder.Pipeline.Modules.Engagement.EngagementPolicyStore>().ReadAsync(Ct);
            var publisher = bootstrap.Services.GetRequiredService<ICandidatePublisher>();
            await publisher.PublishProfileAsync(_profile, 0, "synthetic-test", Ct);
            await publisher.PublishFactsAsync(_facts, 0, "synthetic-test", Ct);
        }
        finally { await bootstrap.StopAsync(CancellationToken.None); }
        await using var setup = await pg.Db.CreateDbContextAsync(Ct);
        await NotificationAdmissionAssertions.ResetAsync(setup, Ct);
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var gate in _gates) gate.Release.TrySetResult();
        foreach (var host in _hosts.AsEnumerable().Reverse())
        {
            try { await host.StopAsync(CancellationToken.None).WaitAsync(Timeout); }
            finally { host.Dispose(); }
        }
    }

    private async Task<IHost> BuildAsync(bool consumer, Gate gate, Recipient recipient, IInterceptor? interceptor = null, Controls? controls = null)
    {
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { DisableDefaults = true });
        await ConfigureAsync(builder, consumer, gate, recipient, interceptor, controls);
        return builder.Build();
    }

    private async Task ConfigureAsync(IHostApplicationBuilder builder, bool consumer, Gate gate, Recipient recipient,
        IInterceptor? interceptor = null, Controls? controls = null)
    {
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        var config = new Dictionary<string, string?>
        {
            ["Database:ConnectionString"] = context.Database.GetConnectionString(), ["Paths:Prompts"] = Fixture.PromptsDir,
            ["Paths:Data"] = Path.Combine(OperatingSystem.IsWindows() ? @"E:\Temp" : Path.GetTempPath(), "jobfinder-application-synthetic"),
            ["Llm:Endpoint"] = "https://example.invalid/llm", ["Llm:ApiKey"] = "synthetic-test-key", ["Decision:TimeZone"] = "Europe/Minsk",
            ["HeadHunter:Enabled"] = "false", ["Habr:Enabled"] = "false", ["Threads:Enabled"] = "false",
        };
        foreach (var purpose in LlmPurpose.All)
        {
            config[$"Llm:Routes:{purpose}:0:Name"] = "synthetic/" + purpose.ToLowerInvariant();
            config[$"Llm:Routes:{purpose}:0:Reasoning:Effort"] = "max";
        }
        builder.Configuration.AddInMemoryCollection(config);
        builder.Logging.AddSimpleConsole(options => options.SingleLine = true).SetMinimumLevel(LogLevel.Warning);
        builder.Services.AddJobFinderCore(builder.Configuration);
        JobFinder.Bot.BotServices.AddJobFinderBot(builder.Services, builder.Configuration, withLoops: false);
        builder.Services.RemoveAll<IDbContextFactory<JobFinderDbContext>>();
        if (interceptor is null) builder.Services.AddSingleton(pg.Db);
        else
        {
            var options = new DbContextOptionsBuilder<JobFinderDbContext>();
            JobFinderDbContext.Configure(options, context.Database.GetConnectionString()!);
            options.AddInterceptors(interceptor);
            builder.Services.AddSingleton<IDbContextFactory<JobFinderDbContext>>(new Factory(options.Options));
        }
        builder.Services.RemoveAll<IApplicationDeliveryGate>();
        builder.Services.AddSingleton<IApplicationDeliveryGate>(gate);
        builder.Services.RemoveAll<IApplicationDeliveryControls>();
        builder.Services.AddSingleton<IApplicationDeliveryControls>(controls ?? new Controls());
        builder.Services.RemoveAll<IDeliveryTransport>();
        builder.Services.AddSingleton<IDeliveryTransport>(recipient);
        builder.Services.RemoveAll<JobFinder.Pipeline.Modules.Engagement.ICommentDeliveryTransport>();
        builder.Services.AddSingleton<JobFinder.Pipeline.Modules.Engagement.ICommentDeliveryTransport>(new DormantCommentTransport());
        builder.Services.RemoveAll<IHttpClientFactory>();
        builder.Services.AddSingleton<IHttpClientFactory>(new NoNetwork());
        builder.AddJobFinderMessaging(consumer);
        builder.Services.ConfigureWolverine(ResumeProbe.FastRecovery);
        if (consumer) builder.Services.AddJobFinderInitialization();
    }

    private async Task<IHost> HostAsync(bool consumer, Gate gate, Recipient recipient, IInterceptor? interceptor = null, Controls? controls = null)
    {
        _gates.Add(gate);
        var host = await BuildAsync(consumer, gate, recipient, interceptor, controls);
        _hosts.Add(host);
        await host.StartAsync(Ct).WaitAsync(Timeout, Ct);
        return host;
    }

    private async Task StopAsync(IHost host)
    {
        await host.StopAsync(Ct).WaitAsync(Timeout, Ct);
        _hosts.Remove(host);
        host.Dispose();
    }

    private async Task<Application> SeedAsync(ApplicationMode mode = ApplicationMode.Approved, ApplicationChannel channel = ApplicationChannel.HeadHunter)
    {
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        var now = DateTimeOffset.UtcNow;
        var vacancy = new Vacancy
        {
            Title = "Synthetic .NET Developer", CompanyName = "Synthetic Company", Url = "https://example.invalid/jobs/1",
            Fingerprint = Guid.NewGuid().ToString("N"), Status = VacancyStatus.Scored, Score = 95,
            Decision = mode == ApplicationMode.Auto ? Decision.AutoApply : Decision.Approval, UpdatedAt = now,
        };
        var application = new Application
        {
            Vacancy = vacancy, Channel = channel, Mode = mode, Status = ApplicationStatus.Pending,
            CoverLetter = "Здравствуйте!\n\nПишу по вакансии .NET Developer.\n\nРазработал проверяемый сервис.\n\nГотов обсудить задачи.",
            Target = channel == ApplicationChannel.Email ? "hr@example.invalid" : channel == ApplicationChannel.TelegramDm ? "@synthetic_hr" : null,
            CreatedAt = now,
        };
        context.Applications.Add(application);
        await context.SaveChangesAsync(Ct);
        return application;
    }

    private async Task<ApplicationDeliveryRequest> RequestAsync(Guid id)
    {
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        return await context.ApplicationDeliveryRequests.AsNoTracking().SingleAsync(row => row.Id == id, Ct);
    }

    private async Task<Guid> EnqueueAsync(IHost host, Application application) =>
        Assert.IsType<Guid>(await host.Services.GetRequiredService<ApplicationDeliveryRequests>().RequestAsync(application.Id, Ct));

    private static async Task UntilAsync(Func<Task<bool>> condition)
    {
        var deadline = DateTimeOffset.UtcNow.Add(Timeout);
        while (!await condition())
        {
            Assert.True(DateTimeOffset.UtcNow < deadline, "application did not reach the expected durable checkpoint");
            await Task.Delay(50, Ct);
        }
    }

    [Fact]
    public async Task Producer_exit_preserves_one_request_and_consumer_delivers_exact_frozen_intent()
    {
        var application = await SeedAsync();
        var recipient = new Recipient();
        var producer = await HostAsync(false, new Gate(), recipient);
        var requests = producer.Services.GetRequiredService<ApplicationDeliveryRequests>();
        var ids = await Task.WhenAll(Enumerable.Range(0, 3).Select(_ => requests.RequestAsync(application.Id, Ct)));
        var id = Assert.IsType<Guid>(ids[0]);
        Assert.All(ids, value => Assert.Equal(id, value));
        var captured = await RequestAsync(id);
        var input = ApplicationDeliverySources.Read(captured);
        Assert.Equal(application.CoverLetter, input.Text);
        Assert.DoesNotContain("synthetic-test-key", captured.InputJson);
        Assert.DoesNotContain("example.invalid/llm", captured.InputJson);
        Assert.NotEqual(Guid.Empty, input.Operation.OperationId);
        Assert.Empty(recipient.Deliveries);
        await StopAsync(producer);
        await HostAsync(true, new Gate(), recipient);
        await UntilAsync(async () => (await RequestAsync(id)).Stage == ApplicationDeliveryStage.Completed);
        var packet = Assert.Single(recipient.Deliveries);
        Assert.Equal(id, packet.ApplicationDeliveryRequestId);
        Assert.Equal(input.Text, packet.Text);
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        var attempt = await context.DeliveryAttempts.SingleAsync(Ct);
        Assert.Equal(id, attempt.ApplicationDeliveryRequestId);
        Assert.Equal(DeliveryAttemptStatus.Confirmed, attempt.Status);
        Assert.Equal(ApplicationStatus.Sent, (await context.Applications.SingleAsync(Ct)).Status);
        Assert.Equal(VacancyStatus.Applied, (await context.Vacancies.SingleAsync(Ct)).Status);
        var notice = Assert.Single(await context.Notifications.Where(row => row.Kind == NotificationKind.AutoApplied).ToListAsync(Ct));
        await NotificationAdmissionAssertions.JoinedAsync(context, notice, Ct);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Source_creation_and_native_handoff_commit_or_roll_back_together(bool commit)
    {
        var recipient = new Recipient();
        var producer = await HostAsync(false, new Gate(), recipient);
        var application = NewApplication();
        Guid id;
        await using (var scope = producer.Services.CreateAsyncScope())
        await using (var context = await pg.Db.CreateDbContextAsync(Ct))
        {
            await using var transaction = await context.Database.BeginTransactionAsync(Ct);
            var outbox = scope.ServiceProvider.GetRequiredService<IDbContextOutbox>();
            outbox.Enroll(context);
            context.Applications.Add(application);
            var requests = producer.Services.GetRequiredService<ApplicationDeliveryRequests>();
            id = await requests.QueueAsync(context, outbox, application, Ct);
            Assert.Equal(id, application.DeliveryRequestId);
            Assert.Equal(0, application.Revision);
            Assert.Equal(id, await requests.QueueAsync(context, outbox, application, Ct));
            if (commit) await outbox.SaveChangesAndFlushMessagesAsync(Ct);
        }
        await StopAsync(producer);
        await using (var read = await pg.Db.CreateDbContextAsync(Ct))
        {
            Assert.Equal(commit ? 1 : 0, await read.Applications.CountAsync(Ct));
            Assert.Equal(commit ? 1 : 0, await read.Vacancies.CountAsync(Ct));
            Assert.Equal(commit ? 1 : 0, await read.ApplicationDeliveryRequests.CountAsync(Ct));
        }
        var consumer = await HostAsync(true, new Gate(), recipient);
        if (commit)
        {
            await UntilAsync(async () => (await RequestAsync(id)).Stage == ApplicationDeliveryStage.Completed);
            Assert.Single(recipient.Deliveries);
        }
        else
        {
            await StopAsync(consumer);
            Assert.Empty(recipient.Deliveries);
            await using var read = await pg.Db.CreateDbContextAsync(Ct);
            Assert.Equal(0, await read.Database.SqlQueryRaw<int>("SELECT count(*)::int AS \"Value\" FROM jobfinder_queues.wolverine_queue_hiring_deliver").SingleAsync(Ct));
        }
    }

    [Fact]
    public async Task Invalid_candidate_at_source_handoff_rolls_back_the_new_application_too()
    {
        var producer = await HostAsync(false, new Gate(), new Recipient());
        var publisher = producer.Services.GetRequiredService<ICandidatePublisher>();
        _profile.Reviewed = false;
        await publisher.PublishProfileAsync(_profile, 1, "synthetic-test", Ct);
        await using (var scope = producer.Services.CreateAsyncScope())
        await using (var context = await pg.Db.CreateDbContextAsync(Ct))
        {
            await using var transaction = await context.Database.BeginTransactionAsync(Ct);
            var outbox = scope.ServiceProvider.GetRequiredService<IDbContextOutbox>();
            outbox.Enroll(context);
            var application = NewApplication();
            context.Applications.Add(application);
            await Assert.ThrowsAsync<ApplicationDeliverySupersededException>(() => producer.Services.GetRequiredService<ApplicationDeliveryRequests>().QueueAsync(context, outbox, application, Ct));
        }
        await using var read = await pg.Db.CreateDbContextAsync(Ct);
        Assert.Empty(await read.Applications.ToListAsync(Ct));
        Assert.Empty(await read.Vacancies.ToListAsync(Ct));
        Assert.Empty(await read.ApplicationDeliveryRequests.ToListAsync(Ct));
    }

    [Fact]
    public async Task Native_root_save_failure_rolls_back_the_already_inserted_source_and_command()
    {
        var fault = new RootFault();
        var producer = await HostAsync(false, new Gate(), new Recipient(), fault);
        var factory = producer.Services.GetRequiredService<IDbContextFactory<JobFinderDbContext>>();
        await using (var scope = producer.Services.CreateAsyncScope())
        await using (var context = await factory.CreateDbContextAsync(Ct))
        {
            await using var transaction = await context.Database.BeginTransactionAsync(Ct);
            var outbox = scope.ServiceProvider.GetRequiredService<IDbContextOutbox>();
            outbox.Enroll(context);
            var application = NewApplication();
            context.Applications.Add(application);
            await producer.Services.GetRequiredService<ApplicationDeliveryRequests>().QueueAsync(context, outbox, application, Ct);
            Assert.True(application.Id > 0);
            await Assert.ThrowsAsync<IOException>(() => outbox.SaveChangesAndFlushMessagesAsync(Ct));
        }
        await using var read = await pg.Db.CreateDbContextAsync(Ct);
        Assert.Equal(1, fault.Failures);
        Assert.Empty(await read.Applications.ToListAsync(Ct));
        Assert.Empty(await read.Vacancies.ToListAsync(Ct));
        Assert.Empty(await read.ApplicationDeliveryRequests.ToListAsync(Ct));
        Assert.Equal(0, await read.Database.SqlQueryRaw<int>("SELECT count(*)::int AS \"Value\" FROM jobfinder_queues.wolverine_queue_hiring_deliver").SingleAsync(Ct));
    }

    private static Application NewApplication() => new()
    {
        Vacancy = new Vacancy
        {
            Title = "Synthetic .NET Developer", CompanyName = "Synthetic Company", Url = "https://example.invalid/atomic",
            Fingerprint = Guid.NewGuid().ToString("N"), Status = VacancyStatus.Scored, Decision = Decision.Approval,
            UpdatedAt = DateTimeOffset.UtcNow,
        },
        Channel = ApplicationChannel.HeadHunter, Mode = ApplicationMode.Approved, Status = ApplicationStatus.Pending,
        CoverLetter = "Здравствуйте!\n\nПишу по вакансии .NET Developer.\n\nРазработал проверяемый сервис.\n\nГотов обсудить задачи.",
        CreatedAt = DateTimeOffset.UtcNow,
    };

    [Fact]
    public async Task Duplicate_envelopes_and_replays_do_not_repeat_external_effect()
    {
        var application = await SeedAsync();
        var recipient = new Recipient();
        var host = await HostAsync(true, new Gate(), recipient);
        var id = await EnqueueAsync(host, application);
        var bus = host.Services.GetRequiredService<IMessageBus>();
        for (var index = 0; index < 4; index++)
        {
            await bus.PublishAsync(new PrepareApplicationDelivery { RequestId = id });
            await bus.PublishAsync(new DispatchApplicationDelivery { RequestId = id });
        }
        await UntilAsync(async () => (await RequestAsync(id)).Stage == ApplicationDeliveryStage.Completed);
        var workflow = host.Services.GetRequiredService<ApplicationDeliveryWorkflow>();
        await workflow.DispatchAsync(id, 1, Guid.NewGuid(), Ct);
        Assert.Single(recipient.Deliveries);
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        Assert.Single(await context.DeliveryAttempts.ToListAsync(Ct));
    }

    [Theory]
    [InlineData("text")]
    [InlineData("target")]
    [InlineData("cancel")]
    [InlineData("facts")]
    [InlineData("evaluation")]
    public async Task Queued_source_changes_cancel_before_paid_preparation(string change)
    {
        var application = await SeedAsync();
        var gate = new Gate();
        var recipient = new Recipient();
        var producer = await HostAsync(false, gate, recipient);
        var id = await EnqueueAsync(producer, application);
        await using (var context = await pg.Db.CreateDbContextAsync(Ct))
        {
            var current = await context.Applications.Include(row => row.Vacancy).SingleAsync(Ct);
            if (change == "text") current.CoverLetter = "changed exact text";
            if (change == "target") current.Target = "changed-target";
            if (change == "cancel") current.Status = ApplicationStatus.Cancelled;
            if (change == "evaluation") current.Vacancy!.Evaluation = new VacancyEvaluation();
            if (change == "facts") await context.Documents.Where(row => row.Key == "candidate.facts").ExecuteUpdateAsync(update => update.SetProperty(row => row.Version, row => row.Version + 1), Ct);
            await context.SaveChangesAsync(Ct);
        }
        await StopAsync(producer);
        await HostAsync(true, gate, recipient);
        await UntilAsync(async () => (await RequestAsync(id)).Stage == ApplicationDeliveryStage.Cancelled);
        Assert.Equal(0, gate.Prepares);
        Assert.Empty(recipient.Deliveries);
        await using var read = await pg.Db.CreateDbContextAsync(Ct);
        Assert.Empty(await read.DeliveryAttempts.ToListAsync(Ct));
    }

    [Fact]
    public async Task Edit_after_ready_is_fenced_again_at_the_delivery_claim()
    {
        var application = await SeedAsync();
        var gate = new Gate { HoldRestore = true };
        var recipient = new Recipient();
        var host = await HostAsync(true, gate, recipient);
        var id = await EnqueueAsync(host, application);
        await gate.Started.Task.WaitAsync(Timeout, Ct);
        await using (var context = await pg.Db.CreateDbContextAsync(Ct))
            await context.Applications.Where(row => row.Id == application.Id).ExecuteUpdateAsync(update => update.SetProperty(row => row.CoverLetter, "changed after ready"), Ct);
        gate.Release.TrySetResult();
        await UntilAsync(async () => (await RequestAsync(id)).Stage == ApplicationDeliveryStage.Cancelled);
        Assert.Empty(recipient.Deliveries);
        await using var read = await pg.Db.CreateDbContextAsync(Ct);
        Assert.Empty(await read.DeliveryAttempts.ToListAsync(Ct));
    }

    [Fact]
    public async Task Resume_dependency_releases_claim_and_survives_consumer_restart_without_repeating_review()
    {
        var application = await SeedAsync(channel: ApplicationChannel.Email);
        var gate = new Gate { WaitForResume = true };
        var recipient = new Recipient();
        var host = await HostAsync(true, gate, recipient);
        var id = await EnqueueAsync(host, application);
        await UntilAsync(async () => (await RequestAsync(id)).ResumeRequestId == gate.Dependency);
        var pending = await RequestAsync(id);
        Assert.Null(pending.ClaimEnvelopeId);
        Assert.NotNull(pending.Review);
        Assert.Equal(1, gate.Reviews);
        await StopAsync(host);
        gate.ResumeAvailable = true;
        await HostAsync(true, gate, recipient);
        await UntilAsync(async () => (await RequestAsync(id)).Stage == ApplicationDeliveryStage.Completed);
        Assert.Equal(1, gate.Reviews);
        var packet = Assert.Single(recipient.Deliveries);
        Assert.NotNull(packet.Resume);
        Assert.Equal(packet.Resume.Sha256, (await RequestAsync(id)).ResumeHash);
    }

    [Theory]
    [InlineData(8)]
    [InlineData(11)]
    public async Task Invalid_review_score_never_becomes_dispatchable(int score)
    {
        var application = await SeedAsync();
        var recipient = new Recipient();
        var host = await HostAsync(true, new Gate { Score = score }, recipient);
        var id = await EnqueueAsync(host, application);
        await UntilAsync(async () => (await RequestAsync(id)).Stage == ApplicationDeliveryStage.Cancelled);
        Assert.Empty(recipient.Deliveries);
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        Assert.Equal(ApplicationStatus.NeedsManual, (await context.Applications.SingleAsync(Ct)).Status);
        Assert.Single(await context.Notifications.Where(row => row.Kind == NotificationKind.ApplyFailed).ToListAsync(Ct));
    }

    [Fact]
    public async Task Receipt_commit_failure_becomes_unknown_instead_of_resending_or_being_silently_swallowed()
    {
        var application = await SeedAsync();
        var recipient = new Recipient();
        var fault = new ReceiptFault();
        var host = await HostAsync(true, new Gate(), recipient, fault);
        var id = await EnqueueAsync(host, application);
        await UntilAsync(async () => (await RequestAsync(id)).Stage == ApplicationDeliveryStage.OutcomeUnknown);
        Assert.Equal(1, fault.Failures);
        await host.Services.GetRequiredService<ApplicationDeliveryWorkflow>().DispatchAsync(id, 1, Guid.NewGuid(), Ct);
        Assert.Single(recipient.Deliveries);
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        Assert.Equal(ApplicationStatus.OutcomeUnknown, (await context.Applications.SingleAsync(Ct)).Status);
        Assert.Equal(DeliveryAttemptStatus.OutcomeUnknown, (await context.DeliveryAttempts.SingleAsync(Ct)).Status);
        Assert.Single(await context.Notifications.Where(row => row.Kind == NotificationKind.ApplyFailed).ToListAsync(Ct));
    }

    [Theory]
    [InlineData(ApplicationStatus.DryRun)]
    [InlineData(ApplicationStatus.Cancelled)]
    [InlineData(ApplicationStatus.NeedsManual)]
    [InlineData(ApplicationStatus.OutcomeUnknown)]
    public async Task Receipt_outcomes_remain_distinct_and_never_auto_resend(ApplicationStatus status)
    {
        var application = await SeedAsync();
        var recipient = new Recipient { Receipt = new DeliveryReceipt(status, "synthetic outcome") };
        var host = await HostAsync(true, new Gate(), recipient);
        var id = await EnqueueAsync(host, application);
        await UntilAsync(async () => (await RequestAsync(id)).Stage is ApplicationDeliveryStage.Completed or ApplicationDeliveryStage.OutcomeUnknown);
        await host.Services.GetRequiredService<ApplicationDeliveryWorkflow>().DispatchAsync(id, 1, Guid.NewGuid(), Ct);
        Assert.Single(recipient.Deliveries);
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        var saved = await context.Applications.SingleAsync(Ct);
        Assert.Equal(status, saved.Status);
        Assert.Equal(status == ApplicationStatus.DryRun ? 0 : 1, saved.Attempts);
        Assert.Equal(status == ApplicationStatus.OutcomeUnknown ? DeliveryAttemptStatus.OutcomeUnknown
            : status == ApplicationStatus.DryRun ? DeliveryAttemptStatus.DryRun : DeliveryAttemptStatus.KnownNotSent,
            (await context.DeliveryAttempts.SingleAsync(Ct)).Status);
    }

    [Fact]
    public async Task Malformed_or_unsupported_snapshots_are_quarantined_without_paid_work()
    {
        var application = await SeedAsync();
        var gate = new Gate();
        var recipient = new Recipient();
        var producer = await HostAsync(false, gate, recipient);
        var id = await EnqueueAsync(producer, application);
        await using (var context = await pg.Db.CreateDbContextAsync(Ct))
        {
            var request = await context.ApplicationDeliveryRequests.SingleAsync(Ct);
            request.InputJson = "{\"version\":99}";
            request.InputHash = JobFinder.Core.Text.CanonicalJson.Hash(request.InputJson);
            await context.SaveChangesAsync(Ct);
        }
        await StopAsync(producer);
        await HostAsync(true, gate, recipient);
        await UntilAsync(async () =>
        {
            await using var context = await pg.Db.CreateDbContextAsync(Ct);
            return await context.Database.SqlQueryRaw<int>("SELECT count(*)::int AS \"Value\" FROM jobfinder_messages.wolverine_dead_letters WHERE message_type = 'jobfinder.hiring.application-prepare.v1'").SingleAsync(Ct) == 1;
        });
        Assert.Equal(0, gate.Prepares);
        Assert.Empty(recipient.Deliveries);
        Assert.Equal(ApplicationDeliveryStage.Failed, (await RequestAsync(id)).Stage);
        await using var read = await pg.Db.CreateDbContextAsync(Ct);
        Assert.Equal(ApplicationStatus.NeedsManual, (await read.Applications.SingleAsync(Ct)).Status);
        Assert.Single(await read.Notifications.Where(row => row.Kind == NotificationKind.ApplyFailed).ToListAsync(Ct));
    }

    [Fact]
    public async Task Legacy_packet_cannot_claim_a_native_owned_application()
    {
        var application = await SeedAsync();
        var gate = new Gate();
        var host = await HostAsync(false, gate, new Recipient());
        var id = await EnqueueAsync(host, application);
        var request = await RequestAsync(id);
        var packet = Gate.Packet(request, ApplicationDeliverySources.Read(request), application, null);
        var ledger = host.Services.GetRequiredService<DeliveryLedger>();
        Assert.Null(await ledger.TryClaimAsync(packet with { ApplicationDeliveryRequestId = null }, Ct));
        Assert.Equal(ApplicationDeliveryStage.Preparing, (await RequestAsync(id)).Stage);
    }

    [Fact]
    public async Task Known_not_sent_receipt_commits_retry_and_pause_before_producer_or_consumer_can_exit()
    {
        var application = await SeedAsync();
        var gate = new Gate { HoldRestore = true };
        var recipient = new Recipient();
        var host = await HostAsync(true, gate, recipient);
        var id = await EnqueueAsync(host, application);
        await gate.Started.Task.WaitAsync(Timeout, Ct);
        await using var read = await pg.Db.CreateDbContextAsync(Ct);
        var request = await RequestAsync(id);
        var packet = Gate.Packet(request, ApplicationDeliverySources.Read(request), application, request.Review);
        var ledger = host.Services.GetRequiredService<DeliveryLedger>();
        var attempt = Assert.IsType<Guid>(await ledger.TryClaimAsync(packet, Ct));
        var publication = host.Services.GetRequiredService<ApplicationDeliveryPublication>();
        Assert.True(await ledger.CompleteAsync(attempt, new DeliveryReceipt(ApplicationStatus.Pending, "synthetic session expired"), publication.Record, Ct));
        Assert.Equal(ApplicationDeliveryStage.Completed, (await RequestAsync(id)).Stage);
        Assert.NotNull(await read.Settings.SingleOrDefaultAsync(row => row.Key == RuntimeSettings.ApplyBlockedUntil, Ct));
        var block = await host.Services.GetRequiredService<ApplicationBlocks>().ReadAsync(Ct);
        Assert.Equal(1, block.Revision);
        Assert.Equal(attempt, (await read.ApplicationBlockRevisions.SingleAsync(Ct)).AttemptId);
        foreach (var notice in await read.Notifications.ToListAsync(Ct)) await NotificationAdmissionAssertions.JoinedAsync(read, notice, Ct);
        await UntilAsync(async () =>
        {
            await using var context = await pg.Db.CreateDbContextAsync(Ct);
            return await context.ApplicationDeliveryRequests.CountAsync(Ct) == 2;
        });
        await using var after = await pg.Db.CreateDbContextAsync(Ct);
        var retry = await after.ApplicationDeliveryRequests.SingleAsync(row => row.Id != id, Ct);
        Assert.Equal(2, retry.ApplicationRevision);
        Assert.Equal(retry.Id, (await after.Applications.SingleAsync(Ct)).DeliveryRequestId);
        Assert.Empty(recipient.Deliveries);
    }

    [Fact]
    public async Task Migration_rollback_refuses_active_native_requests_without_losing_intents()
    {
        var application = await SeedAsync();
        var host = await HostAsync(false, new Gate(), new Recipient());
        var id = await EnqueueAsync(host, application);
        await StopAsync(host);
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        var migrations = (await context.Database.GetAppliedMigrationsAsync(Ct)).ToArray();
        var protectedMigration = Array.FindIndex(migrations, migration => migration.EndsWith("_DurableApplicationDelivery", StringComparison.Ordinal));
        Assert.True(protectedMigration > 0);
        try
        {
            var error = await Assert.ThrowsAsync<PostgresException>(() => MigrationTestRunner.DowngradeAndRestoreAsync(context, migrations[protectedMigration - 1], Ct));
            Assert.Contains("Cannot roll back application delivery", error.MessageText);
            Assert.Equal(ApplicationDeliveryStage.Preparing, (await RequestAsync(id)).Stage);
            Assert.Contains(migrations[protectedMigration], await context.Database.GetAppliedMigrationsAsync(Ct));
        }
        finally { await context.Database.MigrateAsync(CancellationToken.None); }
        Assert.Contains(migrations[^1], await context.Database.GetAppliedMigrationsAsync(Ct));
    }

    [Theory]
    [InlineData("pause")]
    [InlineData("threshold")]
    [InlineData("budget")]
    public async Task Restrictive_policy_publication_after_ready_is_enforced_at_the_ledger_boundary(string restriction)
    {
        var application = await SeedAsync(ApplicationMode.Auto);
        var gate = new Gate { HoldRestore = true };
        var recipient = new Recipient();
        var host = await HostAsync(true, gate, recipient);
        var id = await EnqueueAsync(host, application);
        await gate.Started.Task.WaitAsync(Timeout, Ct);
        var store = host.Services.GetRequiredService<RuntimeSettings>().Automation;
        var snapshot = await store.ReadAsync(Ct);
        var policy = snapshot.Value;
        if (restriction == "pause") policy.AutoApplyPaused = true;
        if (restriction == "threshold") policy.AutoApplyThreshold = 96;
        if (restriction == "budget") policy.DailyAutoApplyLimit = 0;
        await store.PublishAsync(policy, snapshot.Version, "synthetic-test", Ct);
        var request = await RequestAsync(id);
        var input = ApplicationDeliverySources.Read(request);
        var packet = Gate.Packet(request, input, application, request.Review);
        Assert.Null(await host.Services.GetRequiredService<DeliveryLedger>().TryClaimAsync(packet, Ct));
        var control = await ActivatorUtilities.CreateInstance<ApplicationDeliveryControls>(host.Services).ReadAsync(input, Ct);
        if (restriction == "budget")
        {
            Assert.Null(control.Stop);
            Assert.Equal(host.Services.GetRequiredService<JobFinder.Core.Time.LocalClock>().NextOccurrenceUtc(TimeOnly.MinValue), control.RetryAt);
            Assert.True(control.RetryAt > DateTimeOffset.UtcNow.AddSeconds(5));
        }
        else Assert.NotNull(control.Stop);
        Assert.Empty(recipient.Deliveries);
        await using var read = await pg.Db.CreateDbContextAsync(Ct);
        Assert.Empty(await read.DeliveryAttempts.ToListAsync(Ct));
    }

    [Fact]
    public async Task Global_pacing_serializes_different_native_applications_and_exposes_the_saved_due_time()
    {
        var first = await SeedAsync();
        var second = await SeedAsync();
        var host = await HostAsync(false, new Gate(), new Recipient());
        var firstId = await EnqueueAsync(host, first);
        var secondId = await EnqueueAsync(host, second);
        var packets = new List<ApprovedDispatch>();
        foreach (var pair in new[] { (firstId, first), (secondId, second) })
        {
            var request = await RequestAsync(pair.Item1);
            var input = ApplicationDeliverySources.Read(request);
            var packet = Gate.Packet(request, input, pair.Item2, null);
            await using var context = await pg.Db.CreateDbContextAsync(Ct);
            var current = await context.ApplicationDeliveryRequests.SingleAsync(row => row.Id == pair.Item1, Ct);
            current.Stage = ApplicationDeliveryStage.Ready;
            current.Review = packet.Review;
            await context.SaveChangesAsync(Ct);
            packets.Add(packet);
        }
        var ledger = host.Services.GetRequiredService<DeliveryLedger>();
        var results = await Task.WhenAll(packets.Select(packet => ledger.TryClaimAsync(packet, Ct)));
        Assert.Single(results, value => value is not null);
        Assert.Single(results, value => value is null);
        await using var read = await pg.Db.CreateDbContextAsync(Ct);
        var due = DateTimeOffset.Parse((await read.Settings.SingleAsync(row => row.Key == ApplicationDeliveryControls.NextSend, Ct)).Value, System.Globalization.CultureInfo.InvariantCulture);
        Assert.InRange(due - DateTimeOffset.UtcNow, TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(70));
        Assert.Single(await read.DeliveryAttempts.ToListAsync(Ct));
    }

    [Fact]
    public async Task A_corrupt_current_policy_cannot_be_ignored_by_a_ready_delivery_claim()
    {
        var application = await SeedAsync();
        var host = await HostAsync(false, new Gate(), new Recipient());
        var id = await EnqueueAsync(host, application);
        var request = await RequestAsync(id);
        var packet = Gate.Packet(request, ApplicationDeliverySources.Read(request), application, null);
        await using (var context = await pg.Db.CreateDbContextAsync(Ct))
        {
            var current = await context.ApplicationDeliveryRequests.SingleAsync(Ct);
            current.Stage = ApplicationDeliveryStage.Ready;
            current.Review = packet.Review;
            var policy = await context.Documents.SingleAsync(row => row.Key == AutomationPolicyStore.Key, Ct);
            var invalid = JsonSerializer.Deserialize(policy.Content, AutomationPolicyJsonContext.Default.AutomationPolicy)!;
            invalid.SchemaVersion = 99;
            policy.Content = JsonSerializer.Serialize(invalid, AutomationPolicyJsonContext.Default.AutomationPolicy);
            await context.SaveChangesAsync(Ct);
        }
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => host.Services.GetRequiredService<DeliveryLedger>().TryClaimAsync(packet, Ct));
        Assert.Contains("invalid current automation policy", error.Message);
        await using var read = await pg.Db.CreateDbContextAsync(Ct);
        Assert.Empty(await read.DeliveryAttempts.ToListAsync(Ct));
        Assert.Equal(ApplicationStatus.Pending, (await read.Applications.SingleAsync(Ct)).Status);
    }

    [Fact]
    public async Task An_unfinished_recipient_effect_blocks_another_application_even_after_the_reserved_interval_expires()
    {
        var first = await SeedAsync();
        var second = await SeedAsync();
        var host = await HostAsync(false, new Gate(), new Recipient());
        var firstId = await EnqueueAsync(host, first);
        var secondId = await EnqueueAsync(host, second);
        var packets = new List<ApprovedDispatch>();
        foreach (var pair in new[] { (firstId, first), (secondId, second) })
        {
            var request = await RequestAsync(pair.Item1);
            var packet = Gate.Packet(request, ApplicationDeliverySources.Read(request), pair.Item2, null);
            await using var context = await pg.Db.CreateDbContextAsync(Ct);
            var current = await context.ApplicationDeliveryRequests.SingleAsync(row => row.Id == pair.Item1, Ct);
            current.Stage = ApplicationDeliveryStage.Ready;
            current.Review = packet.Review;
            await context.SaveChangesAsync(Ct);
            packets.Add(packet);
        }
        var ledger = host.Services.GetRequiredService<DeliveryLedger>();
        var attempt = Assert.IsType<Guid>(await ledger.TryClaimAsync(packets[0], Ct));
        await using (var context = await pg.Db.CreateDbContextAsync(Ct))
            await context.Settings.Where(row => row.Key == ApplicationDeliveryControls.NextSend)
                .ExecuteUpdateAsync(update => update.SetProperty(row => row.Value, DateTimeOffset.UtcNow.AddMinutes(-1).ToString("O")), Ct);
        Assert.Null(await ledger.TryClaimAsync(packets[1], Ct));
        Assert.True(await ledger.CompleteAsync(attempt, new DeliveryReceipt(ApplicationStatus.Sent), host.Services.GetRequiredService<ApplicationDeliveryPublication>().Record, Ct));
        await using var read = await pg.Db.CreateDbContextAsync(Ct);
        var due = DateTimeOffset.Parse((await read.Settings.SingleAsync(row => row.Key == ApplicationDeliveryControls.NextSend, Ct)).Value, System.Globalization.CultureInfo.InvariantCulture);
        Assert.True(due > DateTimeOffset.UtcNow);
        Assert.Null(await ledger.TryClaimAsync(packets[1], Ct));
    }

    [Fact]
    public async Task Legacy_pending_reconciliation_only_publishes_native_work_and_does_not_prepare_or_send_inline()
    {
        var application = await SeedAsync();
        var gate = new Gate();
        var recipient = new Recipient();
        var producer = await HostAsync(false, gate, recipient);
        var worker = ActivatorUtilities.CreateInstance<ApplyWorker>(producer.Services);
        Assert.Equal(1, await worker.ReconcileAsync(Ct));
        Assert.Equal(0, await worker.ReconcileAsync(Ct));
        Assert.Equal(0, gate.Prepares);
        Assert.Empty(recipient.Deliveries);
        await using (var read = await pg.Db.CreateDbContextAsync(Ct))
            Assert.NotNull((await read.Applications.SingleAsync(row => row.Id == application.Id, Ct)).DeliveryRequestId);
        await StopAsync(producer);
        await HostAsync(true, gate, recipient);
        await UntilAsync(async () =>
        {
            await using var read = await pg.Db.CreateDbContextAsync(Ct);
            return (await read.Applications.SingleAsync(Ct)).Status == ApplicationStatus.Sent;
        });
        Assert.Single(recipient.Deliveries);
    }

    [Fact]
    public async Task Native_dry_run_release_commits_a_new_revision_and_root_without_using_the_frozen_dry_run_policy()
    {
        var application = await SeedAsync();
        var recipient = new Recipient { Receipt = new DeliveryReceipt(ApplicationStatus.DryRun, "synthetic check") };
        var consumer = await HostAsync(true, new Gate(), recipient);
        var store = consumer.Services.GetRequiredService<RuntimeSettings>().Automation;
        var policy = await store.ReadAsync(Ct);
        policy.Value.ApplyDryRun = true;
        await store.PublishAsync(policy.Value, policy.Version, "synthetic-test", Ct);
        var id = await EnqueueAsync(consumer, application);
        await UntilAsync(async () => (await RequestAsync(id)).Stage == ApplicationDeliveryStage.Completed);
        await StopAsync(consumer);
        var producer = await HostAsync(false, new Gate(), new Recipient());
        var replacementStore = producer.Services.GetRequiredService<RuntimeSettings>().Automation;
        policy = await replacementStore.ReadAsync(Ct);
        policy.Value.ApplyDryRun = false;
        await replacementStore.PublishAsync(policy.Value, policy.Version, "synthetic-test", Ct);
        await using (var context = await pg.Db.CreateDbContextAsync(Ct))
        {
            var result = await ApplyWorker.ReleaseHeldAsync(context, producer.Services.GetRequiredService<Notifier>(), DateTimeOffset.UtcNow, Ct,
                producer.Services.GetRequiredService<ApplicationDeliveryRequests>());
            Assert.Equal((1, 0), result);
        }
        await using var read = await pg.Db.CreateDbContextAsync(Ct);
        var saved = await read.Applications.SingleAsync(Ct);
        Assert.Equal(ApplicationStatus.Pending, saved.Status);
        Assert.Equal(3, saved.Revision);
        var next = await read.ApplicationDeliveryRequests.SingleAsync(row => row.Id != id, Ct);
        Assert.Equal(saved.DeliveryRequestId, next.Id);
        Assert.Equal(saved.Revision, next.ApplicationRevision);
        Assert.False(ApplicationDeliverySources.Read(next).Automation.ApplyDryRun);
        Assert.Single(recipient.Deliveries);
    }

    [Fact]
    public async Task The_actual_edit_entry_point_replaces_pending_work_atomically_and_only_the_new_text_is_delivered()
    {
        var application = await SeedAsync();
        var recipient = new Recipient();
        var producer = await HostAsync(false, new Gate(), recipient);
        var oldId = await EnqueueAsync(producer, application);
        const string replacement = "Здравствуйте!\n\nОбновлённый проверяемый текст по вакансии .NET Developer.\n\nГотов обсудить задачи.";
        var approvals = producer.Services.GetRequiredService<ApprovalService>();
        var result = await approvals.SetLetterAsync(application.VacancyId, replacement, await approvals.LetterSnapshotAsync(application.VacancyId, Ct), Ct);
        Assert.True(result.Ok, result.Message);
        Guid newId;
        await using (var read = await pg.Db.CreateDbContextAsync(Ct))
        {
            var saved = await read.Applications.SingleAsync(Ct);
            Assert.Equal(1, saved.Revision);
            newId = Assert.IsType<Guid>(saved.DeliveryRequestId);
            Assert.NotEqual(oldId, newId);
            Assert.Equal(replacement, ApplicationDeliverySources.Read(await RequestAsync(newId)).Text);
            Assert.Equal(2, await read.ApplicationDeliveryRequests.CountAsync(Ct));
        }
        await StopAsync(producer);
        await HostAsync(true, new Gate(), recipient);
        await UntilAsync(async () => (await RequestAsync(oldId)).Stage == ApplicationDeliveryStage.Cancelled
            && (await RequestAsync(newId)).Stage == ApplicationDeliveryStage.Completed);
        Assert.Equal(replacement, Assert.Single(recipient.Deliveries).Text);
    }

    [Theory]
    [InlineData(ApplicationChannel.Email)]
    [InlineData(ApplicationChannel.TelegramDm)]
    public async Task Current_dry_run_stops_actual_non_browser_transport_before_it_can_touch_a_recipient(ApplicationChannel channel)
    {
        var application = await SeedAsync(channel: channel);
        var host = await HostAsync(true, new Gate(), new Recipient(), controls: new Controls { RetryAt = DateTimeOffset.UtcNow.AddHours(1) });
        var id = await EnqueueAsync(host, application);
        var request = await RequestAsync(id);
        var packet = Gate.Packet(request, ApplicationDeliverySources.Read(request), application, null);
        var store = host.Services.GetRequiredService<RuntimeSettings>().Automation;
        var snapshot = await store.ReadAsync(Ct);
        snapshot.Value.ApplyDryRun = true;
        await store.PublishAsync(snapshot.Value, snapshot.Version, "synthetic-test", Ct);
        var transport = ActivatorUtilities.CreateInstance<EmployerDeliveryTransport>(host.Services);
        var receipt = await transport.SendAsync(packet, Ct);
        Assert.Equal(ApplicationStatus.DryRun, receipt.Status);
        Assert.Equal("nothing sent", receipt.Error);
        Assert.Null(receipt.TargetPeerId);
        Assert.Null(receipt.EmailMessageId);
    }

    [Fact]
    public async Task Interrupted_claim_is_recovered_on_server_start_and_no_envelope_can_resend_it()
    {
        var application = await SeedAsync();
        var recipient = new Recipient();
        var producer = await HostAsync(false, new Gate(), recipient);
        var id = await EnqueueAsync(producer, application);
        var request = await RequestAsync(id);
        var packet = Gate.Packet(request, ApplicationDeliverySources.Read(request), application, null);
        await using (var context = await pg.Db.CreateDbContextAsync(Ct))
        {
            var current = await context.ApplicationDeliveryRequests.SingleAsync(Ct);
            current.Stage = ApplicationDeliveryStage.Ready;
            current.Review = packet.Review;
            await context.SaveChangesAsync(Ct);
        }
        Assert.NotNull(await producer.Services.GetRequiredService<DeliveryLedger>().TryClaimAsync(packet, Ct));
        await StopAsync(producer);
        var consumer = await HostAsync(true, new Gate(), recipient);
        Assert.Equal(ApplicationDeliveryStage.OutcomeUnknown, (await RequestAsync(id)).Stage);
        await consumer.Services.GetRequiredService<ApplicationDeliveryWorkflow>().DispatchAsync(id, 1, Guid.NewGuid(), Ct);
        Assert.Empty(recipient.Deliveries);
        await using var read = await pg.Db.CreateDbContextAsync(Ct);
        Assert.Equal(ApplicationStatus.OutcomeUnknown, (await read.Applications.SingleAsync(Ct)).Status);
        Assert.Equal(DeliveryAttemptStatus.OutcomeUnknown, (await read.DeliveryAttempts.SingleAsync(Ct)).Status);
        Assert.Single(await read.Notifications.Where(row => row.Kind == NotificationKind.ApplyFailed).ToListAsync(Ct));
    }

    [Fact]
    public async Task Receipt_contact_handoff_survives_duplicates_without_creating_extra_contacts_or_sends()
    {
        var application = await SeedAsync();
        var recipient = new Recipient { Receipt = new DeliveryReceipt(ApplicationStatus.Sent, Contact: new HhContact("Synthetic HR", "hr@example.invalid", null)) };
        var host = await HostAsync(true, new Gate(), recipient);
        var id = await EnqueueAsync(host, application);
        await UntilAsync(async () =>
        {
            await using var context = await pg.Db.CreateDbContextAsync(Ct);
            return await context.CompanyContacts.AnyAsync(row => row.Value == "hr@example.invalid", Ct);
        });
        var contacts = host.Services.GetRequiredService<ContactRegistry>();
        var command = new RememberApplicationContact { RequestId = id, Name = "Synthetic HR", Email = "hr@example.invalid" };
        await ApplicationDeliveryHandler.Handle(command, pg.Db, contacts, host.Services.GetRequiredService<ServerOwnership>(), Ct);
        await using var read = await pg.Db.CreateDbContextAsync(Ct);
        var saved = await read.CompanyContacts.SingleAsync(Ct);
        Assert.Equal("hr@example.invalid", saved.Value);
        Assert.Equal("Synthetic HR", saved.PersonName);
        Assert.Single(recipient.Deliveries);
    }

    [Fact]
    public async Task Rollback_refuses_a_scheduled_command_even_after_its_request_and_marker_were_retired()
    {
        var application = await SeedAsync();
        var host = await HostAsync(false, new Gate(), new Recipient());
        var id = await EnqueueAsync(host, application);
        await using (var context = await pg.Db.CreateDbContextAsync(Ct))
        {
            await context.Applications.ExecuteUpdateAsync(update => update.SetProperty(row => row.DeliveryRequestId, (Guid?)null), Ct);
            await context.ApplicationDeliveryRequests.ExecuteDeleteAsync(Ct);
            await context.Database.ExecuteSqlRawAsync("DELETE FROM jobfinder_queues.wolverine_queue_hiring_deliver; DELETE FROM jobfinder_messages.wolverine_outgoing_envelopes", Ct);
        }
        await host.Services.GetRequiredService<IMessageBus>().PublishAsync(new DispatchApplicationDelivery { RequestId = id },
            new DeliveryOptions { ScheduledTime = DateTimeOffset.UtcNow.AddHours(1) });
        await StopAsync(host);
        await using var read = await pg.Db.CreateDbContextAsync(Ct);
        var migrations = (await read.Database.GetAppliedMigrationsAsync(Ct)).ToArray();
        var protectedMigration = Array.FindIndex(migrations, migration => migration.EndsWith("_DurableApplicationDelivery", StringComparison.Ordinal));
        Assert.True(protectedMigration > 0);
        try
        {
            var error = await Assert.ThrowsAsync<PostgresException>(() => MigrationTestRunner.DowngradeAndRestoreAsync(read, migrations[protectedMigration - 1], Ct));
            Assert.Contains("Cannot roll back application delivery", error.MessageText);
            Assert.Empty(await read.ApplicationDeliveryRequests.ToListAsync(Ct));
        }
        finally { await read.Database.MigrateAsync(CancellationToken.None); }
        Assert.Empty(await read.Database.GetPendingMigrationsAsync(Ct));
    }

    private sealed class Gate : IApplicationDeliveryGate
    {
        public bool HoldRestore { get; init; }
        public bool WaitForResume { get; init; }
        public volatile bool ResumeAvailable;
        public int Score { get; init; } = 10;
        public int Prepares;
        public int Reviews;
        public Guid Dependency { get; } = Guid.NewGuid();
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<EmployerMessagePreparation> PrepareAsync(ApplicationDeliveryRequest request, ApplicationDeliveryInput input, Application application, CancellationToken ct)
        {
            Interlocked.Increment(ref Prepares);
            if (request.Review is null) Interlocked.Increment(ref Reviews);
            var review = request.Review ?? new ApplicationLetterReview { Score = Score, TextHash = Fingerprint.Of(input.Text), ContextHash = input.ReviewContextHash, Recipe = input.Recipe };
            return Task.FromResult<EmployerMessagePreparation>(WaitForResume && !ResumeAvailable
                ? new EmployerMessagePreparation.ResumePending(Dependency, review)
                : new EmployerMessagePreparation.Ready(Packet(request, input, application, review)));
        }

        public async Task<ApprovedDispatch> RestoreAsync(ApplicationDeliveryRequest request, ApplicationDeliveryInput input, Application application, CancellationToken ct)
        {
            Started.TrySetResult();
            if (HoldRestore) await Release.Task.WaitAsync(Timeout, ct);
            return Packet(request, input, application, request.Review);
        }

        public static ApprovedDispatch Packet(ApplicationDeliveryRequest request, ApplicationDeliveryInput input, Application application, ApplicationLetterReview? review)
        {
            byte[] bytes = [1, 2, 3, 4];
            return new ApprovedDispatch(application, input.Text,
                review ?? new ApplicationLetterReview { Score = 10, TextHash = Fingerprint.Of(input.Text), ContextHash = input.ReviewContextHash },
                input.Channel is ApplicationChannel.Email or ApplicationChannel.TelegramDm
                    ? new EmailAttachment("synthetic.pdf", "Synthetic_CV.pdf", 123, Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes)), bytes) : null,
                null, input.Automation.ApplyDryRun, ApplicationDeliveryRequestId: request.Id);
        }
    }

    private sealed class Recipient : IDeliveryTransport
    {
        public ConcurrentQueue<ApprovedDispatch> Deliveries { get; } = new();
        public DeliveryReceipt Receipt { get; init; } = new(ApplicationStatus.Sent, TargetPeerId: 456, EmailMessageId: "synthetic-outbound");
        public Task<DeliveryReceipt> SendAsync(ApprovedDispatch dispatch, CancellationToken ct)
        {
            Deliveries.Enqueue(dispatch);
            return Task.FromResult(Receipt);
        }
    }

    private sealed class Controls : IApplicationDeliveryControls
    {
        public DateTimeOffset? RetryAt { get; init; }
        public Task<ApplicationDeliveryControl> ReadAsync(ApplicationDeliveryInput input, CancellationToken ct) => Task.FromResult(new ApplicationDeliveryControl(RetryAt: RetryAt));
    }

    private sealed class ReceiptFault : SaveChangesInterceptor
    {
        public int Failures;
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (Failures == 0 && eventData.Context!.ChangeTracker.Entries<DeliveryAttempt>().Any(entry => entry.Entity.Status == DeliveryAttemptStatus.Confirmed))
            {
                Interlocked.Increment(ref Failures);
                throw new IOException("synthetic receipt commit failure");
            }
            return ValueTask.FromResult(result);
        }
    }

    private sealed class RootFault : SaveChangesInterceptor
    {
        public int Failures;
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (eventData.Context!.ChangeTracker.Entries<ApplicationDeliveryRequest>().Any(entry => entry.State == EntityState.Added))
            {
                Interlocked.Increment(ref Failures);
                throw new IOException("synthetic native root save failure");
            }
            return ValueTask.FromResult(result);
        }
    }

    private sealed class NoNetwork : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => name == "threads-api" ? new HttpClient(new NoThreadsNetwork())
            : throw new InvalidOperationException("synthetic workflow must not call a real network");
    }

    private sealed class Factory(DbContextOptions<JobFinderDbContext> options) : IDbContextFactory<JobFinderDbContext>
    {
        public JobFinderDbContext CreateDbContext() => new(options);
    }
}
