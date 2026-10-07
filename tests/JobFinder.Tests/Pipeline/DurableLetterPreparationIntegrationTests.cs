using System.Data.Common;
using System.Text.Json;
using JobFinder.Core.Domain;
using JobFinder.Core.Messaging;
using JobFinder.Core.Profile;
using JobFinder.Core.Text;
using JobFinder.Data;
using JobFinder.Llm;
using JobFinder.Llm.Client;
using JobFinder.Llm.Tasks;
using JobFinder.Pipeline;
using JobFinder.Pipeline.Career;
using JobFinder.Pipeline.Messaging;
using JobFinder.Pipeline.Modules.Candidate;
using JobFinder.Pipeline.Modules.Discovery;
using JobFinder.Pipeline.Modules.Hiring;
using JobFinder.Pipeline.Profile;
using JobFinder.Pipeline.Settings;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Time.Testing;
using Wolverine;

namespace JobFinder.Tests.Pipeline;

public sealed partial class DurableLetterPreparationIntegrationTests(PostgresFixture pg) : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(45);
    private readonly List<IHost> _hosts = [];
    private readonly List<Hold> _holds = [];
    private readonly CandidateProfile _profile = new() { Name = "Synthetic Candidate", Title = "Senior .NET Developer", Summary = "Developed synthetic C# services.", Reviewed = true };
    private readonly CandidateFacts _facts = new() { Facts = [new CandidateFact { Key = "notice", Question = "Notice?", Answer = "Two weeks.", Confirmed = true }] };

    public async ValueTask InitializeAsync()
    {
        Assert.SkipWhen(pg.SkipReason is not null, pg.SkipReason ?? "");
        await using (var context = await pg.Db.CreateDbContextAsync(Ct))
        {
            await context.Database.MigrateAsync(Ct);
            await context.DeliveryAttempts.ExecuteDeleteAsync(Ct);
            await context.EmployerMessages.ExecuteDeleteAsync(Ct);
            await context.LetterPreparationRequests.ExecuteDeleteAsync(Ct);
            await context.VacancyAssessmentRequests.ExecuteDeleteAsync(Ct);
            await context.Applications.ExecuteDeleteAsync(Ct);
            await context.Vacancies.ExecuteDeleteAsync(Ct);
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
        }
        using var bootstrap = await BuildAsync(false, new Steps());
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

    private async Task<IHost> BuildAsync(bool consumer, Steps steps, IInterceptor? interceptor = null, IDeliveryTransport? recipient = null,
        ILetterPreparationSteps? durableSteps = null, bool fastRecovery = false)
    {
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { DisableDefaults = true });
        var config = new Dictionary<string, string?>
        {
            ["Database:ConnectionString"] = context.Database.GetConnectionString(), ["Paths:Prompts"] = Fixture.PromptsDir,
            ["Paths:Data"] = Path.Combine(OperatingSystem.IsWindows() ? @"E:\Temp" : Path.GetTempPath(), "jobfinder-letter-synthetic"),
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
        builder.Services.RemoveAll<IDbContextFactory<JobFinderDbContext>>();
        if (interceptor is null) builder.Services.AddSingleton(pg.Db);
        else
        {
            var options = new DbContextOptionsBuilder<JobFinderDbContext>();
            JobFinderDbContext.Configure(options, context.Database.GetConnectionString()!);
            options.AddInterceptors(interceptor);
            builder.Services.AddSingleton<IDbContextFactory<JobFinderDbContext>>(new Factory(options.Options));
        }
        builder.Services.RemoveAll<ILetterPreparationSteps>();
        builder.Services.AddSingleton<ILetterPreparationSteps>(durableSteps ?? steps);
        builder.Services.RemoveAll<IVacancyAssessmentSteps>();
        builder.Services.AddSingleton<IVacancyAssessmentSteps>(sp => new AssessmentSteps(sp.GetRequiredService<JobFinderLlm>(), sp.GetRequiredService<LetterWriter>()));
        builder.Services.RemoveAll<IHttpClientFactory>();
        builder.Services.AddSingleton<IHttpClientFactory>(new NoNetwork());
        if (recipient is not null)
        {
            builder.Services.RemoveAll<IDeliveryTransport>();
            builder.Services.AddSingleton(recipient);
        }
        builder.AddJobFinderMessaging(consumer);
        if (fastRecovery) builder.Services.ConfigureWolverine(JobFinder.MessagingProbe.ResumeProbe.FastRecovery);
        if (consumer) builder.Services.AddJobFinderInitialization();
        return builder.Build();
    }

    private async Task<IHost> HostAsync(bool consumer, Steps steps, IInterceptor? interceptor = null, IDeliveryTransport? recipient = null,
        ILetterPreparationSteps? durableSteps = null, bool fastRecovery = false)
    {
        var host = await BuildAsync(consumer, steps, interceptor, recipient, durableSteps, fastRecovery);
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

    private async Task<Vacancy> SeedAsync()
    {
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        var vacancy = new Vacancy
        {
            Title = "Synthetic .NET Developer", Description = "Synthetic C# services.", CompanyName = "Synthetic Company",
            Url = "https://example.invalid/vacancy/" + Guid.NewGuid().ToString("N"), Fingerprint = Guid.NewGuid().ToString("N"),
            Status = VacancyStatus.New, UpdatedAt = DateTimeOffset.UtcNow, FirstSeenAt = DateTimeOffset.UtcNow,
        };
        context.Vacancies.Add(vacancy);
        await context.SaveChangesAsync(Ct);
        return await context.Vacancies.AsNoTracking().SingleAsync(row => row.Id == vacancy.Id, Ct);
    }

    private async Task<LetterPreparationTicket> RequestAsync(IHost host, Vacancy vacancy, bool force = false, LetterBrief? brief = null)
    {
        var profile = (await host.Services.GetRequiredService<ProfileProvider>().GetSnapshotAsync(Ct))!.Value;
        var facts = (await host.Services.GetRequiredService<FactsProvider>().GetSnapshotAsync(Ct)).Value;
        var operation = await host.Services.GetRequiredService<JobFinderLlm>().ForOperationAsync(Ct);
        return await host.Services.GetRequiredService<LetterPreparationRequests>().RequestAsync(brief ?? Brief(vacancy), profile, facts, operation, Ct, force);
    }

    private static LetterBrief Brief(Vacancy vacancy) => new(vacancy, LetterChannel.Telegram, "Synthetic Recruiter", "First application.", true, "(no conversation)", true, true, true);

    private async Task<LetterPreparationRequest> RowAsync(Guid id)
    {
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        return await context.LetterPreparationRequests.AsNoTracking().SingleAsync(row => row.Id == id, Ct);
    }

    private async Task UntilAsync(Func<Task<bool>> condition)
    {
        var deadline = DateTimeOffset.UtcNow + Timeout;
        while (!await condition())
        {
            Assert.True(DateTimeOffset.UtcNow < deadline, "native letter condition timed out");
            await Task.Delay(100, Ct);
        }
    }

    private async Task<LetterCandidate> CompletedAsync(Guid id)
    {
        await UntilAsync(async () => (await RowAsync(id)).Stage == LetterPreparationStage.Completed);
        return LetterPreparationRequests.ReadCandidate((await RowAsync(id)).ResultJson);
    }

    [Fact]
    public async Task Producer_exit_duplicate_requests_and_warm_cache_do_not_repeat_paid_work()
    {
        var vacancy = await SeedAsync();
        var steps = new Steps();
        var producer = await HostAsync(false, steps);
        var tickets = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => RequestAsync(producer, vacancy)));
        Assert.Single(tickets.Select(ticket => ticket.RequestId).Distinct());
        Assert.Equal(0, steps.Drafts);
        await StopAsync(producer);
        var consumer = await HostAsync(true, steps);
        var best = await CompletedAsync(tickets[0].RequestId);
        Assert.Equal(10, best.Score);
        Assert.Empty(best.Problems);
        var calls = steps.Drafts + steps.Reviews;
        var warm = await RequestAsync(consumer, vacancy);
        Assert.Equal(tickets[0].RequestId, warm.RequestId);
        Assert.NotNull(warm.Result);
        Assert.Equal(calls, steps.Drafts + steps.Reviews);
        var forced = await RequestAsync(consumer, vacancy, force: true);
        Assert.NotEqual(warm.RequestId, forced.RequestId);
        await CompletedAsync(forced.RequestId);
        Assert.True(steps.Drafts + steps.Reviews > calls);
    }

    [Fact]
    public async Task Detached_score_preview_has_no_vacancy_foreign_key_and_still_fences_candidate()
    {
        var host = await HostAsync(true, new Steps());
        var preview = new Vacancy { Title = "Synthetic preview", Description = "C#", Url = "https://example.invalid/preview", Fingerprint = "synthetic-preview" };
        var ticket = await RequestAsync(host, preview);
        await CompletedAsync(ticket.RequestId);
        Assert.Null((await RowAsync(ticket.RequestId)).VacancyId);
    }

    [Fact]
    public async Task Replayed_assessment_child_capture_across_midnight_reuses_the_original_frozen_date()
    {
        var host = await HostAsync(false, new Steps());
        var vacancy = await SeedAsync();
        var profile = (await host.Services.GetRequiredService<ProfileProvider>().GetSnapshotAsync(Ct))!.Value;
        var facts = (await host.Services.GetRequiredService<FactsProvider>().GetSnapshotAsync(Ct)).Value;
        var operation = await host.Services.GetRequiredService<JobFinderLlm>().ForOperationAsync(Ct);
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 9, 30, 23, 59, 0, TimeSpan.Zero));
        var requests = new LetterPreparationRequests(pg.Db, host.Services.GetRequiredService<IServiceScopeFactory>(), time);
        var anchor = AssessmentVacancy.From(vacancy);
        var first = await requests.RequestAsync(Brief(vacancy), profile, facts, operation, Ct, anchor: anchor);
        time.Advance(TimeSpan.FromMinutes(2));
        var replay = await requests.RequestAsync(Brief(vacancy), profile, facts, operation, Ct, anchor: anchor);
        Assert.Equal(first.RequestId, replay.RequestId);
        var input = LetterPreparationRequests.ReadInput(await RowAsync(first.RequestId));
        Assert.Equal(30, input.CapturedAt.Day);
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        Assert.Equal(1, await context.LetterPreparationRequests.CountAsync(Ct));
        Assert.Equal(2, await context.LetterPreparationLanes.CountAsync(Ct));
    }

    [Fact]
    public async Task Changed_conversation_misses_warm_cache_even_when_the_first_message_brief_is_unchanged()
    {
        var vacancy = await SeedAsync();
        var host = await HostAsync(true, new Steps());
        var initial = await RequestAsync(host, vacancy);
        await CompletedAsync(initial.RequestId);
        await using (var context = await pg.Db.CreateDbContextAsync(Ct))
        {
            context.Applications.Add(new Application { VacancyId = vacancy.Id, Status = ApplicationStatus.Sent, Channel = ApplicationChannel.TelegramDm, CoverLetter = "Synthetic conversation changed.", CreatedAt = DateTimeOffset.UtcNow });
            await context.SaveChangesAsync(Ct);
        }
        var replacement = await RequestAsync(host, vacancy);
        Assert.NotEqual(initial.RequestId, replacement.RequestId);
        Assert.Null(replacement.Result);
        await CompletedAsync(replacement.RequestId);
    }

    [Fact]
    public async Task Force_supersedes_an_active_generation_and_late_old_results_do_not_win()
    {
        var hold = Barrier();
        var steps = new Steps { DraftHold = hold };
        var host = await HostAsync(true, steps);
        var vacancy = await SeedAsync();
        var initial = await RequestAsync(host, vacancy);
        await UntilAsync(() => Task.FromResult(steps.Drafts == 2));
        var replacement = await RequestAsync(host, vacancy, force: true);
        Assert.Equal(LetterPreparationStage.Cancelled, (await RowAsync(initial.RequestId)).Stage);
        hold.Release.TrySetResult();
        await CompletedAsync(replacement.RequestId);
        Assert.Null((await RowAsync(initial.RequestId)).ResultJson);
    }

    [Fact]
    public async Task Lost_ownership_cannot_checkpoint_a_paid_result_or_start_another_paid_call()
    {
        var hold = Barrier();
        var steps = new Steps { DraftHold = hold };
        var host = await HostAsync(true, steps);
        var ticket = await RequestAsync(host, await SeedAsync());
        await UntilAsync(() => Task.FromResult(steps.Drafts == 2));
        host.Services.GetRequiredService<ServerOwnership>().Lost();
        hold.Release.TrySetResult();
        await Task.Delay(200, Ct);
        Assert.Equal(LetterPreparationStage.Active, (await RowAsync(ticket.RequestId)).Stage);
        Assert.Null((await RowAsync(ticket.RequestId)).ResultJson);
        await Assert.ThrowsAsync<InvalidOperationException>(() => host.Services.GetRequiredService<LetterPreparationWorkflow>().ChooseAsync(ticket.RequestId, 1, 1, Ct));
        Assert.Equal(2, steps.Drafts);
        Assert.Equal(0, steps.Reviews);
    }

    [Fact]
    public async Task Review_unavailability_and_restart_resume_saved_drafts_only()
    {
        var vacancy = await SeedAsync();
        var steps = new Steps { ReviewUnavailable = true };
        var host = await HostAsync(true, steps);
        var ticket = await RequestAsync(host, vacancy);
        await UntilAsync(async () =>
        {
            await using var context = await pg.Db.CreateDbContextAsync(Ct);
            return await context.LetterPreparationLanes.CountAsync(row => row.RequestId == ticket.RequestId && row.RetryAt != null, Ct) == 2;
        });
        Assert.Equal(2, steps.Drafts);
        await StopAsync(host);
        await using (var context = await pg.Db.CreateDbContextAsync(Ct))
        {
            await context.LetterPreparationLanes.Where(row => row.RequestId == ticket.RequestId).ExecuteUpdateAsync(update => update.SetProperty(row => row.RetryAt, (DateTimeOffset?)null), Ct);
            Assert.All(await context.LetterPreparationLanes.ToListAsync(Ct), lane => { Assert.Equal(LetterLaneStage.Reviewing, lane.Stage); Assert.NotNull(lane.DraftJson); Assert.Equal(0, lane.Failures); });
        }
        steps.ReviewUnavailable = false;
        var restarted = await HostAsync(true, steps);
        await using (var context = await pg.Db.CreateDbContextAsync(Ct))
            foreach (var lane in await context.LetterPreparationLanes.AsNoTracking().Where(row => row.RequestId == ticket.RequestId).ToListAsync(Ct))
                await restarted.Services.GetRequiredService<IMessageBus>().PublishAsync(new ReviewLetterLane { RequestId = lane.RequestId, LaneId = lane.Id, Round = lane.Round });
        await CompletedAsync(ticket.RequestId);
        Assert.Equal(2, steps.Drafts);
        Assert.Equal(1, (await RowAsync(ticket.RequestId)).Round);
    }

    [Fact]
    public async Task Different_duplicate_envelopes_cannot_claim_a_running_lane()
    {
        var vacancy = await SeedAsync();
        var hold = Barrier();
        var steps = new Steps { DraftHold = hold };
        var host = await HostAsync(true, steps);
        var ticket = await RequestAsync(host, vacancy);
        await UntilAsync(() => Task.FromResult(steps.Drafts == 2));
        await using (var context = await pg.Db.CreateDbContextAsync(Ct))
        {
            var lanes = await context.LetterPreparationLanes.AsNoTracking().ToListAsync(Ct);
            foreach (var lane in lanes)
                await host.Services.GetRequiredService<LetterPreparationWorkflow>().RunAsync(ticket.RequestId, lane.Id, 1, 1, LetterLaneStage.Drafting, Guid.NewGuid(), Ct);
        }
        Assert.Equal(2, steps.Drafts);
        hold.Release.TrySetResult();
        await CompletedAsync(ticket.RequestId);
    }

    [Fact]
    public async Task Perfect_review_does_not_wait_for_a_slow_losing_lane()
    {
        var vacancy = await SeedAsync();
        var hold = Barrier();
        var steps = new Steps { SlowSecondDraft = hold };
        var host = await HostAsync(true, steps);
        var ticket = await RequestAsync(host, vacancy);
        var result = await CompletedAsync(ticket.RequestId);
        Assert.Equal(10, result.Score);
        await UntilAsync(() => Task.FromResult(steps.CancelledDrafts == 1));
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        Assert.Contains(await context.LetterPreparationLanes.ToListAsync(Ct), lane => lane.Stage == LetterLaneStage.Abandoned);
    }

    [Fact]
    public async Task Near_pass_has_one_fixed_native_grace_timer_and_finishes_after_twenty_seconds()
    {
        var vacancy = await SeedAsync();
        var hold = Barrier();
        var steps = new Steps { Score = 9, SlowSecondDraft = hold };
        var host = await HostAsync(true, steps);
        var ticket = await RequestAsync(host, vacancy);
        await UntilAsync(async () => (await RowAsync(ticket.RequestId)).GraceUntil is not null);
        var grace = (await RowAsync(ticket.RequestId)).GraceUntil!.Value;
        await host.Services.GetRequiredService<LetterPreparationWorkflow>().ChooseAsync(ticket.RequestId, 1, 1, Ct);
        Assert.Equal(grace, (await RowAsync(ticket.RequestId)).GraceUntil);
        var result = await CompletedAsync(ticket.RequestId);
        Assert.Equal(9, result.Score);
        Assert.True((await RowAsync(ticket.RequestId)).CompletedAt >= grace);
        Assert.Equal(1, steps.Reviews);
    }

    [Fact]
    public async Task Two_finished_near_passes_need_not_wait_for_the_grace_timer()
    {
        var host = await HostAsync(true, new Steps { Score = 9 });
        var ticket = await RequestAsync(host, await SeedAsync());
        await CompletedAsync(ticket.RequestId);
        var row = await RowAsync(ticket.RequestId);
        Assert.True(row.GraceUntil is null || row.CompletedAt < row.GraceUntil);
    }

    [Fact]
    public async Task Four_weak_rounds_keep_best_text_and_remarks_and_never_become_ready()
    {
        var steps = new Steps { Score = 7 };
        var host = await HostAsync(true, steps);
        var ticket = await RequestAsync(host, await SeedAsync());
        var result = await CompletedAsync(ticket.RequestId);
        Assert.False(LetterPreparationWorkflow.Ready(result));
        Assert.NotEmpty(result.Problems);
        Assert.Equal(8, steps.Drafts);
        Assert.Equal(8, steps.Reviews);
        Assert.Equal(4, (await RowAsync(ticket.RequestId)).Round);
        Assert.Equal(6, steps.Revisions);
    }

    [Theory]
    [InlineData("profile")]
    [InlineData("facts")]
    [InlineData("vacancy")]
    [InlineData("conversation")]
    public async Task Changes_during_paid_work_retire_without_publishing_stale_results(string change)
    {
        var vacancy = await SeedAsync();
        var hold = Barrier();
        var steps = new Steps { DraftHold = hold };
        var host = await HostAsync(true, steps);
        var ticket = await RequestAsync(host, vacancy);
        await UntilAsync(() => Task.FromResult(steps.Drafts == 2));
        var publisher = host.Services.GetRequiredService<ICandidatePublisher>();
        if (change == "profile")
        {
            var snapshot = (await host.Services.GetRequiredService<ProfileProvider>().GetSnapshotAsync(Ct))!;
            snapshot.Value.Summary += " changed";
            await publisher.PublishProfileAsync(snapshot.Value, snapshot.Version, "synthetic-test", Ct);
        }
        else if (change == "facts")
        {
            var snapshot = await host.Services.GetRequiredService<FactsProvider>().GetSnapshotAsync(Ct);
            snapshot.Value.Facts[0].Answer += " changed";
            await publisher.PublishFactsAsync(snapshot.Value, snapshot.Version, "synthetic-test", Ct);
        }
        else
        {
            await using var context = await pg.Db.CreateDbContextAsync(Ct);
            if (change == "vacancy") await context.Vacancies.Where(row => row.Id == vacancy.Id).ExecuteUpdateAsync(update => update.SetProperty(row => row.Description, "Changed C# services."), Ct);
            else
            {
                context.Applications.Add(new Application { VacancyId = vacancy.Id, Status = ApplicationStatus.Sent, Channel = ApplicationChannel.TelegramDm, CoverLetter = "Synthetic previous message.", CreatedAt = DateTimeOffset.UtcNow });
                await context.SaveChangesAsync(Ct);
            }
        }
        hold.Release.TrySetResult();
        await UntilAsync(async () => (await RowAsync(ticket.RequestId)).Stage == LetterPreparationStage.Cancelled);
        Assert.Equal(0, steps.Reviews);
        Assert.Null((await RowAsync(ticket.RequestId)).ResultJson);
        await using var read = await pg.Db.CreateDbContextAsync(Ct);
        Assert.Single(await read.Notifications.Where(row => row.EntityId == vacancy.Id && row.Text.Contains("letter source or candidate superseded")).ToListAsync(Ct));
    }

    [Fact]
    public async Task Cancelled_waiter_does_not_cancel_the_saved_operation()
    {
        var vacancy = await SeedAsync();
        var hold = Barrier();
        var host = await HostAsync(true, new Steps { DraftHold = hold });
        using var waiting = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var profile = (await host.Services.GetRequiredService<ProfileProvider>().GetSnapshotAsync(Ct))!.Value;
        var facts = (await host.Services.GetRequiredService<FactsProvider>().GetSnapshotAsync(Ct)).Value;
        var operation = await host.Services.GetRequiredService<JobFinderLlm>().ForOperationAsync(Ct);
        var task = host.Services.GetRequiredService<LetterPreparationRequests>().WriteAsync(Brief(vacancy), profile, facts, operation, waiting.Token);
        await hold.Entered.Task.WaitAsync(Timeout, Ct);
        waiting.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await task);
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        var id = await context.LetterPreparationRequests.Select(row => row.Id).SingleAsync(Ct);
        hold.Release.TrySetResult();
        await CompletedAsync(id);
    }

    [Theory]
    [InlineData("draft")]
    [InlineData("review")]
    public async Task One_shot_checkpoint_failure_reuses_the_paid_response(string stage)
    {
        var fault = new FailCommit(stage);
        var steps = new Steps { Score = 9 };
        var host = await HostAsync(true, steps, fault);
        var ticket = await RequestAsync(host, await SeedAsync());
        await CompletedAsync(ticket.RequestId);
        Assert.True(fault.Fired);
        Assert.Equal(2, steps.Drafts);
        Assert.Equal(2, steps.Reviews);
    }

    [Fact]
    public async Task Corrupt_snapshot_is_quarantined_and_does_not_block_a_healthy_request()
    {
        var steps = new Steps();
        var producer = await HostAsync(false, steps);
        var vacancy = await SeedAsync();
        var ticket = await RequestAsync(producer, vacancy);
        await using (var context = await pg.Db.CreateDbContextAsync(Ct))
            await context.LetterPreparationRequests.Where(row => row.Id == ticket.RequestId).ExecuteUpdateAsync(update => update.SetProperty(row => row.InputHash, "corrupt"), Ct);
        await StopAsync(producer);
        var consumer = await HostAsync(true, steps);
        await UntilAsync(async () => (await RowAsync(ticket.RequestId)).Stage == LetterPreparationStage.Failed);
        Assert.Equal(0, steps.Drafts);
        var healthy = await RequestAsync(consumer, await SeedAsync());
        await CompletedAsync(healthy.RequestId);
        await using var read = await pg.Db.CreateDbContextAsync(Ct);
        Assert.True(await read.Database.SqlQuery<int>($"SELECT count(*)::int AS \"Value\" FROM jobfinder_messages.wolverine_dead_letters").SingleAsync(Ct) >= 1);
    }

    [Fact]
    public async Task Unsupported_commands_and_late_completed_commands_do_no_paid_work()
    {
        var steps = new Steps();
        var host = await HostAsync(true, steps);
        var ticket = await RequestAsync(host, await SeedAsync());
        await CompletedAsync(ticket.RequestId);
        var calls = steps.Drafts + steps.Reviews;
        var workflow = host.Services.GetRequiredService<LetterPreparationWorkflow>();
        await Assert.ThrowsAsync<UnsupportedLetterPreparationException>(() => workflow.ChooseAsync(ticket.RequestId, 1, 2, Ct));
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        foreach (var lane in await context.LetterPreparationLanes.AsNoTracking().ToListAsync(Ct))
            await workflow.RunAsync(ticket.RequestId, lane.Id, 1, 1, LetterLaneStage.Drafting, Guid.NewGuid(), Ct);
        Assert.Equal(calls, steps.Drafts + steps.Reviews);
    }

    [Fact]
    public async Task Guarded_downgrade_keeps_saved_request_and_native_envelopes_intact()
    {
        var host = await HostAsync(false, new Steps());
        var ticket = await RequestAsync(host, await SeedAsync());
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        try
        {
            await Assert.ThrowsAsync<Npgsql.PostgresException>(() => MigrationTestRunner.DowngradeAndRestoreAsync(context, "20260930170205_DurableVacancyAssessment", Ct));
            Assert.Equal(LetterPreparationStage.Active, (await RowAsync(ticket.RequestId)).Stage);
            Assert.Equal(2, await context.LetterPreparationLanes.CountAsync(row => row.RequestId == ticket.RequestId, Ct));
            Assert.Contains("20260930182958_DurableLetterPreparation", await context.Database.GetAppliedMigrationsAsync(Ct));
        }
        finally { await context.Database.MigrateAsync(CancellationToken.None); }
        Assert.Empty(await context.Database.GetPendingMigrationsAsync(Ct));
    }

    [Fact]
    public async Task Assessment_persists_child_dependency_and_frees_its_handler_while_letters_are_held()
    {
        var hold = Barrier();
        var host = await HostAsync(true, new Steps { DraftHold = hold });
        var first = await SeedAsync();
        var second = await SeedAsync();
        var requests = host.Services.GetRequiredService<VacancyAssessments>();
        var ids = new[] { (await requests.RequestAsync(first.Id, Ct))!.Value, (await requests.RequestAsync(second.Id, Ct))!.Value };
        await UntilAsync(async () =>
        {
            await using var context = await pg.Db.CreateDbContextAsync(Ct);
            return await context.VacancyAssessmentRequests.CountAsync(row => ids.Contains(row.Id) && row.Stage == VacancyAssessmentStage.Lettering && row.LetterJson != null, Ct) == 2;
        });
        await using (var context = await pg.Db.CreateDbContextAsync(Ct))
        {
            Assert.Equal(2, await context.LetterPreparationRequests.CountAsync(Ct));
            var parents = await context.VacancyAssessmentRequests.AsNoTracking().ToListAsync(Ct);
            Assert.All(parents, row => Assert.NotNull(JsonSerializer.Deserialize(row.LetterJson!, VacancyAssessmentJsonContext.Default.AssessmentLetter)!.PreparationId));
        }
        hold.Release.TrySetResult();
        await UntilAsync(async () =>
        {
            await using var context = await pg.Db.CreateDbContextAsync(Ct);
            return await context.VacancyAssessmentRequests.CountAsync(row => ids.Contains(row.Id) && row.Stage == VacancyAssessmentStage.Completed, Ct) == 2;
        });
        await using var read = await pg.Db.CreateDbContextAsync(Ct);
        Assert.Equal(2, await read.LetterPreparationRequests.CountAsync(Ct));
        Assert.All(await read.Vacancies.AsNoTracking().ToListAsync(Ct), vacancy => { Assert.Equal(VacancyStatus.Scored, vacancy.Status); Assert.Equal(10, vacancy.LetterReview!.Score); });
    }

    private sealed class Factory(DbContextOptions<JobFinderDbContext> options) : IDbContextFactory<JobFinderDbContext>
    {
        public JobFinderDbContext CreateDbContext() => new(options);
    }

    private sealed class NoNetwork : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => throw new InvalidOperationException("synthetic workflow must not call a real network");
    }

    private sealed class Steps : ILetterPreparationSteps
    {
        public int Drafts;
        public int Reviews;
        public int Revisions;
        public int CancelledDrafts;
        public int Score { get; set; } = 10;
        public bool ReviewUnavailable { get; set; }
        public Hold? DraftHold { get; set; }
        public Hold? SlowSecondDraft { get; set; }
        public Hold? ReviewHold { get; set; }
        public void EnsureCompatible(LetterPreparationInput input) { }

        public async Task<LetterCandidate> DraftAsync(LetterPreparationInput input, LetterCandidate? best, CancellationToken ct)
        {
            var number = Interlocked.Increment(ref Drafts);
            if (best is not null) Interlocked.Increment(ref Revisions);
            try
            {
                if (DraftHold is not null) await DraftHold.WaitAsync(ct);
                if (number == 2 && SlowSecondDraft is not null) await SlowSecondDraft.WaitAsync(ct);
            }
            catch (OperationCanceledException) { Interlocked.Increment(ref CancelledDrafts); throw; }
            return new LetterCandidate { Text = "Synthetic letter " + number, Written = "Synthetic letter " + number };
        }

        public async Task<LetterCandidate> ReviewAsync(LetterPreparationInput input, LetterCandidate draft, CancellationToken ct)
        {
            Interlocked.Increment(ref Reviews);
            if (ReviewUnavailable) throw new LlmUnavailableException("synthetic reviewer unavailable");
            if (ReviewHold is not null) await ReviewHold.WaitAsync(ct);
            var problems = Score >= 9 ? new List<string>() : ["synthetic weak message"];
            return new LetterCandidate
            {
                Text = draft.Text, Written = draft.Written, Problems = problems, Score = Score,
                Review = new ApplicationLetterReview { TextHash = Fingerprint.Of(draft.Text), ContextHash = input.ExactText is null ? input.Recipe.Hash : input.ReviewContextHash, Recipe = input.Recipe, Score = Score, Problems = problems, CheckedAt = DateTimeOffset.UtcNow },
            };
        }
    }

    private sealed class AssessmentSteps(JobFinderLlm llm, LetterWriter writer) : IVacancyAssessmentSteps
    {
        public void EnsureCompatible(VacancyAssessmentInput input) { }
        public Task<AssessmentScore> ScoreAsync(VacancyAssessmentInput input, CancellationToken ct) => Task.FromResult(new AssessmentScore
        {
            Score = 80, Status = VacancyStatus.Scored, Decision = Decision.Approval, EnglishRequired = EnglishLevel.B1,
            Evaluation = new VacancyEvaluation { Score = 80, Summary = "Synthetic matching services.", Keywords = ["C#"], Model = "synthetic" },
        });
        public Task<AssessmentLetter> LetterAsync(VacancyAssessmentInput input, AssessmentScore score, CancellationToken ct) =>
            new VacancyAssessmentSteps(llm, writer).LetterAsync(input, score, ct);
    }

    private sealed class Hold
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task WaitAsync(CancellationToken ct) { Entered.TrySetResult(); await Release.Task.WaitAsync(Timeout, ct); }
    }

    private sealed class FailCommit(string stage) : DbTransactionInterceptor
    {
        private int _armed = 1;
        public bool Fired { get; private set; }
        public override ValueTask<InterceptionResult> TransactionCommittingAsync(DbTransaction transaction, TransactionEventData eventData,
            InterceptionResult result, CancellationToken ct = default)
        {
            if (eventData.Context?.ChangeTracker.Entries<LetterPreparationLane>().Any(entry => stage == "draft"
                    ? entry.Entity.Stage == LetterLaneStage.Reviewing && entry.Entity.DraftJson is not null
                    : entry.Entity.ResultJson is not null) == true && Interlocked.Exchange(ref _armed, 0) == 1)
            {
                Fired = true;
                throw new IOException("synthetic letter checkpoint fault");
            }
            return ValueTask.FromResult(result);
        }
    }
}
