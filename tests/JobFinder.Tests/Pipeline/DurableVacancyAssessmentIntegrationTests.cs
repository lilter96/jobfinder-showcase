using System.Collections.Concurrent;
using System.Data.Common;
using System.Text;
using System.Text.Json;
using JobFinder.Core.Domain;
using JobFinder.Core.Filtering;
using JobFinder.Core.Profile;
using JobFinder.Core.Text;
using JobFinder.Data;
using JobFinder.Llm;
using JobFinder.Llm.Client;
using JobFinder.Pipeline;
using JobFinder.Pipeline.Actions;
using JobFinder.Pipeline.Career;
using JobFinder.Pipeline.Messaging;
using JobFinder.Pipeline.Modules.Candidate;
using JobFinder.Pipeline.Modules.Discovery;
using JobFinder.Pipeline.Notifications;
using JobFinder.Pipeline.Profile;
using JobFinder.Pipeline.Settings;
using JobFinder.Pipeline.Workers;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Wolverine;
using Wolverine.Runtime;

namespace JobFinder.Tests.Pipeline;

public sealed partial class DurableVacancyAssessmentIntegrationTests(PostgresFixture pg) : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(35);
    private readonly List<IHost> _hosts = [];
    private readonly List<Hold> _holds = [];
    private readonly string _scratch = Path.Combine(OperatingSystem.IsWindows() ? @"E:\Temp" : Path.GetTempPath(),
        "jobfinder-assessment-tests-" + Guid.NewGuid().ToString("N"));
    private readonly CandidateProfile _profile = new()
    {
        Name = "Synthetic Candidate", NameLatin = "Synthetic Candidate", Title = "Senior .NET Developer", Reviewed = true,
        Summary = "Developed synthetic .NET services.", Location = "Minsk", WorkFormats = ["remote"],
        Skills = [new ProfileSkill { Name = "C#", Level = "expert", Years = 6 }],
        DesiredSalary = new DesiredSalary { Amount = 5000, Currency = "USD", Net = true },
    };
    private readonly CandidateFacts _facts = new()
    {
        Facts = [new CandidateFact { Key = "notice", Question = "When can you start?", Answer = "In two weeks.", Confirmed = true, Source = "synthetic-test" }],
    };

    public async ValueTask InitializeAsync()
    {
        Assert.SkipWhen(pg.SkipReason is not null, pg.SkipReason ?? "");
        await using (var context = await pg.Db.CreateDbContextAsync(Ct))
        {
            await context.Database.MigrateAsync(Ct);
            await context.LetterPreparationLanes.ExecuteDeleteAsync(Ct);
            await context.LetterPreparationRequests.ExecuteDeleteAsync(Ct);
            await context.VacancyAssessmentRequests.ExecuteDeleteAsync(Ct);
            await context.DeliveryAttempts.ExecuteDeleteAsync(Ct);
            await context.EmployerMessages.ExecuteDeleteAsync(Ct);
            await context.ApplicationDeliveryRequests.ExecuteDeleteAsync(Ct);
            await context.Applications.ExecuteDeleteAsync(Ct);
            await context.Vacancies.ExecuteDeleteAsync(Ct);
            await context.CompanyContacts.ExecuteDeleteAsync(Ct);
            await context.HhResponses.ExecuteDeleteAsync(Ct);
            await context.PolicyRevisions.ExecuteDeleteAsync(Ct);
            await context.CandidateRevisions.ExecuteDeleteAsync(Ct);
            await context.Documents.ExecuteDeleteAsync(Ct);
            await context.Settings.ExecuteDeleteAsync(Ct);
            await NotificationAdmissionAssertions.ResetAsync(context, Ct);
            await context.Database.ExecuteSqlRawAsync("DELETE FROM jobfinder_queues.wolverine_queue_scoring; DELETE FROM jobfinder_queues.wolverine_queue_scoring_scheduled; DELETE FROM jobfinder_queues.wolverine_queue_preparation; DELETE FROM jobfinder_queues.wolverine_queue_preparation_scheduled; DELETE FROM jobfinder_queues.wolverine_queue_hiring_prepare; DELETE FROM jobfinder_queues.wolverine_queue_hiring_prepare_scheduled; DELETE FROM jobfinder_messages.wolverine_incoming_envelopes; DELETE FROM jobfinder_messages.wolverine_outgoing_envelopes; DELETE FROM jobfinder_messages.wolverine_dead_letters", Ct);
        }
        using var bootstrap = await BuildHostAsync(false, new Steps());
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
        foreach (var hold in _holds) hold.Release.TrySetResult();
        foreach (var host in _hosts.AsEnumerable().Reverse())
        {
            try { await host.StopAsync(CancellationToken.None).WaitAsync(Timeout); }
            finally { host.Dispose(); }
        }
    }

    private async Task<IHost> BuildHostAsync(bool consumer, Steps steps, IInterceptor? interceptor = null, bool owned = false,
        Action<IServiceCollection>? configure = null)
    {
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { DisableDefaults = true });
        var config = new Dictionary<string, string?>
        {
            ["Database:ConnectionString"] = context.Database.GetConnectionString(), ["Paths:Prompts"] = Fixture.PromptsDir,
            ["Paths:Profile"] = Path.Combine(_scratch, "profile"), ["Paths:Data"] = Path.Combine(_scratch, "data"),
            ["Paths:Assets"] = Path.Combine(_scratch, "assets"), ["Llm:Endpoint"] = "https://example.invalid/llm",
            ["Llm:ApiKey"] = "synthetic-test-key", ["Decision:TimeZone"] = "Europe/Minsk",
            ["Filter:RequiredMarkers:0"] = ".NET", ["Filter:RemoteCountries:0"] = "BY",
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
        builder.Services.RemoveAll<IDbContextFactory<JobFinderDbContext>>();
        if (interceptor is null) builder.Services.AddSingleton(pg.Db);
        else
        {
            var options = new DbContextOptionsBuilder<JobFinderDbContext>();
            JobFinderDbContext.Configure(options, context.Database.GetConnectionString()!);
            options.AddInterceptors(interceptor);
            builder.Services.AddSingleton<IDbContextFactory<JobFinderDbContext>>(new Factory(options.Options));
        }
        builder.Services.RemoveAll<IVacancyAssessmentSteps>();
        builder.Services.AddSingleton<IVacancyAssessmentSteps>(steps);
        builder.Services.RemoveAll<IHttpClientFactory>();
        builder.Services.AddSingleton<IHttpClientFactory>(new NoNetwork());
        configure?.Invoke(builder.Services);
        builder.AddJobFinderMessaging(consumer);
        if (consumer) builder.Services.AddJobFinderInitialization();
        else if (owned) builder.Services.AddHostedService<SingleInstanceGuard>();
        return builder.Build();
    }

    private async Task<IHost> HostAsync(bool consumer, Steps steps, IInterceptor? interceptor = null, bool owned = false,
        Action<IServiceCollection>? configure = null)
    {
        var host = await BuildHostAsync(consumer, steps, interceptor, owned, configure);
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

    private Hold Barrier()
    {
        var hold = new Hold();
        _holds.Add(hold);
        return hold;
    }

    private async Task<Vacancy> SeedAsync(bool hh = false)
    {
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        var vacancy = new Vacancy
        {
            Title = "Synthetic Senior .NET Developer", CompanyName = "Synthetic Company", Description = "Develop synthetic C# and PostgreSQL services.",
            Url = "https://example.invalid/vacancy/" + Guid.NewGuid().ToString("N"), Fingerprint = Guid.NewGuid().ToString("N"),
            Status = VacancyStatus.New, WorkFormats = WorkFormat.Remote, CountryCode = "BY", City = "Minsk",
            SalaryFrom = 4500, SalaryTo = 5000, Currency = "USD", SalaryGross = false, Skills = ["C#", "PostgreSQL"],
            FirstSeenAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
        };
        if (hh) vacancy.Sources.Add(new VacancySource
        {
            Source = SourceKind.HeadHunter, ExternalId = "987001", Host = "hh.ru", Url = "https://hh.ru/vacancy/987001",
            LetterRequired = true, HasEmployerTest = false, FetchedAt = DateTimeOffset.UtcNow,
        });
        context.Vacancies.Add(vacancy);
        await context.SaveChangesAsync(Ct);
        return vacancy;
    }

    private static async Task<Guid> EnqueueAsync(IHost host, long vacancyId)
    {
        var id = await host.Services.GetRequiredService<VacancyAssessments>().RequestAsync(vacancyId, Ct).WaitAsync(Timeout, Ct);
        Assert.NotNull(id);
        return id.Value;
    }

    private async Task<VacancyAssessmentRequest> RequestAsync(Guid id)
    {
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        return await context.VacancyAssessmentRequests.AsNoTracking().SingleAsync(row => row.Id == id, Ct);
    }

    private async Task<Vacancy> VacancyAsync(long id)
    {
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        return await context.Vacancies.AsNoTracking().Include(row => row.Sources).SingleAsync(row => row.Id == id, Ct);
    }

    private static VacancyAssessmentInput Input(VacancyAssessmentRequest request) =>
        JsonSerializer.Deserialize(request.InputJson, VacancyAssessmentJsonContext.Default.VacancyAssessmentInput)!;

    private static async Task UntilAsync(Func<Task<bool>> condition)
    {
        async Task PollAsync()
        {
            while (!await condition()) await Task.Delay(50, Ct);
        }
        await PollAsync().WaitAsync(Timeout, Ct);
    }

    private async Task<List<Notification>> CardsAsync()
    {
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        return await context.Notifications.AsNoTracking().Where(row => row.Kind == NotificationKind.VacancyApproval).ToListAsync(Ct);
    }

    private async Task AssertNoApplicationsAsync()
    {
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        Assert.Empty(await context.Applications.ToListAsync(Ct));
        Assert.Empty(await context.DeliveryAttempts.ToListAsync(Ct));
        Assert.Empty(await context.EmployerMessages.ToListAsync(Ct));
    }

    private async Task<int> NativePendingAsync()
    {
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        return await context.Database.SqlQuery<int>($"""
            SELECT ((SELECT count(*) FROM jobfinder_queues.wolverine_queue_scoring)
              + (SELECT count(*) FROM jobfinder_queues.wolverine_queue_scoring_scheduled)
              + (SELECT count(*) FROM jobfinder_messages.wolverine_incoming_envelopes WHERE status <> 'Handled' AND (message_type LIKE 'jobfinder.discovery.vacancy-%' OR message_type = 'jobfinder.hiring.vacancy-letter.v1'))
              + (SELECT count(*) FROM jobfinder_messages.wolverine_outgoing_envelopes WHERE message_type LIKE 'jobfinder.discovery.vacancy-%' OR message_type = 'jobfinder.hiring.vacancy-letter.v1'))::int AS "Value"
            """).SingleAsync(Ct);
    }

    private async Task<List<StoredEnvelope>> ScheduledAsync(Guid id)
    {
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        var requestBytes = Encoding.UTF8.GetBytes(id.ToString());
        return await context.Database.SqlQuery<StoredEnvelope>($"""
            SELECT message_type, body, execution_time
            FROM jobfinder_queues.wolverine_queue_scoring_scheduled WHERE position({requestBytes} in body) > 0
            UNION ALL
            SELECT message_type, body, execution_time
            FROM jobfinder_messages.wolverine_incoming_envelopes WHERE status = 'Scheduled' AND position({requestBytes} in body) > 0
            """).ToListAsync(Ct);
    }

    private async Task AssertExactCardAsync(long vacancyId, IReadOnlyList<string>? problems = null)
    {
        var vacancy = await VacancyAsync(vacancyId);
        var card = Assert.Single(await CardsAsync());
        var expected = Cards.VacancyApproval(vacancy, ApplyRoutes.Resolve(vacancy, false), letterProblems: problems);
        Assert.Equal(vacancyId, card.EntityId);
        Assert.Equal(NotificationStatus.Pending, card.Status);
        Assert.Null(card.SentAt);
        Assert.Null(card.TelegramMessageId);
        Assert.Equal(expected.Text, card.Text);
        Assert.Equal(JsonSerializer.Serialize(expected.Buttons), JsonSerializer.Serialize(card.Buttons));
        Assert.Null(card.AttachmentPath);
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        await NotificationAdmissionAssertions.JoinedAsync(context, card, Ct);
    }

    [Fact]
    public async Task Duplicate_producers_exit_without_paid_work_then_native_consumer_commits_one_exact_card()
    {
        var vacancy = await SeedAsync();
        var steps = new Steps();
        var producer = await HostAsync(false, steps);
        var ids = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => EnqueueAsync(producer, vacancy.Id)));
        var id = Assert.Single(ids.Distinct());
        Assert.Empty(steps.Calls);
        Assert.False(producer.Services.GetRequiredService<ServerOwnership>().IsOwned);
        Assert.False(producer.Services.GetRequiredService<IWolverineRuntime>().Options.Durability.DurabilityAgentEnabled);
        Assert.Equal(1, await NativePendingAsync());
        await using (var context = await pg.Db.CreateDbContextAsync(Ct))
        {
            Assert.Single(await context.VacancyAssessmentRequests.ToListAsync(Ct));
            Assert.Equal("jobfinder.discovery.vacancy-score.v1", await context.Database.SqlQuery<string>($"SELECT message_type AS \"Value\" FROM jobfinder_queues.wolverine_queue_scoring").SingleAsync(Ct));
        }
        var before = await RequestAsync(id);
        Assert.Equal(VacancyAssessmentStage.Scoring, before.Stage);
        Assert.Null(before.ScoreJson);
        Assert.Null(before.LetterJson);
        Assert.Empty(await CardsAsync());
        await AssertNoApplicationsAsync();
        await StopAsync(producer);
        var consumer = await HostAsync(true, steps);
        await UntilAsync(async () => (await RequestAsync(id)).Stage == VacancyAssessmentStage.Completed);
        Assert.Equal([VacancyAssessmentStage.Scoring, VacancyAssessmentStage.Lettering], steps.Calls.Select(call => call.Stage));
        var completed = await RequestAsync(id);
        Assert.Equal(before.InputJson, completed.InputJson);
        Assert.Equal(0, completed.Failures);
        Assert.Null(completed.ClaimEnvelopeId);
        Assert.NotNull(completed.CompletedAt);
        var saved = await VacancyAsync(vacancy.Id);
        var score = JsonSerializer.Deserialize(completed.ScoreJson!, VacancyAssessmentJsonContext.Default.AssessmentScore)!;
        var letter = JsonSerializer.Deserialize(completed.LetterJson!, VacancyAssessmentJsonContext.Default.AssessmentLetter)!;
        Assert.Equal(score.Score, saved.Score);
        Assert.Equal(JsonSerializer.Serialize(score.Evaluation), JsonSerializer.Serialize(saved.Evaluation));
        Assert.Equal(letter.Text, saved.CoverLetter);
        Assert.Equal(JsonSerializer.Serialize(letter.Review), JsonSerializer.Serialize(saved.LetterReview));
        Assert.Equal(VacancyStatus.Scored, saved.Status);
        Assert.Equal(Decision.Approval, saved.Decision);
        Assert.True(saved.Notified);
        await AssertExactCardAsync(vacancy.Id);
        await AssertNoApplicationsAsync();
        var bus = consumer.Services.GetRequiredService<IMessageBus>();
        await bus.PublishAsync(new ScoreVacancy { RequestId = id });
        await bus.PublishAsync(new PrepareVacancyLetter { RequestId = id });
        await bus.PublishAsync(new FinishVacancyAssessment { RequestId = id });
        await UntilAsync(async () => await NativePendingAsync() == 0);
        Assert.Equal(2, steps.Calls.Count);
        await AssertExactCardAsync(vacancy.Id);
        Assert.Null(await consumer.Services.GetRequiredService<VacancyAssessments>().RequestAsync(vacancy.Id, Ct));
    }

    [Fact]
    public async Task Candidate_and_policy_inputs_are_frozen_across_producer_exit_and_policy_republication()
    {
        var vacancy = await SeedAsync();
        var steps = new Steps();
        var producer = await HostAsync(false, steps);
        var id = await EnqueueAsync(producer, vacancy.Id);
        var before = await RequestAsync(id);
        var captured = Input(before);
        Assert.True(captured.Profile.Reviewed);
        Assert.True(captured.Facts.IsConfirmed("notice"));
        Assert.Equal(1, captured.ProfileVersion);
        Assert.Equal(1, captured.FactsVersion);
        Assert.Equal(0, captured.SalaryAdjustment);
        _profile.Name = "Not published";
        _profile.Skills[0].Name = "Not published";
        _profile.DesiredSalary!.Amount = 99999;
        _facts.Facts[0].Answer = "Not published";
        var automation = producer.Services.GetRequiredService<RuntimeSettings>().Automation;
        var currentAutomation = await automation.ReadAsync(Ct);
        await automation.PublishAsync(currentAutomation.Value with { ApprovalThreshold = 95, AutoApplyPaused = true }, currentAutomation.Version, "synthetic-test", Ct);
        var search = producer.Services.GetRequiredService<SearchPolicyStore>();
        var currentSearch = await search.ReadAsync(Ct);
        currentSearch.Value.Filter.RequiredMarkers = ["replacement-only"];
        await search.PublishAsync(currentSearch.Value, currentSearch.Version, "synthetic-test", Ct);
        var llm = producer.Services.GetRequiredService<LlmPolicyStore>();
        var currentLlm = await llm.ReadAsync(Ct);
        currentLlm.Value.Routes[LlmPurpose.Score].Steps[0].MaxTokens = 4096;
        await llm.PublishAsync(currentLlm.Value, currentLlm.Version, "synthetic-test", Ct);
        await StopAsync(producer);
        await HostAsync(true, steps);
        await UntilAsync(async () => (await RequestAsync(id)).Stage == VacancyAssessmentStage.Completed);
        Assert.Equal(before.InputJson, (await RequestAsync(id)).InputJson);
        Assert.All(steps.Calls, call =>
        {
            Assert.Equal(before.InputHash, CanonicalJson.Hash(JsonSerializer.Serialize(call.Input, VacancyAssessmentJsonContext.Default.VacancyAssessmentInput)));
            Assert.Equal("Synthetic Candidate", call.Input.Profile.Name);
            Assert.Equal("C#", call.Input.Profile.Skills[0].Name);
            Assert.Equal("In two weeks.", call.Input.Facts.Get("notice")!.Answer);
            Assert.Equal(60, call.Input.Automation.ApprovalThreshold);
            Assert.Contains(".NET", call.Input.Search.Filter.RequiredMarkers);
            Assert.Equal(2000, call.Input.Operation.Policy.Routes[LlmPurpose.Score].Steps[0].MaxTokens);
        });
        var saved = await VacancyAsync(vacancy.Id);
        var score = JsonSerializer.Deserialize((await RequestAsync(id)).ScoreJson!, VacancyAssessmentJsonContext.Default.AssessmentScore)!;
        Assert.Equal(Decision.Approval, score.Decision);
        Assert.Equal(VacancyStatus.Rejected, saved.Status);
        Assert.Equal(Decision.None, saved.Decision);
        Assert.Empty(await CardsAsync());
        await AssertNoApplicationsAsync();
    }

    [Fact]
    public async Task Restart_from_letter_checkpoint_does_not_repeat_the_saved_score()
    {
        var vacancy = await SeedAsync();
        var first = new Steps { OnStage = (stage, _, _) => stage == VacancyAssessmentStage.Lettering
            ? throw new InvalidOperationException("synthetic letter failure") : Task.CompletedTask };
        var consumer = await HostAsync(true, first);
        var id = await EnqueueAsync(consumer, vacancy.Id);
        await UntilAsync(async () => (await RequestAsync(id)).Failures == 1);
        var checkpoint = await RequestAsync(id);
        Assert.Equal(VacancyAssessmentStage.Lettering, checkpoint.Stage);
        Assert.NotNull(checkpoint.ScoreJson);
        Assert.Null(checkpoint.LetterJson);
        Assert.Null(checkpoint.ClaimEnvelopeId);
        Assert.Empty(await CardsAsync());
        await StopAsync(consumer);
        var restarted = new Steps();
        await HostAsync(true, restarted);
        await UntilAsync(async () => (await RequestAsync(id)).Stage == VacancyAssessmentStage.Completed);
        Assert.Equal(checkpoint.ScoreJson, (await RequestAsync(id)).ScoreJson);
        Assert.Single(first.Calls, call => call.Stage == VacancyAssessmentStage.Scoring);
        Assert.Equal(VacancyAssessmentStage.Lettering, Assert.Single(restarted.Calls).Stage);
        await AssertExactCardAsync(vacancy.Id);
        await AssertNoApplicationsAsync();
    }

    [Fact]
    public async Task Letter_unavailability_schedules_the_exact_native_stage_without_failures_and_another_vacancy_completes()
    {
        var blocked = await SeedAsync();
        var healthy = await SeedAsync();
        var steps = new Steps { OnStage = (stage, input, _) => stage == VacancyAssessmentStage.Lettering && input.Vacancy.Id == blocked.Id
            ? throw new LlmUnavailableException("synthetic letter outage") : Task.CompletedTask };
        var host = await HostAsync(true, steps);
        var id = await EnqueueAsync(host, blocked.Id);
        await UntilAsync(async () => (await RequestAsync(id)).RetryAt is not null && (await ScheduledAsync(id)).Count > 0);
        var waiting = await RequestAsync(id);
        Assert.Equal(VacancyAssessmentStage.Lettering, waiting.Stage);
        Assert.Equal(0, waiting.Failures);
        Assert.Null(waiting.ClaimEnvelopeId);
        Assert.NotNull(waiting.ScoreJson);
        Assert.Null(waiting.LetterJson);
        Assert.Equal("synthetic letter outage", waiting.Error);
        Assert.True(waiting.RetryAt > DateTimeOffset.UtcNow.AddMinutes(1));
        var scheduled = Assert.Single(await ScheduledAsync(id));
        Assert.Equal("jobfinder.hiring.vacancy-letter.v1", scheduled.MessageType);
        Assert.Equal(waiting.RetryAt!.Value.ToUnixTimeMilliseconds(), scheduled.ExecutionTime.ToUnixTimeMilliseconds());
        Assert.Contains(id.ToString(), Encoding.UTF8.GetString(scheduled.Body));
        var other = await EnqueueAsync(host, healthy.Id);
        await UntilAsync(async () => (await RequestAsync(other)).Stage == VacancyAssessmentStage.Completed);
        var stillWaiting = await RequestAsync(id);
        Assert.Equal(waiting.ScoreJson, stillWaiting.ScoreJson);
        Assert.Equal(waiting.RetryAt, stillWaiting.RetryAt);
        Assert.Equal(0, stillWaiting.Failures);
        Assert.Equal(0, (await VacancyAsync(blocked.Id)).Attempts);
        Assert.Equal(VacancyAssessmentStage.Lettering, stillWaiting.Stage);
        Assert.Single(steps.Calls, call => call.Input.Vacancy.Id == blocked.Id && call.Stage == VacancyAssessmentStage.Scoring);
        Assert.Single(steps.Calls, call => call.Input.Vacancy.Id == blocked.Id && call.Stage == VacancyAssessmentStage.Lettering);
        await AssertExactCardAsync(healthy.Id);
        await AssertNoApplicationsAsync();
    }

    [Theory]
    [InlineData(VacancyAssessmentStage.Scoring, "dismiss")]
    [InlineData(VacancyAssessmentStage.Lettering, "dismiss")]
    [InlineData(VacancyAssessmentStage.Scoring, "profile")]
    [InlineData(VacancyAssessmentStage.Lettering, "profile")]
    [InlineData(VacancyAssessmentStage.Scoring, "facts")]
    [InlineData(VacancyAssessmentStage.Lettering, "facts")]
    [InlineData(VacancyAssessmentStage.Scoring, "source")]
    [InlineData(VacancyAssessmentStage.Lettering, "source")]
    public async Task Dismissal_or_candidate_publication_while_paid_work_is_held_cancels_without_a_card_or_application(VacancyAssessmentStage heldStage, string change)
    {
        var vacancy = await SeedAsync();
        var hold = Barrier();
        var steps = new Steps { OnStage = (stage, _, token) => stage == heldStage ? hold.WaitAsync(token) : Task.CompletedTask };
        var host = await HostAsync(true, steps);
        var id = await EnqueueAsync(host, vacancy.Id);
        await hold.Entered.Task.WaitAsync(Timeout, Ct);
        var claimed = await RequestAsync(id);
        Assert.Equal(heldStage, claimed.Stage);
        Assert.NotNull(claimed.ClaimEnvelopeId);
        if (change == "dismiss")
            Assert.True((await host.Services.GetRequiredService<ApprovalService>().DismissAsync(vacancy.Id, Ct)).Ok);
        else if (change == "profile")
        {
            var current = (await host.Services.GetRequiredService<ProfileProvider>().GetSnapshotAsync(Ct))!;
            await host.Services.GetRequiredService<ICandidatePublisher>().PublishProfileAsync(current.Value with { Summary = "New synthetic experience." },
                current.Version, "synthetic-test", Ct);
        }
        else if (change == "source")
        {
            await using var context = await pg.Db.CreateDbContextAsync(Ct);
            var current = await context.Vacancies.SingleAsync(row => row.Id == vacancy.Id, Ct);
            current.Description = new string('x', current.Description.Length);
            await context.SaveChangesAsync(Ct);
        }
        else
        {
            var current = await host.Services.GetRequiredService<FactsProvider>().GetSnapshotAsync(Ct);
            current.Value.Get("notice")!.Answer = "In one month.";
            await host.Services.GetRequiredService<ICandidatePublisher>().PublishFactsAsync(current.Value, current.Version, "synthetic-test", Ct);
        }
        hold.Release.TrySetResult();
        await UntilAsync(async () => (await RequestAsync(id)).Stage == VacancyAssessmentStage.Cancelled);
        var cancelled = await RequestAsync(id);
        Assert.Null(cancelled.ClaimEnvelopeId);
        Assert.Null(cancelled.RetryAt);
        Assert.NotNull(cancelled.CompletedAt);
        Assert.Equal(0, cancelled.Failures);
        Assert.Equal(claimed.ScoreJson, cancelled.ScoreJson);
        Assert.Null(cancelled.LetterJson);
        Assert.Equal(claimed.InputJson, cancelled.InputJson);
        var saved = await VacancyAsync(vacancy.Id);
        Assert.Equal(change == "dismiss" ? VacancyStatus.Dismissed : VacancyStatus.New, saved.Status);
        Assert.Null(saved.Score);
        Assert.Null(saved.Evaluation);
        Assert.Null(saved.CoverLetter);
        Assert.False(saved.Notified);
        Assert.Equal(heldStage == VacancyAssessmentStage.Scoring ? 1 : 2, steps.Calls.Count);
        Assert.Empty(await CardsAsync());
        await AssertNoApplicationsAsync();
    }

    [Theory]
    [InlineData("below-bar")]
    [InlineData("missing-review")]
    [InlineData("wrong-text")]
    public async Task A_weak_or_stale_letter_downgrades_an_auto_decision_to_visible_approval_without_creating_an_application(string weakness)
    {
        var vacancy = await SeedAsync(hh: true);
        var steps = new Steps { LlmScore = 91, LetterWeakness = weakness };
        var producer = await HostAsync(false, steps);
        var id = await EnqueueAsync(producer, vacancy.Id);
        await using (var context = await pg.Db.CreateDbContextAsync(Ct))
        {
            var request = await context.VacancyAssessmentRequests.SingleAsync(row => row.Id == id, Ct);
            var input = Input(request);
            Assert.False(input.CanAutoApply);
            Assert.False(input.Automation.AutoApplyPaused);
            Assert.Equal(0, input.AutoToday);
            input.CanAutoApply = true;
            request.InputJson = JsonSerializer.Serialize(input, VacancyAssessmentJsonContext.Default.VacancyAssessmentInput);
            request.InputHash = CanonicalJson.Hash(request.InputJson);
            await context.SaveChangesAsync(Ct);
        }
        await StopAsync(producer);
        await HostAsync(true, steps);
        await UntilAsync(async () => (await RequestAsync(id)).Stage == VacancyAssessmentStage.Completed);
        var completed = await RequestAsync(id);
        var score = JsonSerializer.Deserialize(completed.ScoreJson!, VacancyAssessmentJsonContext.Default.AssessmentScore)!;
        var letter = JsonSerializer.Deserialize(completed.LetterJson!, VacancyAssessmentJsonContext.Default.AssessmentLetter)!;
        Assert.Equal(Decision.AutoApply, score.Decision);
        Assert.Contains("нет действующей проверки письма ≥ 9/10", letter.Problems);
        var saved = await VacancyAsync(vacancy.Id);
        Assert.True(ApplyRoutes.HhApplicable(saved));
        Assert.Equal(Decision.Approval, saved.Decision);
        Assert.Equal(letter.Text, saved.CoverLetter);
        Assert.True(saved.Notified);
        await AssertExactCardAsync(vacancy.Id, letter.Problems);
        Assert.Contains("Письмо не прошло проверку", Assert.Single(await CardsAsync()).Text);
        await AssertNoApplicationsAsync();
    }

    [Theory]
    [InlineData(ApplicationStatus.Pending)]
    [InlineData(ApplicationStatus.Sending)]
    [InlineData(ApplicationStatus.OutcomeUnknown)]
    [InlineData(ApplicationStatus.DryRun)]
    public async Task Actual_rescore_and_producer_refuse_delivery_in_progress_or_held_without_mutating_the_existing_generation(ApplicationStatus status)
    {
        var vacancy = await SeedAsync();
        var steps = new Steps();
        var host = await HostAsync(false, steps);
        var id = await EnqueueAsync(host, vacancy.Id);
        var before = await RequestAsync(id);
        await using (var context = await pg.Db.CreateDbContextAsync(Ct))
        {
            context.Applications.Add(new Application
            {
                VacancyId = vacancy.Id, Channel = ApplicationChannel.HeadHunter, Mode = ApplicationMode.Approved,
                Status = status, CoverLetter = "Existing synthetic letter", CreatedAt = DateTimeOffset.UtcNow,
            });
            await context.SaveChangesAsync(Ct);
        }
        var result = await host.Services.GetRequiredService<ApprovalService>().RescoreAsync(vacancy.Id, Ct);
        Assert.False(result.Ok);
        Assert.Null(await host.Services.GetRequiredService<VacancyAssessments>().RequestAsync(vacancy.Id, Ct));
        var saved = await VacancyAsync(vacancy.Id);
        Assert.Equal(vacancy.Status, saved.Status);
        Assert.Equal(vacancy.AssessmentRevision, saved.AssessmentRevision);
        Assert.Equal(vacancy.UpdatedAt.ToUnixTimeMilliseconds(), saved.UpdatedAt.ToUnixTimeMilliseconds());
        var after = await RequestAsync(id);
        Assert.Equal(before.InputJson, after.InputJson);
        Assert.Equal(before.Stage, after.Stage);
        Assert.Null(after.CompletedAt);
        Assert.Empty(steps.Calls);
        Assert.Empty(await CardsAsync());
        Assert.Equal(1, await NativePendingAsync());
        await using var read = await pg.Db.CreateDbContextAsync(Ct);
        var application = Assert.Single(await read.Applications.ToListAsync(Ct));
        Assert.Equal(status, application.Status);
        Assert.Equal("Existing synthetic letter", application.CoverLetter);
        Assert.Single(await read.VacancyAssessmentRequests.ToListAsync(Ct));
    }

    [Fact]
    public async Task Actual_rescore_supersedes_a_same_New_active_generation_and_only_the_replacement_publishes()
    {
        var vacancy = await SeedAsync();
        var hold = Barrier();
        var steps = new Steps { OnStage = (stage, input, token) => stage == VacancyAssessmentStage.Scoring && input.Vacancy.Revision == 0
            ? hold.WaitAsync(token) : Task.CompletedTask };
        var host = await HostAsync(true, steps);
        var before = await EnqueueAsync(host, vacancy.Id);
        await hold.Entered.Task.WaitAsync(Timeout, Ct);
        Assert.Equal(VacancyStatus.New, (await VacancyAsync(vacancy.Id)).Status);
        var result = await host.Services.GetRequiredService<ApprovalService>().RescoreAsync(vacancy.Id, Ct);
        Assert.True(result.Ok, result.Message);
        var cancelled = await RequestAsync(before);
        Assert.Equal(VacancyAssessmentStage.Cancelled, cancelled.Stage);
        Assert.Null(cancelled.ClaimEnvelopeId);
        Assert.Null(cancelled.RetryAt);
        var after = await EnqueueAsync(host, vacancy.Id);
        Assert.NotEqual(before, after);
        Assert.Equal(1, Input(await RequestAsync(after)).Vacancy.Revision);
        hold.Release.TrySetResult();
        await UntilAsync(async () => (await RequestAsync(after)).Stage == VacancyAssessmentStage.Completed);
        await UntilAsync(async () => await NativePendingAsync() == 0);
        Assert.Equal(VacancyAssessmentStage.Cancelled, (await RequestAsync(before)).Stage);
        Assert.Null((await RequestAsync(before)).ScoreJson);
        Assert.Null((await RequestAsync(before)).LetterJson);
        Assert.Single(steps.Calls, call => call.Input.Operation.OperationId == before);
        Assert.Equal(2, steps.Calls.Count(call => call.Input.Operation.OperationId == after));
        Assert.Equal(1, (await VacancyAsync(vacancy.Id)).AssessmentRevision);
        await AssertExactCardAsync(vacancy.Id);
        await AssertNoApplicationsAsync();
    }

    [Fact]
    public async Task Final_transaction_commit_fault_rolls_back_score_letter_card_and_completion_then_checkpoint_retry_finishes_once()
    {
        var vacancy = await SeedAsync();
        var retry = Barrier();
        var fault = new FailCommit(VacancyAssessmentStage.Completed, retry);
        var steps = new Steps();
        var host = await HostAsync(true, steps, fault);
        var id = await EnqueueAsync(host, vacancy.Id);
        await retry.Entered.Task.WaitAsync(Timeout, Ct);
        Assert.True(fault.Fired);
        Assert.True(fault.SawScoredVacancy);
        Assert.True(fault.SawApprovalCard);
        var checkpoint = await RequestAsync(id);
        Assert.Equal(VacancyAssessmentStage.Finishing, checkpoint.Stage);
        Assert.NotNull(checkpoint.ScoreJson);
        Assert.NotNull(checkpoint.LetterJson);
        Assert.Null(checkpoint.CompletedAt);
        Assert.NotNull(checkpoint.ClaimEnvelopeId);
        Assert.Equal(0, checkpoint.Failures);
        var partial = await VacancyAsync(vacancy.Id);
        Assert.Equal(VacancyStatus.New, partial.Status);
        Assert.Null(partial.Score);
        Assert.Null(partial.LlmScore);
        Assert.Null(partial.Evaluation);
        Assert.Null(partial.ScoredAt);
        Assert.Null(partial.CoverLetter);
        Assert.Null(partial.LetterReview);
        Assert.False(partial.Notified);
        Assert.Equal(Decision.None, partial.Decision);
        Assert.Equal(0, partial.Attempts);
        Assert.Null(partial.LastError);
        Assert.Empty(await CardsAsync());
        await AssertNoApplicationsAsync();
        retry.Release.TrySetResult();
        await UntilAsync(async () => (await RequestAsync(id)).Stage == VacancyAssessmentStage.Completed);
        var completed = await RequestAsync(id);
        Assert.Equal(checkpoint.ScoreJson, completed.ScoreJson);
        Assert.Equal(checkpoint.LetterJson, completed.LetterJson);
        Assert.Equal(0, completed.Failures);
        Assert.Null(completed.ClaimEnvelopeId);
        Assert.Equal(2, steps.Calls.Count);
        Assert.Equal(0, (await VacancyAsync(vacancy.Id)).Attempts);
        await AssertExactCardAsync(vacancy.Id);
        await AssertNoApplicationsAsync();
    }

    [Fact]
    public async Task Producer_commit_fault_rolls_back_both_business_request_and_native_score_command()
    {
        var vacancy = await SeedAsync();
        var fault = new FailCommit(VacancyAssessmentStage.Scoring);
        var steps = new Steps();
        var producer = await HostAsync(false, steps, fault);
        await Assert.ThrowsAsync<IOException>(() => producer.Services.GetRequiredService<VacancyAssessments>().RequestAsync(vacancy.Id, Ct));
        Assert.True(fault.Fired);
        await using (var context = await pg.Db.CreateDbContextAsync(Ct))
            Assert.Empty(await context.VacancyAssessmentRequests.ToListAsync(Ct));
        Assert.Equal(0, await NativePendingAsync());
        Assert.Empty(steps.Calls);
        Assert.Empty(await CardsAsync());
        Assert.Equal(VacancyStatus.New, (await VacancyAsync(vacancy.Id)).Status);
        await AssertNoApplicationsAsync();
        var id = await EnqueueAsync(producer, vacancy.Id);
        Assert.Equal(1, await NativePendingAsync());
        await StopAsync(producer);
        await HostAsync(true, steps);
        await UntilAsync(async () => (await RequestAsync(id)).Stage == VacancyAssessmentStage.Completed);
        Assert.Equal(2, steps.Calls.Count);
        await AssertExactCardAsync(vacancy.Id);
    }

    [Theory]
    [InlineData(VacancyAssessmentStage.Lettering)]
    [InlineData(VacancyAssessmentStage.Finishing)]
    public async Task A_one_shot_checkpoint_IO_failure_retries_the_saved_paid_result_without_another_score_or_letter_call(VacancyAssessmentStage checkpoint)
    {
        var vacancy = await SeedAsync();
        var fault = new FailCommit(checkpoint);
        var steps = new Steps();
        var host = await HostAsync(true, steps, fault);
        var id = await EnqueueAsync(host, vacancy.Id);
        await UntilAsync(async () => (await RequestAsync(id)).Stage == VacancyAssessmentStage.Completed);
        Assert.True(fault.Fired);
        Assert.Equal(0, (await RequestAsync(id)).Failures);
        Assert.Single(steps.Calls, call => call.Stage == VacancyAssessmentStage.Scoring);
        Assert.Single(steps.Calls, call => call.Stage == VacancyAssessmentStage.Lettering);
        var saved = await VacancyAsync(vacancy.Id);
        Assert.Equal(0, saved.Attempts);
        Assert.Null(saved.LastError);
        await AssertExactCardAsync(vacancy.Id);
        await AssertNoApplicationsAsync();
    }

    [Fact]
    public async Task Unsupported_versions_of_all_three_native_commands_are_dead_lettered_before_paid_work()
    {
        var steps = new Steps();
        var host = await HostAsync(true, steps);
        var bus = host.Services.GetRequiredService<IMessageBus>();
        var id = Guid.NewGuid();
        await bus.PublishAsync(new ScoreVacancy { RequestId = id, Version = 2 });
        await bus.PublishAsync(new PrepareVacancyLetter { RequestId = id, Version = 2 });
        await bus.PublishAsync(new FinishVacancyAssessment { RequestId = id, Version = 2 });
        await UntilAsync(async () =>
        {
            await using var context = await pg.Db.CreateDbContextAsync(Ct);
            return await context.Database.SqlQuery<int>($"SELECT count(*)::int AS \"Value\" FROM jobfinder_messages.wolverine_dead_letters").SingleAsync(Ct) == 3;
        });
        await using var read = await pg.Db.CreateDbContextAsync(Ct);
        var types = await read.Database.SqlQuery<string>($"SELECT message_type AS \"Value\" FROM jobfinder_messages.wolverine_dead_letters").ToListAsync(Ct);
        Assert.Equal(["jobfinder.discovery.vacancy-finish.v1", "jobfinder.discovery.vacancy-score.v1", "jobfinder.hiring.vacancy-letter.v1"],
            types.Order(StringComparer.Ordinal));
        Assert.Empty(steps.Calls);
        Assert.Empty(await read.VacancyAssessmentRequests.ToListAsync(Ct));
        Assert.Empty(await CardsAsync());
        await AssertNoApplicationsAsync();
    }

    [Fact]
    public async Task Concurrent_native_completions_share_the_six_per_hour_card_cap_and_ignore_older_cards()
    {
        var first = await SeedAsync();
        var second = await SeedAsync();
        var now = DateTimeOffset.UtcNow;
        await using (var context = await pg.Db.CreateDbContextAsync(Ct))
        {
            context.Notifications.AddRange(Enumerable.Range(0, 5).Select(index => new Notification
            {
                Kind = NotificationKind.VacancyApproval, Text = $"Synthetic earlier card {index}", CreatedAt = now.AddMinutes(-10), Status = NotificationStatus.Pending,
            }));
            context.Notifications.Add(new Notification
            {
                Kind = NotificationKind.VacancyApproval, Text = "Synthetic expired card", CreatedAt = now.AddHours(-2), Status = NotificationStatus.Sent,
            });
            await context.SaveChangesAsync(Ct);
        }
        var firstHold = Barrier();
        var secondHold = Barrier();
        var steps = new Steps { OnStage = (stage, input, token) => stage == VacancyAssessmentStage.Lettering
            ? (input.Vacancy.Id == first.Id ? firstHold : secondHold).WaitAsync(token) : Task.CompletedTask };
        var host = await HostAsync(true, steps);
        var ids = await Task.WhenAll(EnqueueAsync(host, first.Id), EnqueueAsync(host, second.Id));
        await Task.WhenAll(firstHold.Entered.Task, secondHold.Entered.Task).WaitAsync(Timeout, Ct);
        Assert.All(await Task.WhenAll(ids.Select(RequestAsync)), request => Assert.Equal(VacancyAssessmentStage.Lettering, request.Stage));
        firstHold.Release.TrySetResult();
        secondHold.Release.TrySetResult();
        await UntilAsync(async () => (await Task.WhenAll(ids.Select(RequestAsync))).All(request => request.Stage == VacancyAssessmentStage.Completed));
        var cards = await CardsAsync();
        Assert.Equal(7, cards.Count);
        Assert.Equal(VacancyAssessmentWorkflow.MaxApprovalCardsPerHour, cards.Count(card => card.CreatedAt > now.AddHours(-1)));
        var fresh = Assert.Single(cards, card => card.EntityId == first.Id || card.EntityId == second.Id);
        var winner = await VacancyAsync(fresh.EntityId!.Value);
        var expected = Cards.VacancyApproval(winner, ApplyRoutes.Resolve(winner, false));
        Assert.Equal(expected.Text, fresh.Text);
        Assert.Equal(JsonSerializer.Serialize(expected.Buttons), JsonSerializer.Serialize(fresh.Buttons));
        foreach (var vacancy in await Task.WhenAll(VacancyAsync(first.Id), VacancyAsync(second.Id)))
        {
            Assert.True(vacancy.Notified);
            Assert.Equal(VacancyStatus.Scored, vacancy.Status);
            Assert.Equal(Decision.Approval, vacancy.Decision);
            Assert.NotNull(vacancy.Score);
            Assert.NotNull(vacancy.CoverLetter);
        }
        Assert.Equal(4, steps.Calls.Count);
        await AssertNoApplicationsAsync();
    }

    private sealed record StepCall(VacancyAssessmentStage Stage, VacancyAssessmentInput Input);

    [Fact]
    public async Task Producer_failures_defer_and_retire_the_bad_rows_instead_of_starving_a_healthy_vacancy()
    {
        var first = await SeedAsync();
        var second = await SeedAsync();
        var healthy = await SeedAsync();
        var steps = new Steps();
        var producer = await HostAsync(false, steps, new FailCaptures([first.Id, second.Id]));
        using var worker = new VacancyWorker(producer.Services.GetRequiredService<IDbContextFactory<JobFinderDbContext>>(),
            producer.Services.GetRequiredService<VacancyAssessmentCapture>(), producer.Services.GetRequiredService<ProfileProvider>(),
            producer.Services.GetRequiredService<JobFinder.Pipeline.Jobs.JobTriggers>(),
            TimeProvider.System, Microsoft.Extensions.Logging.Abstractions.NullLogger<VacancyWorker>.Instance);
        async Task<int> BatchAsync() => await (Task<int>)typeof(VacancyWorker)
            .GetMethod("ProcessBatchAsync", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.Invoke(worker, [Ct])!;
        Assert.Equal(0, await BatchAsync());
        Assert.Equal(1, (await VacancyAsync(first.Id)).Attempts);
        Assert.Equal(1, (await VacancyAsync(second.Id)).Attempts);
        Assert.Equal(1, await BatchAsync());
        Assert.NotNull((await VacancyAsync(healthy.Id)).AssessmentCaptureId);
        for (var round = 0; round < 3; round++)
        {
            await using var context = await pg.Db.CreateDbContextAsync(Ct);
            await context.Vacancies.Where(row => row.Id == first.Id || row.Id == second.Id)
                .ExecuteUpdateAsync(update => update.SetProperty(row => row.UpdatedAt, DateTimeOffset.UtcNow.AddMinutes(-3)), Ct);
            Assert.Equal(0, await BatchAsync());
            Assert.Equal(0, await BatchAsync());
        }
        Assert.Equal(VacancyStatus.Failed, (await VacancyAsync(first.Id)).Status);
        Assert.Equal(VacancyStatus.Failed, (await VacancyAsync(second.Id)).Status);
        Assert.Equal(4, (await VacancyAsync(first.Id)).Attempts);
        Assert.Equal(4, (await VacancyAsync(second.Id)).Attempts);
        await using (var context = await pg.Db.CreateDbContextAsync(Ct))
            Assert.Equal(2, await context.Notifications.CountAsync(row => row.Kind == NotificationKind.Alert, Ct));
        await using (var context = await pg.Db.CreateDbContextAsync(Ct))
            Assert.Equal(1, await context.Database.SqlQuery<int>($"SELECT count(*)::int AS \"Value\" FROM jobfinder_queues.wolverine_queue_preparation").SingleAsync(Ct));
        Assert.Empty(steps.Calls);
        await StopAsync(producer);
        await HostAsync(true, steps);
        await UntilAsync(async () => (await VacancyAsync(healthy.Id)).Status == VacancyStatus.Scored);
        Assert.Equal(2, steps.Calls.Count);
        await AssertExactCardAsync(healthy.Id);
        await AssertNoApplicationsAsync();
    }

    [Theory]
    [InlineData("raised-auto", Decision.Approval)]
    [InlineData("raised-both", Decision.Ignore)]
    [InlineData("paused", Decision.Approval)]
    [InlineData("unpaused-approval", Decision.Approval)]
    public async Task Restrictive_current_automation_can_downgrade_but_never_upgrade_the_captured_decision(string change, Decision expected)
    {
        var vacancy = await SeedAsync(hh: true);
        var steps = new Steps { LlmScore = 91 };
        var producer = await HostAsync(false, steps);
        var automation = producer.Services.GetRequiredService<RuntimeSettings>().Automation;
        var policy = await automation.ReadAsync(Ct);
        if (change == "unpaused-approval")
            policy = await automation.PublishAsync(policy.Value with { AutoApplyPaused = true }, policy.Version, "synthetic-test", Ct);
        var id = await EnqueueAsync(producer, vacancy.Id);
        await using (var context = await pg.Db.CreateDbContextAsync(Ct))
        {
            var request = await context.VacancyAssessmentRequests.SingleAsync(row => row.Id == id, Ct);
            var input = Input(request);
            input.CanAutoApply = true;
            request.InputJson = JsonSerializer.Serialize(input, VacancyAssessmentJsonContext.Default.VacancyAssessmentInput);
            request.InputHash = CanonicalJson.Hash(request.InputJson);
            await context.SaveChangesAsync(Ct);
        }
        var latest = change switch
        {
            "raised-auto" => policy.Value with { AutoApplyThreshold = 95 },
            "raised-both" => policy.Value with { AutoApplyThreshold = 95, ApprovalThreshold = 95 },
            "paused" => policy.Value with { AutoApplyPaused = true },
            _ => policy.Value with { AutoApplyPaused = false },
        };
        await automation.PublishAsync(latest, policy.Version, "synthetic-test", Ct);
        await StopAsync(producer);
        await HostAsync(true, steps);
        await UntilAsync(async () => (await RequestAsync(id)).Stage == VacancyAssessmentStage.Completed);
        var score = JsonSerializer.Deserialize((await RequestAsync(id)).ScoreJson!, VacancyAssessmentJsonContext.Default.AssessmentScore)!;
        Assert.Equal(change == "unpaused-approval" ? Decision.Approval : Decision.AutoApply, score.Decision);
        Assert.Equal(expected, (await VacancyAsync(vacancy.Id)).Decision);
        if (expected == Decision.Approval) await AssertExactCardAsync(vacancy.Id);
        else Assert.Empty(await CardsAsync());
        await AssertNoApplicationsAsync();
    }

    [Fact]
    public async Task Corrupt_snapshot_is_quarantined_and_does_not_hold_a_slot_or_run_paid_work()
    {
        var vacancy = await SeedAsync();
        var steps = new Steps();
        var producer = await HostAsync(false, steps);
        var id = await EnqueueAsync(producer, vacancy.Id);
        await using (var context = await pg.Db.CreateDbContextAsync(Ct))
        {
            var request = await context.VacancyAssessmentRequests.SingleAsync(row => row.Id == id, Ct);
            request.InputJson = "{}";
            await context.SaveChangesAsync(Ct);
        }
        await StopAsync(producer);
        var consumer = await HostAsync(true, steps);
        await UntilAsync(async () => (await RequestAsync(id)).Stage == VacancyAssessmentStage.Failed);
        await UntilAsync(async () =>
        {
            await using var context = await pg.Db.CreateDbContextAsync(Ct);
            return await context.Database.SqlQuery<int>($"SELECT count(*)::int AS \"Value\" FROM jobfinder_messages.wolverine_dead_letters").SingleAsync(Ct) == 1;
        });
        var quarantined = await RequestAsync(id);
        Assert.Null(quarantined.ClaimEnvelopeId);
        Assert.NotNull(quarantined.CompletedAt);
        Assert.Contains("quarantined", quarantined.Error!);
        Assert.Equal(VacancyStatus.New, (await VacancyAsync(vacancy.Id)).Status);
        Assert.Empty(steps.Calls);
        await using (var context = await pg.Db.CreateDbContextAsync(Ct))
            Assert.Equal(1, await context.Notifications.CountAsync(notification => notification.Kind == NotificationKind.Alert && notification.EntityId == vacancy.Id, Ct));
        var replacement = await EnqueueAsync(consumer, vacancy.Id);
        Assert.NotEqual(id, replacement);
        await UntilAsync(async () => (await RequestAsync(replacement)).Stage == VacancyAssessmentStage.Completed);
        Assert.Equal(2, steps.Calls.Count);
        await AssertExactCardAsync(vacancy.Id);
        await AssertNoApplicationsAsync();
    }

    [Fact]
    public async Task Populated_assessment_history_refuses_destructive_downgrade_and_stays_executable()
    {
        var vacancy = await SeedAsync();
        var steps = new Steps();
        var producer = await HostAsync(false, steps);
        var id = await EnqueueAsync(producer, vacancy.Id);
        var before = await RequestAsync(id);
        await StopAsync(producer);
        await using (var context = await pg.Db.CreateDbContextAsync(Ct))
        {
            var error = await Assert.ThrowsAsync<Npgsql.PostgresException>(() => MigrationTestRunner.DowngradeAndRestoreAsync(context, "20260930132955_EmployerResumeDependency", Ct));
            Assert.Equal("P0001", error.SqlState);
            Assert.Contains("assessment rollback refused", error.MessageText);
            Assert.Contains("20260930170205_DurableVacancyAssessment", await context.Database.GetAppliedMigrationsAsync(Ct));
        }
        Assert.Equal(before.InputJson, (await RequestAsync(id)).InputJson);
        await HostAsync(true, steps);
        await UntilAsync(async () => (await RequestAsync(id)).Stage == VacancyAssessmentStage.Completed);
        Assert.Equal(2, steps.Calls.Count);
        await AssertExactCardAsync(vacancy.Id);
    }

    private sealed class Steps : IVacancyAssessmentSteps
    {
        public ConcurrentQueue<StepCall> Calls { get; } = new();
        public Func<VacancyAssessmentStage, VacancyAssessmentInput, CancellationToken, Task>? OnStage { get; set; }
        public int LlmScore { get; set; } = 74;
        public string? LetterWeakness { get; set; }

        public void EnsureCompatible(VacancyAssessmentInput input) { }

        private async Task CalledAsync(VacancyAssessmentStage stage, VacancyAssessmentInput input, CancellationToken ct)
        {
            Calls.Enqueue(new StepCall(stage, input));
            if (OnStage is { } hook) await hook(stage, input, ct).WaitAsync(Timeout, ct);
        }

        public async Task<AssessmentScore> ScoreAsync(VacancyAssessmentInput input, CancellationToken ct)
        {
            await CalledAsync(VacancyAssessmentStage.Scoring, input, ct);
            var score = Math.Clamp(LlmScore + input.SalaryAdjustment, 0, 100);
            var policy = input.Automation;
            return new AssessmentScore
            {
                CostUsd = input.Preview is null ? null : 0.004m,
                Score = score, Status = VacancyStatus.Scored, EnglishRequired = EnglishLevel.B1, Legitimacy = Legitimacy.Ok,
                Decision = input.Preview is not null ? Decision.None : DecisionPolicy.Decide(new DecisionInput(score, input.CanAutoApply, input.AutoToday),
                    new DecisionSettings(policy.AutoApplyThreshold, policy.ApprovalThreshold, policy.DailyAutoApplyLimit, policy.AutoApplyPaused)),
                Evaluation = new VacancyEvaluation
                {
                    Score = LlmScore, Summary = $"Synthetic match for revision {input.Vacancy.Revision}.", Model = "synthetic-scorer",
                    Strengths = ["Synthetic .NET experience"], Keywords = ["C#", "PostgreSQL"], Language = "en", SalaryNote = input.SalaryNote,
                },
            };
        }

        public async Task<AssessmentLetter> LetterAsync(VacancyAssessmentInput input, AssessmentScore score, CancellationToken ct)
        {
            await CalledAsync(VacancyAssessmentStage.Lettering, input, ct);
            var text = $"Synthetic letter for vacancy {input.Vacancy.Id}, revision {input.Vacancy.Revision}, score {score.Score}.";
            return new AssessmentLetter
            {
                Text = text,
                Review = LetterWeakness == "missing-review" ? null : new ApplicationLetterReview
                {
                    Score = LetterWeakness == "below-bar" ? 8 : 10,
                    TextHash = LetterWeakness == "wrong-text" ? Fingerprint.Of("Different synthetic letter") : Fingerprint.Of(text),
                    ContextHash = input.Recipe.Hash, Recipe = input.Recipe, CheckedAt = DateTimeOffset.UtcNow,
                },
            };
        }
    }

    private sealed class Hold
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task WaitAsync(CancellationToken ct)
        {
            Entered.TrySetResult();
            await Release.Task.WaitAsync(Timeout, ct);
        }
    }

    private sealed class FailCommit(VacancyAssessmentStage at, Hold? retry = null) : DbTransactionInterceptor
    {
        private int _armed = 1;
        public bool Fired { get; private set; }
        public bool SawScoredVacancy { get; private set; }
        public bool SawApprovalCard { get; private set; }

        public override async ValueTask<InterceptionResult> TransactionCommittingAsync(DbTransaction transaction, TransactionEventData eventData,
            InterceptionResult result, CancellationToken ct = default)
        {
            var context = eventData.Context;
            if (context is not null && context.ChangeTracker.Entries<VacancyAssessmentRequest>().Any(entry => entry.Entity.Stage == at))
            {
                if (Interlocked.Exchange(ref _armed, 0) == 1)
                {
                    Fired = true;
                    SawScoredVacancy = context.ChangeTracker.Entries<Vacancy>().Any(entry => entry.Entity.Status == VacancyStatus.Scored && entry.Entity.Score is not null);
                    SawApprovalCard = context.ChangeTracker.Entries<Notification>().Any(entry => entry.Entity.Kind == NotificationKind.VacancyApproval);
                    throw new IOException("synthetic transaction commit fault");
                }
                if (retry is not null) await retry.WaitAsync(ct);
            }
            return result;
        }
    }

    private sealed class FailCaptures(HashSet<long> ids) : DbTransactionInterceptor
    {
        public override ValueTask<InterceptionResult> TransactionCommittingAsync(DbTransaction transaction, TransactionEventData eventData,
            InterceptionResult result, CancellationToken ct = default)
        {
            if (eventData.Context?.ChangeTracker.Entries<Vacancy>()
                .Any(entry => entry.Entity.AssessmentCaptureId is not null && ids.Contains(entry.Entity.Id)) == true)
                throw new IOException("synthetic persistent capture failure");
            return ValueTask.FromResult(result);
        }
    }

    private sealed class StoredEnvelope
    {
        public string MessageType { get; set; } = "";
        public byte[] Body { get; set; } = [];
        public DateTimeOffset ExecutionTime { get; set; }
    }

    private sealed class Factory(DbContextOptions<JobFinderDbContext> options) : IDbContextFactory<JobFinderDbContext>
    {
        public JobFinderDbContext CreateDbContext() => new(options);
    }

    private sealed class NoNetwork : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(new RejectNetwork());
    }

    private sealed class RejectNetwork : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            throw new InvalidOperationException("Network access is forbidden in synthetic vacancy assessment tests.");
    }
}
