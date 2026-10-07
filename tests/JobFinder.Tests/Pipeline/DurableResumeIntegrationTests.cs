using System.Collections.Concurrent;
using System.Text.Json;
using JobFinder.Core.Domain;
using JobFinder.Core.Profile;
using JobFinder.Core.Resume;
using JobFinder.Data;
using JobFinder.Llm.Client;
using JobFinder.Llm.Tasks;
using JobFinder.Pipeline;
using JobFinder.Pipeline.Messaging;
using JobFinder.Pipeline.Modules.Discovery;
using JobFinder.Pipeline.Modules.Preparation;
using JobFinder.Pipeline.Settings;
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

public sealed partial class DurableResumeIntegrationTests(PostgresFixture pg) : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private readonly string _artifacts = Path.Combine(OperatingSystem.IsWindows() ? Path.GetPathRoot(AppContext.BaseDirectory)! : Path.GetTempPath(),
        "Temp", "jobfinder-resume-tests-" + Guid.NewGuid().ToString("N"));

    public async ValueTask InitializeAsync()
    {
        Assert.SkipWhen(pg.SkipReason is not null, pg.SkipReason ?? "");
        using var host = await HostAsync(false, new Steps());
        await host.StartAsync(Ct);
        try
        {
            await host.Services.GetRequiredService<RuntimeSettings>().Automation.ReadAsync(Ct);
            await host.Services.GetRequiredService<LlmPolicyStore>().ReadAsync(Ct);
            await host.Services.GetRequiredService<SearchPolicyStore>().ReadAsync(Ct);
            await host.Services.GetRequiredService<JobFinder.Pipeline.Modules.Engagement.EngagementPolicyStore>().ReadAsync(Ct);
        }
        finally { await host.StopAsync(CancellationToken.None); }
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        await context.ResumePreparationRequests.ExecuteDeleteAsync(Ct);
        await context.Database.ExecuteSqlRawAsync("DELETE FROM jobfinder_queues.wolverine_queue_hiring_prepare; DELETE FROM jobfinder_queues.wolverine_queue_hiring_prepare_scheduled; DELETE FROM jobfinder_messages.wolverine_incoming_envelopes; DELETE FROM jobfinder_messages.wolverine_outgoing_envelopes; DELETE FROM jobfinder_messages.wolverine_dead_letters", Ct);
        await NotificationAdmissionAssertions.ResetAsync(context, Ct);
        await context.TailoredResumes.ExecuteDeleteAsync(Ct);
        await context.LlmCalls.ExecuteDeleteAsync(Ct);
        await context.Vacancies.ExecuteDeleteAsync(Ct);
    }

    public ValueTask DisposeAsync()
    {
        if (Directory.Exists(_artifacts)) Directory.Delete(_artifacts, true);
        return ValueTask.CompletedTask;
    }

    private async Task<IHost> HostAsync(bool consumer, IResumePreparationSteps? steps, SaveChangesInterceptor? interceptor = null, IHttpClientFactory? http = null,
        bool fastRecovery = false)
    {
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { DisableDefaults = true });
        builder.Configuration.AddJsonFile(Path.Combine(AppContext.BaseDirectory, "config", "appsettings.json"));
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Database:ConnectionString"] = context.Database.GetConnectionString(), ["Paths:Prompts"] = Fixture.PromptsDir,
            ["Paths:Profile"] = Path.Combine(_artifacts, "profile"), ["Paths:Data"] = Path.Combine(_artifacts, "data"),
            ["Paths:Assets"] = Path.GetFullPath(Path.Combine(Fixture.PromptsDir, "..", "assets")), ["Llm:ApiKey"] = "synthetic-test-key",
        });
        builder.Logging.AddSimpleConsole(options => options.SingleLine = true).SetMinimumLevel(Microsoft.Extensions.Logging.LogLevel.Warning);
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
        if (steps is not null)
        {
            builder.Services.RemoveAll<IResumePreparationSteps>();
            builder.Services.AddSingleton(steps);
        }
        if (http is not null)
        {
            builder.Services.RemoveAll<IHttpClientFactory>();
            builder.Services.AddSingleton(http);
        }
        builder.AddJobFinderMessaging(consumer);
        if (fastRecovery) builder.Services.ConfigureWolverine(MessagingProbe.ResumeProbe.FastRecovery);
        if (consumer) builder.Services.AddJobFinderInitialization();
        return builder.Build();
    }

    private async Task<ResumePreparationInput> InputAsync(IHost host, long id = 902001)
    {
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        var vacancy = await context.Vacancies.SingleOrDefaultAsync(row => row.Id == id, Ct);
        if (vacancy is null)
        {
            vacancy = new Vacancy { Id = id, Title = "Senior .NET Developer", CompanyName = "Synthetic Company", Description = "Build .NET services.",
                Url = $"https://example.invalid/vacancy/{id}", Fingerprint = id.ToString(), FirstSeenAt = DateTimeOffset.UtcNow };
            context.Vacancies.Add(vacancy);
            await context.SaveChangesAsync(Ct);
        }
        var operation = await host.Services.GetRequiredService<JobFinderLlm>().ForOperationAsync(Ct);
        var input = new ResumePreparationInput
        {
            Profile = new CandidateProfile { Name = "Synthetic Candidate", NameLatin = "Synthetic Candidate", Title = "Senior .NET Developer" },
            ResumeText = "Synthetic candidate developed .NET services.", FactsText = "Synthetic verified facts", Vacancy = ResumeVacancyInput.From(vacancy),
            Operation = operation.CaptureOperation(),
        };
        input.Recipe = ResumePreparationSteps.Recipe(operation, input);
        return input;
    }

    private async Task<ResumePreparationRequest> RequestAsync(Guid id)
    {
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        return await context.ResumePreparationRequests.AsNoTracking().SingleAsync(row => row.Id == id, Ct);
    }

    private async Task<int> NoticesAsync()
    {
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        return await context.Notifications.CountAsync(Ct);
    }

    private static async Task UntilAsync(Func<Task<bool>> condition)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(35);
        while (!await condition())
        {
            Assert.True(DateTimeOffset.UtcNow < deadline, "durable resume did not reach the expected checkpoint");
            await Task.Delay(50, Ct);
        }
    }

    [Fact]
    public async Task Producer_exits_before_native_handlers_complete_four_stages_and_one_atomic_notice()
    {
        var steps = new Steps();
        Guid id;
        using (var producer = await HostAsync(false, steps))
        {
            await producer.StartAsync(Ct);
            id = await producer.Services.GetRequiredService<ResumePreparationRequests>().EnqueueAsync(await InputAsync(producer), Ct);
            Assert.Empty(steps.Calls);
            Assert.False(producer.Services.GetRequiredService<ServerOwnership>().IsOwned);
            Assert.False(producer.Services.GetRequiredService<IWolverineRuntime>().Options.Durability.DurabilityAgentEnabled);
            await producer.StopAsync(Ct);
        }
        using var consumer = await HostAsync(true, steps);
        await consumer.StartAsync(Ct);
        try
        {
            await UntilAsync(async () => (await RequestAsync(id)).Stage == ResumePreparationStage.Completed);
            Assert.Equal(["Drafting:1", "Printing:1", "Reviewing:1", "Finalizing:1"], steps.Calls);
            Assert.Equal(1, await NoticesAsync());
            var result = await consumer.Services.GetRequiredService<ResumePreparationRequests>().WaitAsync(id, Ct);
            Assert.Equal("writer-1", result.Model);
            var report = JsonSerializer.Deserialize(result.ReportJson!, ResumeJsonContext.Default.ResumeReport)!;
            Assert.True(report.Ready);
            Assert.Equal(10, report.Review!.Scored().Score);
            Assert.Equal(id, report.Recipe!.OperationId);
            var bus = consumer.Services.GetRequiredService<IMessageBus>();
            await bus.PublishAsync(new DraftResume { RequestId = id });
            await bus.PublishAsync(new PrintResume { RequestId = id });
            await bus.PublishAsync(new ReviewResume { RequestId = id });
            await bus.PublishAsync(new FinishResume { RequestId = id });
            await Task.Delay(1500, Ct);
            Assert.Equal(4, steps.Calls.Count);
            await using var context = await pg.Db.CreateDbContextAsync(Ct);
            Assert.Single(await context.TailoredResumes.ToListAsync(Ct));
            Assert.Equal(1, await NoticesAsync());
            await NotificationAdmissionAssertions.JoinedAsync(context, await context.Notifications.SingleAsync(Ct), Ct);
        }
        finally { await consumer.StopAsync(Ct); }
    }

    [Fact]
    public async Task Concurrent_producers_freeze_one_input_and_publish_one_business_request()
    {
        using var producer = await HostAsync(false, new Steps());
        await producer.StartAsync(Ct);
        try
        {
            var input = await InputAsync(producer);
            var inputs = await Task.WhenAll(Enumerable.Range(0, 8).Select(async index =>
            {
                var operation = await producer.Services.GetRequiredService<JobFinderLlm>().ForOperationAsync(Ct);
                return input with { Operation = operation.CaptureOperation(), Recipe = ResumePreparationSteps.Recipe(operation, input) };
            }));
            var ids = await Task.WhenAll(inputs.Select(value => producer.Services.GetRequiredService<ResumePreparationRequests>().EnqueueAsync(value, Ct)));
            Assert.Single(ids.Distinct());
            input.Profile.Name = "mutated after publication";
            var saved = JsonSerializer.Deserialize((await RequestAsync(ids[0])).InputJson, ResumePreparationJsonContext.Default.ResumePreparationInput)!;
            Assert.Equal("Synthetic Candidate", saved.Profile.Name);
            await using var context = await pg.Db.CreateDbContextAsync(Ct);
            Assert.Equal(1, await context.Database.SqlQuery<int>($"SELECT count(*)::int AS \"Value\" FROM jobfinder_queues.wolverine_queue_hiring_prepare").SingleAsync(Ct));
            Assert.Single(await context.ResumePreparationRequests.ToListAsync(Ct));
        }
        finally { await producer.StopAsync(Ct); }
    }

    [Theory]
    [InlineData(ResumePreparationStage.Printing)]
    [InlineData(ResumePreparationStage.Reviewing)]
    [InlineData(ResumePreparationStage.Finalizing)]
    public async Task Restart_after_a_later_stage_failure_does_not_repeat_completed_costly_stages(ResumePreparationStage failure)
    {
        var first = new Steps { OnStage = (stage, round, token) => stage == failure ? throw new InvalidOperationException("isolated stage failure") : Task.CompletedTask };
        Guid id;
        using (var consumer = await HostAsync(true, first))
        {
            await consumer.StartAsync(Ct);
            id = await consumer.Services.GetRequiredService<ResumePreparationRequests>().EnqueueAsync(await InputAsync(consumer), Ct);
            await UntilAsync(async () => (await RequestAsync(id)).Failures == 1);
            Assert.Equal(failure, (await RequestAsync(id)).Stage);
            await consumer.StopAsync(Ct);
        }
        var second = new Steps();
        using var restarted = await HostAsync(true, second);
        await restarted.StartAsync(Ct);
        try
        {
            await UntilAsync(async () => (await RequestAsync(id)).Stage == ResumePreparationStage.Completed);
            Assert.DoesNotContain("Drafting:1", second.Calls);
            if (failure != ResumePreparationStage.Printing) Assert.DoesNotContain("Printing:1", second.Calls);
            if (failure == ResumePreparationStage.Finalizing) Assert.DoesNotContain("Reviewing:1", second.Calls);
            Assert.Equal(1, await NoticesAsync());
        }
        finally { await restarted.StopAsync(Ct); }
    }

    [Fact]
    public async Task A_distinct_duplicate_envelope_cannot_execute_an_already_claimed_costly_draft()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var steps = new Steps { OnStage = async (stage, round, token) =>
        {
            if (stage == ResumePreparationStage.Drafting) { entered.TrySetResult(); await release.Task.WaitAsync(token); }
        } };
        using var consumer = await HostAsync(true, steps);
        await consumer.StartAsync(Ct);
        try
        {
            var id = await consumer.Services.GetRequiredService<ResumePreparationRequests>().EnqueueAsync(await InputAsync(consumer), Ct);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(15), Ct);
            Assert.NotNull((await RequestAsync(id)).ClaimEnvelopeId);
            await consumer.Services.GetRequiredService<IMessageBus>().PublishAsync(new DraftResume { RequestId = id });
            await Task.Delay(1500, Ct);
            Assert.Single(steps.Calls);
            release.SetResult();
            await UntilAsync(async () => (await RequestAsync(id)).Stage == ResumePreparationStage.Completed);
            Assert.Single(steps.Calls, call => call.StartsWith("Drafting", StringComparison.Ordinal));
        }
        finally { release.TrySetResult(); await consumer.StopAsync(Ct); }
    }

    [Fact]
    public async Task Native_redelivery_with_the_original_envelope_identity_recovers_a_persisted_stage_claim()
    {
        Guid id;
        Guid envelope;
        var steps = new Steps();
        using (var producer = await HostAsync(false, steps))
        {
            await producer.StartAsync(Ct);
            id = await producer.Services.GetRequiredService<ResumePreparationRequests>().EnqueueAsync(await InputAsync(producer), Ct);
            await using var context = await pg.Db.CreateDbContextAsync(Ct);
            envelope = await context.Database.SqlQuery<Guid>($"SELECT id AS \"Value\" FROM jobfinder_queues.wolverine_queue_hiring_prepare").SingleAsync(Ct);
            var request = await context.ResumePreparationRequests.SingleAsync(row => row.Id == id, Ct);
            request.ClaimEnvelopeId = envelope;
            await context.SaveChangesAsync(Ct);
            await producer.StopAsync(Ct);
        }
        using var consumer = await HostAsync(true, steps);
        await consumer.StartAsync(Ct);
        try
        {
            await UntilAsync(async () => (await RequestAsync(id)).Stage == ResumePreparationStage.Completed);
            Assert.Equal(4, steps.Calls.Count);
            Assert.Null((await RequestAsync(id)).ClaimEnvelopeId);
        }
        finally { await consumer.StopAsync(Ct); }
    }

    [Fact]
    public async Task Superseding_a_running_request_discards_its_output_and_commits_only_the_new_snapshot()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = 0;
        var steps = new Steps { OnStage = async (stage, round, token) =>
        {
            if (stage == ResumePreparationStage.Drafting && Interlocked.Increment(ref first) == 1)
            { entered.TrySetResult(); await release.Task.WaitAsync(token); }
        } };
        using var consumer = await HostAsync(true, steps);
        await consumer.StartAsync(Ct);
        try
        {
            var requests = consumer.Services.GetRequiredService<ResumePreparationRequests>();
            var input = await InputAsync(consumer);
            var before = await requests.EnqueueAsync(input, Ct);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(15), Ct);
            var operation = await consumer.Services.GetRequiredService<JobFinderLlm>().ForOperationAsync(Ct);
            var changed = input with { FactsText = "updated confirmed facts", Operation = operation.CaptureOperation() };
            changed.Recipe = ResumePreparationSteps.Recipe(operation, changed);
            var after = await requests.EnqueueAsync(changed, Ct);
            release.SetResult();
            await UntilAsync(async () => (await RequestAsync(after)).Stage == ResumePreparationStage.Completed);
            Assert.Equal(ResumePreparationStage.Cancelled, (await RequestAsync(before)).Stage);
            Assert.Null((await RequestAsync(before)).CurrentJson);
            await Assert.ThrowsAsync<ResumePreparationSupersededException>(() => requests.WaitAsync(before, Ct));
            await using var context = await pg.Db.CreateDbContextAsync(Ct);
            Assert.Single(await context.TailoredResumes.ToListAsync(Ct));
            Assert.Equal(1, await NoticesAsync());
        }
        finally { release.TrySetResult(); await consumer.StopAsync(Ct); }
    }

    [Fact]
    public async Task A_review_outage_schedules_only_review_and_leaves_a_healthy_vacancy_runnable()
    {
        var steps = new Steps { OnStage = (stage, round, token) => stage == ResumePreparationStage.Reviewing
            ? throw new LlmUnavailableException("isolated review outage") : Task.CompletedTask };
        using var consumer = await HostAsync(true, steps);
        await consumer.StartAsync(Ct);
        try
        {
            var requests = consumer.Services.GetRequiredService<ResumePreparationRequests>();
            var id = await requests.EnqueueAsync(await InputAsync(consumer), Ct);
            await UntilAsync(async () => (await RequestAsync(id)).RetryAt is not null);
            var waiting = await RequestAsync(id);
            Assert.Equal(ResumePreparationStage.Reviewing, waiting.Stage);
            Assert.Equal(0, waiting.Failures);
            Assert.Null(waiting.ClaimEnvelopeId);
            Assert.NotNull(waiting.CurrentJson);
            Assert.True(waiting.RetryAt > DateTimeOffset.UtcNow.AddMinutes(1));
            steps.OnStage = null;
            var healthy = await requests.EnqueueAsync(await InputAsync(consumer, 902002), Ct);
            await UntilAsync(async () => (await RequestAsync(healthy)).Stage == ResumePreparationStage.Completed);
            Assert.Equal(ResumePreparationStage.Reviewing, (await RequestAsync(id)).Stage);
            await consumer.Services.GetRequiredService<IMessageBus>().PublishAsync(new ReviewResume { RequestId = id });
            await Task.Delay(500, Ct);
            Assert.Equal(7, steps.Calls.Count);
            Assert.Equal(1, await NoticesAsync());
        }
        finally { await consumer.StopAsync(Ct); }
    }

    [Fact]
    public async Task Revision_stops_at_nine_without_weakening_the_checklist_or_starting_extra_rounds()
    {
        var steps = new Steps { Review = round => Checklist(round == 2 ? "readability" : "keywords") };
        using var consumer = await HostAsync(true, steps);
        await consumer.StartAsync(Ct);
        try
        {
            var id = await consumer.Services.GetRequiredService<ResumePreparationRequests>().EnqueueAsync(await InputAsync(consumer), Ct);
            await UntilAsync(async () => (await RequestAsync(id)).Stage == ResumePreparationStage.Completed);
            var result = await consumer.Services.GetRequiredService<ResumePreparationRequests>().WaitAsync(id, Ct);
            Assert.Equal("writer-2", result.Model);
            Assert.Equal(2, steps.Calls.Count(call => call.StartsWith("Drafting", StringComparison.Ordinal)));
            Assert.Equal(9, JsonSerializer.Deserialize(result.ReportJson!, ResumeJsonContext.Default.ResumeReport)!.Review!.Scored().Score);
        }
        finally { await consumer.StopAsync(Ct); }
    }

    [Fact]
    public async Task A_below_bar_best_round_finishes_rejected_after_four_rounds_without_weakening_readiness()
    {
        var steps = new Steps { Review = round => Checklist(round == 2 ? "keywords" : "truth") };
        using var consumer = await HostAsync(true, steps);
        await consumer.StartAsync(Ct);
        try
        {
            var id = await consumer.Services.GetRequiredService<ResumePreparationRequests>().EnqueueAsync(await InputAsync(consumer), Ct);
            await UntilAsync(async () => (await RequestAsync(id)).Stage == ResumePreparationStage.Completed);
            var result = await consumer.Services.GetRequiredService<ResumePreparationRequests>().WaitAsync(id, Ct);
            Assert.Equal(4, steps.Calls.Count(call => call.StartsWith("Drafting", StringComparison.Ordinal)));
            Assert.Equal("writer-2", result.Model);
            Assert.False(JsonSerializer.Deserialize(result.ReportJson!, ResumeJsonContext.Default.ResumeReport)!.Ready);
        }
        finally { await consumer.StopAsync(Ct); }
    }

    [Theory]
    [InlineData(ResumePreparationStage.Reviewing)]
    [InlineData(ResumePreparationStage.Finalizing)]
    public async Task A_replaced_or_missing_PDF_reprints_the_saved_document_without_another_writer_call(ResumePreparationStage changedAt)
    {
        var once = 0;
        var steps = new Steps { OnStage = (stage, round, token) => stage == changedAt && Interlocked.Increment(ref once) == 1
            ? throw new ResumePrintChangedException() : Task.CompletedTask };
        using var consumer = await HostAsync(true, steps);
        await consumer.StartAsync(Ct);
        try
        {
            var id = await consumer.Services.GetRequiredService<ResumePreparationRequests>().EnqueueAsync(await InputAsync(consumer), Ct);
            await UntilAsync(async () => (await RequestAsync(id)).Stage == ResumePreparationStage.Completed);
            Assert.Single(steps.Calls, call => call.StartsWith("Drafting", StringComparison.Ordinal));
            Assert.Equal(2, steps.Calls.Count(call => call.StartsWith("Printing", StringComparison.Ordinal)));
            Assert.Equal(0, (await RequestAsync(id)).Failures);
        }
        finally { await consumer.StopAsync(Ct); }
    }

    [Fact]
    public async Task A_stage_commit_failure_rolls_back_the_next_command_and_native_retry_recovers()
    {
        var fault = new FailSave(ResumePreparationStage.Reviewing);
        var steps = new Steps();
        using var consumer = await HostAsync(true, steps, fault);
        await consumer.StartAsync(Ct);
        try
        {
            var id = await consumer.Services.GetRequiredService<ResumePreparationRequests>().EnqueueAsync(await InputAsync(consumer), Ct);
            await UntilAsync(async () => (await RequestAsync(id)).Failures == 1);
            Assert.Equal(ResumePreparationStage.Printing, (await RequestAsync(id)).Stage);
            Assert.DoesNotContain("Reviewing:1", steps.Calls);
            await UntilAsync(async () => (await RequestAsync(id)).Stage == ResumePreparationStage.Completed);
            Assert.Single(steps.Calls, call => call.StartsWith("Drafting", StringComparison.Ordinal));
            Assert.Single(steps.Calls, call => call.StartsWith("Reviewing", StringComparison.Ordinal));
            Assert.Equal(2, steps.Calls.Count(call => call.StartsWith("Printing", StringComparison.Ordinal)));
        }
        finally { await consumer.StopAsync(Ct); }
    }

    [Fact]
    public async Task A_final_commit_failure_rolls_back_result_and_notice_together_and_accounts_paid_failed_calls()
    {
        var fault = new FailSave(ResumePreparationStage.Completed);
        var steps = new Steps();
        using var consumer = await HostAsync(true, steps, fault);
        await consumer.StartAsync(Ct);
        try
        {
            var input = await InputAsync(consumer);
            await using (var context = await pg.Db.CreateDbContextAsync(Ct))
            {
                context.LlmCalls.AddRange(new LlmCall { OperationId = input.Operation.OperationId, Purpose = "Resume", Model = "writer", CostUsd = 0.1m, At = DateTimeOffset.UtcNow, Ok = true },
                    new LlmCall { OperationId = input.Operation.OperationId, Purpose = "Resume", Model = "failed-review", CostUsd = 0.2m, At = DateTimeOffset.UtcNow, Ok = false },
                    new LlmCall { OperationId = Guid.NewGuid(), Purpose = "Resume", Model = "unrelated", CostUsd = 5m, At = DateTimeOffset.UtcNow, Ok = true });
                await context.SaveChangesAsync(Ct);
            }
            var id = await consumer.Services.GetRequiredService<ResumePreparationRequests>().EnqueueAsync(input, Ct);
            await UntilAsync(async () => (await RequestAsync(id)).Failures == 1);
            await using (var context = await pg.Db.CreateDbContextAsync(Ct)) Assert.Empty(await context.TailoredResumes.ToListAsync(Ct));
            Assert.Equal(0, await NoticesAsync());
            Assert.Null((await RequestAsync(id)).ResultId);
            await UntilAsync(async () => (await RequestAsync(id)).Stage == ResumePreparationStage.Completed);
            var result = await consumer.Services.GetRequiredService<ResumePreparationRequests>().WaitAsync(id, Ct);
            Assert.Equal(0.3m, result.CostUsd);
            Assert.Equal(1, await NoticesAsync());
            Assert.Single(steps.Calls, call => call.StartsWith("Reviewing", StringComparison.Ordinal));
        }
        finally { await consumer.StopAsync(Ct); }
    }

    [Fact]
    public async Task Unknown_commands_are_dead_lettered_without_executing_a_stage()
    {
        var steps = new Steps();
        using var consumer = await HostAsync(true, steps);
        await consumer.StartAsync(Ct);
        try
        {
            await consumer.Services.GetRequiredService<IMessageBus>().PublishAsync(new DraftResume { RequestId = Guid.NewGuid(), Version = 2 });
            await UntilAsync(async () =>
            {
                await using var context = await pg.Db.CreateDbContextAsync(Ct);
                return await context.Database.SqlQuery<int>($"SELECT count(*)::int AS \"Value\" FROM jobfinder_messages.wolverine_dead_letters").SingleAsync(Ct) == 1;
            });
            Assert.Empty(steps.Calls);
        }
        finally { await consumer.StopAsync(Ct); }
    }

    [Fact]
    public async Task Populated_resume_request_history_cannot_be_silently_dropped_by_rollback()
    {
        using var producer = await HostAsync(false, new Steps());
        await producer.StartAsync(Ct);
        try
        {
            await producer.Services.GetRequiredService<ResumePreparationRequests>().EnqueueAsync(await InputAsync(producer), Ct);
            await using var context = await pg.Db.CreateDbContextAsync(Ct);
            try
            {
                var error = await Assert.ThrowsAsync<Npgsql.PostgresException>(() => MigrationTestRunner.DowngradeAndRestoreAsync(context, "20260930084019_DurableHhLinkRequests", Ct));
                Assert.Equal("P0001", error.SqlState);
                Assert.Single(await context.ResumePreparationRequests.ToListAsync(Ct));
            }
            finally { await context.Database.MigrateAsync(CancellationToken.None); }
            Assert.Empty(await context.Database.GetPendingMigrationsAsync(Ct));
        }
        finally { await producer.StopAsync(Ct); }
    }

    private static ResumeReview Checklist(string? failed = null) => new()
    {
        Checks = ResumeReview.Criteria.Keys.Select(id => new ReviewCheck { Id = id, Passed = id != failed }).ToList(),
    };

    private sealed class Steps : IResumePreparationSteps
    {
        public ConcurrentQueue<string> Calls { get; } = new();
        public List<ResumePreparationInput> Inputs { get; } = [];
        public Func<ResumePreparationStage, int, CancellationToken, Task>? OnStage { get; set; }
        public Func<int, ResumeReview>? Review { get; set; }

        private async Task CalledAsync(ResumePreparationInput input, ResumePreparationStage stage, int round, CancellationToken ct)
        {
            Calls.Enqueue($"{stage}:{round}");
            lock (Inputs) Inputs.Add(input);
            if (OnStage is { } hook) await hook(stage, round, ct);
        }
        public async Task<ResumeRound> DraftAsync(ResumePreparationInput input, int round, ResumeRound? best, CancellationToken ct)
        {
            await CalledAsync(input, ResumePreparationStage.Drafting, round, ct);
            return new ResumeRound { Number = round, Model = $"writer-{round}", Document = new ResumeDocument { Language = "en", Headline = "Senior .NET Developer", Summary = $"round {round}" } };
        }
        public async Task<ResumeRound> PrintAsync(ResumePreparationInput input, ResumeRound current, CancellationToken ct)
        {
            await CalledAsync(input, ResumePreparationStage.Printing, current.Number, ct);
            return current with { Pages = 1, PdfPath = "synthetic.pdf", PdfHash = "synthetic-hash", PageTexts = ["synthetic PDF text"] };
        }
        public async Task<ResumeRound> ReviewAsync(ResumePreparationInput input, ResumeRound current, CancellationToken ct)
        {
            await CalledAsync(input, ResumePreparationStage.Reviewing, current.Number, ct);
            return current with { Review = (Review?.Invoke(current.Number) ?? Checklist()).Scored() };
        }
        public async Task<TailoredResume> FinishAsync(ResumePreparationInput input, ResumeRound best, CancellationToken ct)
        {
            await CalledAsync(input, ResumePreparationStage.Finalizing, best.Number, ct);
            return new TailoredResume { VacancyId = input.Vacancy.Id, Language = best.Document.Language, Model = best.Model,
                PdfPath = best.PdfPath!, FileName = "Synthetic_Candidate_CV.pdf", CreatedAt = DateTimeOffset.UtcNow,
                ContentJson = JsonSerializer.Serialize(best.Document, ResumeJsonContext.Default.ResumeDocument),
                ReportJson = JsonSerializer.Serialize(new ResumeReport { Ready = best.Ready, Review = best.Review, Pages = best.Pages,
                    InputHash = input.Recipe.Hash, Recipe = input.Recipe, PdfHash = best.PdfHash! }, ResumeJsonContext.Default.ResumeReport) };
        }
    }

    private sealed class FailSave(ResumePreparationStage at) : SaveChangesInterceptor
    {
        private int _armed = 1;
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData, InterceptionResult<int> result, CancellationToken ct = default)
        {
            if (eventData.Context!.ChangeTracker.Entries<ResumePreparationRequest>().Any(entry => entry.State == EntityState.Modified && entry.Entity.Stage == at)
                && Interlocked.Exchange(ref _armed, 0) == 1) throw new InvalidOperationException("isolated checkpoint commit failure");
            return ValueTask.FromResult(result);
        }
    }

    private sealed class Factory(DbContextOptions<JobFinderDbContext> options) : IDbContextFactory<JobFinderDbContext>
    {
        public JobFinderDbContext CreateDbContext() => new(options);
    }
}
