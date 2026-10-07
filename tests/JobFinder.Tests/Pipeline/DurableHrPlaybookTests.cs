using System.Text.Json;
using JobFinder.Core.Domain;
using JobFinder.Core.Profile;
using JobFinder.Core.Text;
using JobFinder.Data;
using JobFinder.Llm.Client;
using JobFinder.Llm.Tasks;
using JobFinder.Pipeline;
using JobFinder.Pipeline.Career;
using JobFinder.Pipeline.Messaging;
using JobFinder.Pipeline.Modules.Candidate;
using JobFinder.Pipeline.Modules.Preparation;
using JobFinder.Pipeline.Notifications;
using JobFinder.Pipeline.Settings;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace JobFinder.Tests.Pipeline;

public sealed class DurableHrPlaybookTests(PostgresFixture pg) : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private readonly List<IHost> _hosts = [];

    public async ValueTask InitializeAsync()
    {
        Assert.SkipWhen(pg.SkipReason is not null, pg.SkipReason ?? "");
        await pg.SeedPoliciesAsync();
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        await context.HrPlaybookRequests.ExecuteDeleteAsync(Ct);
        await context.Interviews.ExecuteDeleteAsync(Ct);
        await context.Applications.ExecuteDeleteAsync(Ct);
        await context.StoryRevisions.ExecuteDeleteAsync(Ct);
        await context.Stories.IgnoreQueryFilters().ExecuteDeleteAsync(Ct);
        await context.Documents.Where(row => row.Key.StartsWith("candidate.")).ExecuteDeleteAsync(Ct);
        await context.CandidateRevisions.ExecuteDeleteAsync(Ct);
        await context.Settings.Where(row => row.Key.StartsWith("hr.playbook")).ExecuteDeleteAsync(Ct);
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
            services.RemoveAll<IHrPlaybookPreparationSteps>();
            services.AddSingleton<IHrPlaybookPreparationSteps>(steps);
            if (!consumer) services.AddHostedService<SingleInstanceGuard>();
        });
        _hosts.Add(host);
        await host.StartAsync(Ct);
        if (seed)
        {
            var publisher = host.Services.GetRequiredService<ICandidatePublisher>();
            await publisher.PublishProfileAsync(new CandidateProfile { Name = "Synthetic Candidate", Reviewed = true, Summary = "Supported evidence" }, 0, "synthetic", Ct);
            await publisher.PublishFactsAsync(new CandidateFacts(), 0, "synthetic", Ct);
            await using var context = await pg.Db.CreateDbContextAsync(Ct);
            await NotificationAdmissionAssertions.ResetAsync(context, Ct);
        }
        return host;
    }

    private async Task<HrPlaybookRequest> RootAsync(Guid id)
    {
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        return await context.HrPlaybookRequests.AsNoTracking().SingleAsync(row => row.Id == id, Ct);
    }
    private static HrBank Bank(IHost host) => host.Services.GetRequiredService<HrPlaybookService>().Bank();
    private static Task<Guid?> RequestAsync(IHost host, bool force = true) => host.Services.GetRequiredService<HrPlaybookSources>().RequestAsync(Bank(host), Ct, force);
    private static Task BuildAsync(IHost host, Guid id) => host.Services.GetRequiredService<HrPlaybookWorkflow>().BuildAsync(new() { RequestId = id }, Guid.NewGuid(), Ct);
    private static Task PublishAsync(IHost host, Guid id) => host.Services.GetRequiredService<HrPlaybookWorkflow>().PublishAsync(new() { RequestId = id }, Guid.NewGuid(), Ct);

    [Fact]
    public async Task A_paid_hr_cache_checkpoint_failure_is_quarantined_and_never_automatically_redrafted()
    {
        var steps = new Steps { CheckpointFault = true };
        var host = await HostAsync(steps);
        var id = (await RequestAsync(host))!.Value;
        await Assert.ThrowsAsync<HrPlaybookCheckpointException>(() => BuildAsync(host, id));
        var failed = await RootAsync(id);
        Assert.Equal(HrPlaybookStage.Failed, failed.Stage);
        Assert.NotNull(failed.ResultJson);
        Assert.Equal(CanonicalJson.Hash(failed.ResultJson), failed.ResultHash);
        await BuildAsync(host, id);
        Assert.Null(await RequestAsync(host, force: false));
        Assert.Equal(1, steps.Calls);
        Assert.False(await host.Services.GetRequiredService<HrPlaybookService>().RequestedAsync(Ct));
        Assert.Null(await host.Services.GetRequiredService<HrPlaybookService>().LatestAsync(Ct));
    }

    [Fact]
    public async Task A_missing_hr_profile_is_not_reported_as_an_admitted_request()
    {
        var host = await HostAsync(new Steps(), seed: false);
        Assert.Null(await RequestAsync(host, force: false));
        await Assert.ThrowsAsync<InvalidOperationException>(() => host.Services.GetRequiredService<HrPlaybookService>().RequestAsync(Ct));
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        Assert.Empty(await context.HrPlaybookRequests.ToListAsync(Ct));
        Assert.Empty(await context.Settings.Where(row => row.Key.StartsWith("hr.playbook")).ToListAsync(Ct));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Hr_downgrade_refuses_orphaned_current_or_future_native_envelopes(bool scheduled)
    {
        var host = await HostAsync(new Steps());
        var id = (await RequestAsync(host))!.Value;
        if (scheduled)
        {
            await using var scope = host.Services.CreateAsyncScope();
            var outbox = scope.ServiceProvider.GetRequiredService<Wolverine.EntityFrameworkCore.IDbContextOutbox>();
            await using var publication = await pg.Db.CreateDbContextAsync(Ct);
            await using var transaction = await publication.Database.BeginTransactionAsync(Ct);
            outbox.Enroll(publication);
            await HrPlaybookRequests.PublishAsync(outbox, await RootAsync(id), DateTimeOffset.UtcNow.AddDays(1));
            await outbox.SaveChangesAndFlushMessagesAsync(Ct);
        }
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        await context.HrPlaybookRequests.ExecuteDeleteAsync(Ct);
        await context.Settings.Where(row => row.Key.StartsWith("hr.playbook")).ExecuteDeleteAsync(Ct);
        if (scheduled) await context.Database.ExecuteSqlRawAsync("TRUNCATE jobfinder_queues.wolverine_queue_preparation; UPDATE jobfinder_queues.wolverine_queue_preparation_scheduled SET message_type='jobfinder.preparation.build-hr-playbook.v99'", Ct);
        var error = await Assert.ThrowsAsync<Npgsql.PostgresException>(() => MigrationTestRunner.DowngradeAndRestoreAsync(context, "20261004021028_DurableMockPreparation", Ct));
        Assert.Contains("retained HR preparation envelopes", error.Message);
    }

    [Fact]
    public async Task Hr_position_refuses_mutated_absent_fact_preimages_without_creating_candidate_state()
    {
        var host = await HostAsync(new Steps());
        await using (var context = await pg.Db.CreateDbContextAsync(Ct))
        {
            await context.Documents.Where(row => row.Key == JobFinder.Pipeline.Profile.FactsProvider.Key).ExecuteDeleteAsync(Ct);
            await context.CandidateRevisions.Where(row => row.Key == JobFinder.Pipeline.Profile.FactsProvider.Key).ExecuteDeleteAsync(Ct);
        }
        var service = host.Services.GetRequiredService<HrPlaybookService>();
        var question = Bank(host).Questions[0];
        var snapshot = (await service.CapturePositionsAsync([question], Ct))[question.Id];
        snapshot.FactsJson = JsonSerializer.Serialize(new CandidateFacts { Facts = [new() { Key = "injected", Question = "Unshown", Answer = "Forged", Confirmed = true }] }, FactsJsonContext.Default.CandidateFacts);
        Assert.False((await service.SavePositionAsync(snapshot, "Legitimate text", Ct)).Ok);
        await using var final = await pg.Db.CreateDbContextAsync(Ct);
        Assert.False(await final.Documents.AnyAsync(row => row.Key == JobFinder.Pipeline.Profile.FactsProvider.Key, Ct));
        Assert.Empty(await final.HrPlaybookRequests.ToListAsync(Ct));
    }

    [Fact]
    public async Task Hr_request_snapshot_notice_and_native_command_commit_once_and_periodic_admission_reuses_it()
    {
        var steps = new Steps();
        var host = await HostAsync(steps);
        var id = (await RequestAsync(host))!.Value;
        Assert.Equal(id, await RequestAsync(host, force: false));
        var root = await RootAsync(id);
        var input = HrPlaybookRequests.ReadInput(root);
        Assert.Equal(1, input.ProfileVersion);
        Assert.Equal(1, input.FactsVersion);
        Assert.Equal(Bank(host).Questions.Count, input.Bank.Questions.Count);
        Assert.Equal(0, steps.Calls);
        Assert.True(await host.Services.GetRequiredService<HrPlaybookService>().RequestedAsync(Ct));
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        Assert.Single(await context.HrPlaybookRequests.ToListAsync(Ct));
        await NotificationAdmissionAssertions.JoinedAsync(context, await context.Notifications.SingleAsync(Ct), Ct);
        Assert.Equal(1, await context.Database.SqlQueryRaw<int>("SELECT count(*)::int AS \"Value\" FROM jobfinder_queues.wolverine_queue_preparation").SingleAsync(Ct));
    }

    [Fact]
    public async Task Hr_preparation_replays_publish_without_repeating_paid_build_and_completed_work_stays_warm()
    {
        var steps = new Steps();
        var host = await HostAsync(steps);
        var id = (await RequestAsync(host))!.Value;
        await BuildAsync(host, id);
        await BuildAsync(host, id);
        Assert.Equal(HrPlaybookStage.Publishing, (await RootAsync(id)).Stage);
        await PublishAsync(host, id);
        await PublishAsync(host, id);
        Assert.Equal(1, steps.Calls);
        Assert.Equal(HrPlaybookStage.Completed, (await RootAsync(id)).Stage);
        Assert.False(await host.Services.GetRequiredService<HrPlaybookService>().RequestedAsync(Ct));
        Assert.Null(await RequestAsync(host, force: false));
        Assert.NotNull(await host.Services.GetRequiredService<HrPlaybookService>().LatestAsync(Ct));
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        var notice = Assert.Single(await context.Notifications.Where(row => row.Text.Contains("обновлены")).ToListAsync(Ct));
        await NotificationAdmissionAssertions.JoinedAsync(context, notice, Ct);
    }

    [Theory]
    [InlineData("admission")]
    [InlineData("publication")]
    [InlineData("checkpoint")]
    public async Task Hr_source_checkpoint_and_notice_boundaries_roll_back_without_paid_repetition(string boundary)
    {
        var fault = new Fault(boundary);
        var steps = new Steps();
        var host = await HostAsync(steps, fault: fault);
        fault.Armed = boundary == "admission";
        if (fault.Armed)
        {
            await Assert.ThrowsAsync<IOException>(() => RequestAsync(host));
            await using var failed = await pg.Db.CreateDbContextAsync(Ct);
            Assert.Empty(await failed.HrPlaybookRequests.ToListAsync(Ct));
            Assert.Empty(await failed.Notifications.ToListAsync(Ct));
            Assert.Empty(await failed.Settings.Where(row => row.Key.StartsWith("hr.playbook")).ToListAsync(Ct));
            fault.Armed = false;
        }
        var id = (await RequestAsync(host))!.Value;
        fault.Armed = boundary == "checkpoint";
        await BuildAsync(host, id);
        Assert.Equal(HrPlaybookStage.Publishing, (await RootAsync(id)).Stage);
        Assert.Equal(1, steps.Calls);
        fault.Armed = boundary == "publication";
        await PublishAsync(host, id);
        if (boundary == "publication")
        {
            Assert.Null(await host.Services.GetRequiredService<HrPlaybookService>().LatestAsync(Ct));
            Assert.True(await host.Services.GetRequiredService<HrPlaybookService>().RequestedAsync(Ct));
            Assert.Equal(HrPlaybookStage.Publishing, (await RootAsync(id)).Stage);
            fault.Armed = false;
            await using (var context = await pg.Db.CreateDbContextAsync(Ct))
                await context.HrPlaybookRequests.Where(row => row.Id == id).ExecuteUpdateAsync(update => update.SetProperty(row => row.RetryAt, (DateTimeOffset?)null), Ct);
            await PublishAsync(host, id);
        }
        Assert.Equal(1, steps.Calls);
        Assert.Equal(HrPlaybookStage.Completed, (await RootAsync(id)).Stage);
    }

    [Fact]
    public async Task New_hr_request_during_paid_build_preserves_the_new_intent_and_retains_the_late_result()
    {
        var steps = new Steps { Release = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        var host = await HostAsync(steps);
        var oldId = (await RequestAsync(host))!.Value;
        var building = BuildAsync(host, oldId);
        await steps.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        var newId = (await RequestAsync(host))!.Value;
        steps.Release.SetResult();
        await building;
        Assert.NotEqual(oldId, newId);
        Assert.Equal(HrPlaybookStage.Cancelled, (await RootAsync(oldId)).Stage);
        Assert.NotNull((await RootAsync(oldId)).ResultJson);
        await PublishAsync(host, oldId);
        Assert.Null(await host.Services.GetRequiredService<HrPlaybookService>().LatestAsync(Ct));
        Assert.True(await host.Services.GetRequiredService<HrPlaybookService>().RequestedAsync(Ct));
        await BuildAsync(host, newId);
        await PublishAsync(host, newId);
        Assert.Equal(HrPlaybookStage.Completed, (await RootAsync(newId)).Stage);
    }

    [Fact]
    public async Task Hr_outage_keeps_the_same_snapshot_request_and_failure_budget_until_the_stage_resumes()
    {
        var steps = new Steps { Unavailable = true };
        var host = await HostAsync(steps);
        var id = (await RequestAsync(host))!.Value;
        var before = (await RootAsync(id)).InputHash;
        await BuildAsync(host, id);
        var paused = await RootAsync(id);
        Assert.Equal(HrPlaybookStage.Building, paused.Stage);
        Assert.Equal(0, paused.Failures);
        Assert.NotNull(paused.RetryAt);
        Assert.Equal(before, paused.InputHash);
        Assert.Equal(id, await RequestAsync(host, force: false));
        steps.Unavailable = false;
        await using (var context = await pg.Db.CreateDbContextAsync(Ct))
            await context.HrPlaybookRequests.Where(row => row.Id == id).ExecuteUpdateAsync(update => update.SetProperty(row => row.RetryAt, (DateTimeOffset?)null), Ct);
        await BuildAsync(host, id);
        await PublishAsync(host, id);
        Assert.Equal(HrPlaybookStage.Completed, (await RootAsync(id)).Stage);
    }

    [Theory]
    [InlineData("profile")]
    [InlineData("facts")]
    public async Task Changed_hr_candidate_after_paid_work_quarantines_the_result_without_publishing(string change)
    {
        var steps = new Steps { Release = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        var host = await HostAsync(steps);
        var id = (await RequestAsync(host))!.Value;
        var building = BuildAsync(host, id);
        await steps.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        var publisher = host.Services.GetRequiredService<ICandidatePublisher>();
        if (change == "profile") await publisher.PublishProfileAsync(new CandidateProfile { Name = "Changed Candidate", Reviewed = true }, 1, "synthetic", Ct);
        else await publisher.PublishFactsAsync(new CandidateFacts { Facts = [new() { Key = "location", Question = "Location?", Answer = "Changed", Confirmed = true }] }, 1, "synthetic", Ct);
        steps.Release.SetResult();
        await building;
        var root = await RootAsync(id);
        Assert.Equal(HrPlaybookStage.Cancelled, root.Stage);
        Assert.NotNull(root.ResultJson);
        Assert.Null(await host.Services.GetRequiredService<HrPlaybookService>().LatestAsync(Ct));
        Assert.Equal(1, steps.Calls);
    }

    [Fact]
    public async Task Hr_position_and_candidate_history_and_new_native_request_share_one_transaction_and_stale_ui_keeps_its_witness()
    {
        var fault = new Fault("admission");
        var host = await HostAsync(new Steps(), fault: fault);
        var service = host.Services.GetRequiredService<HrPlaybookService>();
        var question = Bank(host).Questions[0];
        var captured = (await service.CapturePositionsAsync([question], Ct))[question.Id];
        fault.Armed = true;
        await Assert.ThrowsAsync<IOException>(() => service.SavePositionAsync(captured, "Original desired answer", Ct));
        await using (var context = await pg.Db.CreateDbContextAsync(Ct))
        {
            Assert.Equal(1, (await context.Documents.SingleAsync(row => row.Key == JobFinder.Pipeline.Profile.FactsProvider.Key, Ct)).Version);
            Assert.Empty(await context.HrPlaybookRequests.ToListAsync(Ct));
            Assert.Empty(await context.Notifications.ToListAsync(Ct));
        }
        fault.Armed = false;
        Assert.True((await service.SavePositionAsync(captured, "Original desired answer", Ct)).Ok);
        Assert.False((await service.SavePositionAsync(captured, "Late overwrite", Ct)).Ok);
        await using var final = await pg.Db.CreateDbContextAsync(Ct);
        var root = Assert.Single(await final.HrPlaybookRequests.ToListAsync(Ct));
        Assert.Equal(2, HrPlaybookRequests.ReadInput(root).FactsVersion);
        var facts = JsonSerializer.Deserialize(HrPlaybookRequests.ReadInput(root).FactsJson, FactsJsonContext.Default.CandidateFacts)!;
        Assert.Contains(facts.Facts, fact => fact.Answer == "Original desired answer" && fact.Confirmed);
        Assert.DoesNotContain(facts.Facts, fact => fact.Answer == "Late overwrite");
        Assert.Equal(1, captured.FactsVersion);
        Assert.Equal(2, await final.CandidateRevisions.CountAsync(row => row.Key == JobFinder.Pipeline.Profile.FactsProvider.Key, Ct));
    }

    [Fact]
    public async Task Legacy_hr_flag_import_is_not_lost_and_generic_writers_cannot_override_native_intents()
    {
        var host = await HostAsync(new Steps());
        await using (var context = await pg.Db.CreateDbContextAsync(Ct))
        {
            context.Settings.Add(new() { Key = HrPlaybookService.RequestedKey, Value = "True", UpdatedAt = DateTimeOffset.UtcNow });
            await context.SaveChangesAsync(Ct);
        }
        Assert.True(await host.Services.GetRequiredService<HrPlaybookService>().RequestedAsync(Ct));
        Assert.NotNull(await RequestAsync(host, force: false));
        var settings = host.Services.GetRequiredService<RuntimeSettings>();
        foreach (var key in new[] { HrPlaybookService.RequestedKey, HrPlaybookService.LatestKey, HrPlaybookSources.RequestKey, HrPlaybookSources.OwnerKey })
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => settings.SetAsync(key, "replacement", Ct));
            await Assert.ThrowsAsync<InvalidOperationException>(() => settings.RemoveAsync(key, Ct));
        }
    }

    [Fact]
    public async Task Native_hr_checkpoint_restart_publishes_through_the_real_listener_without_building_again()
    {
        var steps = new Steps();
        var producer = await HostAsync(steps);
        var id = (await RequestAsync(producer))!.Value;
        await BuildAsync(producer, id);
        await producer.StopAsync(Ct);
        var consumer = await HostAsync(steps, consumer: true, seed: false);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        await consumer.Services.GetRequiredService<HrPlaybookRequests>().WaitAsync(id, timeout.Token);
        Assert.Equal(1, steps.Calls);
        Assert.Equal(HrPlaybookStage.Completed, (await RootAsync(id)).Stage);
    }

    [Fact]
    public async Task Hr_downgrade_refuses_roots_even_when_they_are_completed()
    {
        var host = await HostAsync(new Steps());
        var id = (await RequestAsync(host))!.Value;
        await BuildAsync(host, id);
        await PublishAsync(host, id);
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        var error = await Assert.ThrowsAsync<Npgsql.PostgresException>(() => MigrationTestRunner.DowngradeAndRestoreAsync(context, "20261004021028_DurableMockPreparation", Ct));
        Assert.Contains("retained HR", error.Message);
        Assert.Equal(HrPlaybookStage.Completed, (await RootAsync(id)).Stage);
    }

    private sealed class Steps : IHrPlaybookPreparationSteps
    {
        public int Calls;
        public bool Unavailable;
        public bool CheckpointFault;
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource? Release;
        public void EnsureCompatible(HrPlaybookInput input) { }
        public async Task<HrPlaybook> BuildAsync(HrPlaybookInput input, CancellationToken ct)
        {
            Interlocked.Increment(ref Calls);
            Entered.TrySetResult();
            if (Release is not null) await Release.Task.WaitAsync(ct);
            if (Unavailable) throw new LlmUnavailableException("synthetic unavailable", false);
            var result = new HrPlaybook { Version = HrPlaybook.CurrentVersion, GeneratedAt = DateTimeOffset.UtcNow, Model = "synthetic",
                Items = input.Bank.Questions.Select(question => new HrPlaybookItem { Bank = question, Item = new PrepItem
                {
                    Q = HrPlaybookService.ToPlanned(question), A = new SpokenAnswer { Start = "Supported original answer", Core = "Supported detailed answer" },
                    Fingerprint = Fingerprint.Of(question.Id, "synthetic"),
                } }).ToList() };
            if (CheckpointFault) throw new HrPlaybookCheckpointException(new IOException("synthetic paid cache failure"), JsonSerializer.Serialize(result, InterviewJsonContext.Default.HrPlaybook));
            return result;
        }
    }

    private sealed class Fault(string boundary) : SaveChangesInterceptor
    {
        public bool Armed;
        public int Failures;
        public int Limit = 1;
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData data, InterceptionResult<int> result, CancellationToken ct = default)
        {
            if (Armed && Failures < Limit && data.Context is JobFinderDbContext context &&
                (boundary == "admission" && context.ChangeTracker.Entries<HrPlaybookRequest>().Any(entry => entry.State == EntityState.Added)
                || boundary == "checkpoint" && context.ChangeTracker.Entries<HrPlaybookRequest>().Any(entry => entry.State == EntityState.Modified && entry.Entity.Stage == HrPlaybookStage.Publishing)
                || boundary == "publication" && context.ChangeTracker.Entries<AppSetting>().Any(entry => entry.Entity.Key == HrPlaybookService.LatestKey)))
            {
                Failures++;
                throw new IOException("synthetic HR boundary fault");
            }
            return ValueTask.FromResult(result);
        }
    }
}
