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
using JobFinder.Pipeline.Settings;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace JobFinder.Tests.Pipeline;

public sealed class DurableRejectionReviewTests(PostgresFixture pg) : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private readonly List<IHost> _hosts = [];

    public async ValueTask InitializeAsync()
    {
        Assert.SkipWhen(pg.SkipReason is not null, pg.SkipReason ?? "");
        await pg.SeedPoliciesAsync();
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        await context.RejectionReviewRequests.ExecuteDeleteAsync(Ct);
        await context.Applications.ExecuteDeleteAsync(Ct);
        await context.Vacancies.ExecuteDeleteAsync(Ct);
        await context.Documents.Where(row => row.Key.StartsWith("candidate.")).ExecuteDeleteAsync(Ct);
        await context.CandidateRevisions.ExecuteDeleteAsync(Ct);
        await context.Settings.Where(row => row.Key.StartsWith("analysis.rejections")).ExecuteDeleteAsync(Ct);
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
            services.RemoveAll<IRejectionReviewSteps>();
            services.AddSingleton<IRejectionReviewSteps>(steps);
            if (!consumer) services.AddHostedService<SingleInstanceGuard>();
        });
        _hosts.Add(host);
        await host.StartAsync(Ct);
        if (seed)
        {
            await host.Services.GetRequiredService<ICandidatePublisher>().PublishProfileAsync(
                new CandidateProfile { Name = "Synthetic Candidate", Reviewed = true, Summary = "Original supported facts" }, 0, "synthetic", Ct);
            await using var context = await pg.Db.CreateDbContextAsync(Ct);
            for (var index = 0; index < RejectionReviewService.MinRejections; index++)
                context.Applications.Add(new Application
                {
                    Status = ApplicationStatus.Sent, Stage = ApplicationStage.Rejected, Channel = ApplicationChannel.HeadHunter,
                    CreatedAt = DateTimeOffset.UtcNow.AddDays(-3), SentAt = DateTimeOffset.UtcNow.AddDays(-2), StageChangedAt = DateTimeOffset.UtcNow.AddDays(-1),
                    Vacancy = new Vacancy { Title = "Backend " + index, Url = "https://vacancy.invalid/" + index, Description = "Supported backend demands",
                        Fingerprint = Guid.NewGuid().ToString("N"), FirstSeenAt = DateTimeOffset.UtcNow.AddDays(-4) },
                });
            await context.SaveChangesAsync(Ct);
            await NotificationAdmissionAssertions.ResetAsync(context, Ct);
        }
        return host;
    }

    private async Task<RejectionReviewRequest> RootAsync(Guid id)
    {
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        return await context.RejectionReviewRequests.AsNoTracking().SingleAsync(row => row.Id == id, Ct);
    }

    private static async Task<Guid> RequestAsync(IHost host, bool force = false)
    {
        var result = await host.Services.GetRequiredService<RejectionReviewSources>().RequestAsync(Ct, force);
        Assert.True(result.Result.Ok, result.Result.Message);
        return result.Id!.Value;
    }
    private static Task AnalyzeAsync(IHost host, Guid id) => host.Services.GetRequiredService<RejectionReviewWorkflow>().AnalyzeAsync(new() { RequestId = id }, Guid.NewGuid(), Ct);
    private static Task PublishAsync(IHost host, Guid id) => host.Services.GetRequiredService<RejectionReviewWorkflow>().PublishAsync(new() { RequestId = id }, Guid.NewGuid(), Ct);

    [Fact]
    public async Task Newly_sent_response_after_capture_replaces_the_queued_rejection_cohort_instead_of_reusing_stale_work()
    {
        var host = await HostAsync(new Steps());
        var old = await RequestAsync(host);
        var capturedAt = RejectionReviewRequests.ReadInput(await RootAsync(old)).CapturedAt;
        await using (var context = await pg.Db.CreateDbContextAsync(Ct))
        {
            var sentAt = DateTimeOffset.UtcNow;
            Assert.True(sentAt > capturedAt);
            context.Applications.Add(new Application { Status = ApplicationStatus.Sent, Stage = ApplicationStage.Rejected,
                CreatedAt = sentAt, SentAt = sentAt, Vacancy = new Vacancy { Title = "New response", Url = "https://vacancy.invalid/new",
                    Fingerprint = Guid.NewGuid().ToString("N"), FirstSeenAt = sentAt } });
            await context.SaveChangesAsync(Ct);
        }
        var current = await RequestAsync(host);
        Assert.NotEqual(old, current);
        Assert.Equal(RejectionReviewStage.Cancelled, (await RootAsync(old)).Stage);
        Assert.Equal(6, RejectionReviewRequests.ReadInput(await RootAsync(current)).Responses);
    }

    [Fact]
    public async Task Malformed_rejection_source_identity_is_quarantined_before_paid_work_without_retrying_a_damaged_protocol()
    {
        var steps = new Steps();
        var host = await HostAsync(steps);
        var id = await RequestAsync(host);
        await using (var context = await pg.Db.CreateDbContextAsync(Ct))
        {
            var root = await context.RejectionReviewRequests.SingleAsync(row => row.Id == id, Ct);
            var input = RejectionReviewRequests.ReadInput(root);
            var source = System.Text.Json.Nodes.JsonNode.Parse(input.SourceJson)!;
            source[0]!["application"]!["id"] = "invalid numeric identity";
            input.SourceJson = source.ToJsonString();
            root.InputJson = JsonSerializer.Serialize(input, RejectionReviewJsonContext.Default.RejectionReviewInputSnapshot);
            root.InputHash = CanonicalJson.Hash(root.InputJson);
            await context.SaveChangesAsync(Ct);
        }
        await Assert.ThrowsAsync<UnsupportedRejectionReviewException>(() => AnalyzeAsync(host, id));
        Assert.Equal(RejectionReviewStage.Failed, (await RootAsync(id)).Stage);
        Assert.Equal(0, steps.Calls);
    }

    [Fact]
    public async Task Review_facade_admits_original_inputs_immediately_and_duplicate_controls_reuse_the_native_root()
    {
        var steps = new Steps();
        var host = await HostAsync(steps);
        Assert.True((await host.Services.GetRequiredService<RejectionReviewService>().ReviewAsync(Ct)).Ok);
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        var root = Assert.Single(await context.RejectionReviewRequests.AsNoTracking().ToListAsync(Ct));
        var input = RejectionReviewRequests.ReadInput(root);
        Assert.Equal(5, input.Rejections);
        Assert.Contains("Original supported facts", input.ProfileJson);
        Assert.Contains("Backend 0", input.FactsText);
        Assert.Equal(1, input.ProfileVersion);
        Assert.Equal(root.Id, input.Operation.OperationId);
        Assert.DoesNotContain("synthetic-key", root.InputJson);
        Assert.Equal(root.Id, await RequestAsync(host));
        Assert.Equal(0, steps.Calls);
        Assert.Single(await context.Notifications.ToListAsync(Ct));
        Assert.Equal(1, await context.Database.SqlQuery<int>($"SELECT count(*)::int AS \"Value\" FROM jobfinder_queues.wolverine_queue_preparation WHERE message_type = 'jobfinder.preparation.analyze-rejections.v1'").SingleAsync(Ct));
    }

    [Fact]
    public async Task Rejection_checkpoint_publication_replay_and_warm_control_do_not_pay_again_or_split_result_and_timestamp()
    {
        var steps = new Steps();
        var host = await HostAsync(steps);
        var id = await RequestAsync(host);
        await AnalyzeAsync(host, id);
        Assert.Equal(RejectionReviewStage.Publishing, (await RootAsync(id)).Stage);
        Assert.Null((await host.Services.GetRequiredService<RejectionReviewService>().LatestAsync(Ct)).Review);
        await AnalyzeAsync(host, id);
        await PublishAsync(host, id);
        await PublishAsync(host, id);
        Assert.Equal(id, await RequestAsync(host));
        Assert.Equal(1, steps.Calls);
        var latest = await host.Services.GetRequiredService<RejectionReviewService>().LatestAsync(Ct);
        Assert.Equal("Original review", latest.Review!.Summary);
        Assert.Equal(RejectionReviewRequests.ReadResult(await RootAsync(id)).GeneratedAt, latest.At);
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        Assert.Equal(2, await context.Notifications.CountAsync(Ct));
    }

    [Theory]
    [InlineData("admission")]
    [InlineData("checkpoint")]
    [InlineData("publication")]
    public async Task Rejection_native_boundaries_roll_back_and_retry_saved_results_without_paid_rework(string boundary)
    {
        var fault = new Fault(boundary);
        var steps = new Steps();
        var host = await HostAsync(steps, fault: fault);
        fault.Armed = boundary == "admission";
        if (fault.Armed)
        {
            await Assert.ThrowsAsync<IOException>(() => RequestAsync(host));
            await using var failed = await pg.Db.CreateDbContextAsync(Ct);
            Assert.Empty(await failed.RejectionReviewRequests.ToListAsync(Ct));
            Assert.Empty(await failed.Notifications.ToListAsync(Ct));
            Assert.Empty(await failed.Settings.Where(row => row.Key.StartsWith("analysis.rejections")).ToListAsync(Ct));
        }
        fault.Armed = false;
        var id = await RequestAsync(host);
        fault.Armed = boundary == "checkpoint";
        await AnalyzeAsync(host, id);
        Assert.Equal(1, steps.Calls);
        fault.Armed = boundary == "publication";
        await PublishAsync(host, id);
        if (boundary == "publication")
        {
            Assert.Equal(RejectionReviewStage.Publishing, (await RootAsync(id)).Stage);
            Assert.Null((await host.Services.GetRequiredService<RejectionReviewService>().LatestAsync(Ct)).Review);
            fault.Armed = false;
            await using var retry = await pg.Db.CreateDbContextAsync(Ct);
            await retry.RejectionReviewRequests.Where(row => row.Id == id).ExecuteUpdateAsync(update => update.SetProperty(row => row.RetryAt, (DateTimeOffset?)null), Ct);
            await PublishAsync(host, id);
        }
        Assert.Equal(RejectionReviewStage.Completed, (await RootAsync(id)).Stage);
        Assert.Equal(1, steps.Calls);
        Assert.Equal(1, fault.Failures);
    }

    [Fact]
    public async Task Rejection_provider_outage_retains_original_input_and_resumes_the_same_request()
    {
        var steps = new Steps { Unavailable = true };
        var host = await HostAsync(steps);
        var id = await RequestAsync(host);
        var original = await RootAsync(id);
        await AnalyzeAsync(host, id);
        var paused = await RootAsync(id);
        Assert.Equal(original.InputHash, paused.InputHash);
        Assert.Equal(0, paused.Failures);
        Assert.Null(paused.ResultJson);
        Assert.NotNull(paused.RetryAt);
        steps.Unavailable = false;
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        await context.RejectionReviewRequests.Where(row => row.Id == id).ExecuteUpdateAsync(update => update.SetProperty(row => row.RetryAt, (DateTimeOffset?)null), Ct);
        await AnalyzeAsync(host, id);
        await PublishAsync(host, id);
        Assert.Equal(RejectionReviewStage.Completed, (await RootAsync(id)).Stage);
        Assert.Equal(2, steps.Calls);
    }

    [Theory]
    [InlineData("candidate")]
    [InlineData("application")]
    [InlineData("event")]
    [InlineData("vacancy")]
    public async Task Changed_rejection_evidence_cancels_late_analysis_and_retains_the_known_paid_result(string change)
    {
        var steps = new Steps { Release = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        var host = await HostAsync(steps);
        var id = await RequestAsync(host);
        var running = AnalyzeAsync(host, id);
        await steps.Entered.Task.WaitAsync(Ct);
        if (change == "candidate")
            await host.Services.GetRequiredService<ICandidatePublisher>().PublishProfileAsync(new CandidateProfile { Name = "Changed candidate" }, 1, "synthetic", Ct);
        else
        {
            await using var context = await pg.Db.CreateDbContextAsync(Ct);
            var application = await context.Applications.OrderBy(row => row.Id).FirstAsync(Ct);
            if (change == "application") { application.Revision++; application.Stage = ApplicationStage.Replied; }
            if (change == "event") context.ApplicationEvents.Add(new ApplicationEvent { ApplicationId = application.Id, At = DateTimeOffset.UtcNow, Kind = EventKind.MessageIn, Text = "Changed employer evidence" });
            if (change == "vacancy") (await context.Vacancies.SingleAsync(row => row.Id == application.VacancyId, Ct)).Description = "Changed demand";
            await context.SaveChangesAsync(Ct);
        }
        steps.Release.SetResult();
        await running;
        var root = await RootAsync(id);
        Assert.Equal(RejectionReviewStage.Cancelled, root.Stage);
        Assert.NotNull(root.ResultJson);
        Assert.Null((await host.Services.GetRequiredService<RejectionReviewService>().LatestAsync(Ct)).Review);
        Assert.Equal(1, steps.Calls);
    }

    [Fact]
    public async Task New_rejection_request_during_paid_work_wins_and_late_loser_keeps_its_result_without_publication()
    {
        var steps = new Steps { Release = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        var host = await HostAsync(steps);
        var old = await RequestAsync(host);
        var running = AnalyzeAsync(host, old);
        await steps.Entered.Task.WaitAsync(Ct);
        var current = await RequestAsync(host, force: true);
        steps.Release.SetResult();
        await running;
        Assert.Equal(RejectionReviewStage.Cancelled, (await RootAsync(old)).Stage);
        Assert.NotNull((await RootAsync(old)).ResultJson);
        await AnalyzeAsync(host, current);
        await PublishAsync(host, current);
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        Assert.Equal(current.ToString("D"), (await context.Settings.SingleAsync(row => row.Key == RejectionReviewSources.OwnerKey, Ct)).Value);
        Assert.Equal(2, steps.Calls);
    }

    [Fact]
    public async Task Rejection_checkpoint_failure_quarantines_known_result_without_automatic_paid_replay()
    {
        var fault = new Fault("checkpoint") { Limit = 3 };
        var steps = new Steps();
        var host = await HostAsync(steps, fault: fault);
        var id = await RequestAsync(host);
        fault.Armed = true;
        await Assert.ThrowsAsync<RejectionReviewCheckpointException>(() => AnalyzeAsync(host, id));
        var root = await RootAsync(id);
        Assert.Equal(RejectionReviewStage.Failed, root.Stage);
        Assert.NotNull(root.ResultJson);
        Assert.Equal(CanonicalJson.Hash(root.ResultJson), root.ResultHash);
        await AnalyzeAsync(host, id);
        Assert.Equal(1, steps.Calls);
        Assert.Null((await host.Services.GetRequiredService<RejectionReviewService>().LatestAsync(Ct)).Review);
    }

    [Fact]
    public async Task Native_rejection_listener_restart_publishes_the_saved_checkpoint_without_another_paid_call()
    {
        var steps = new Steps();
        var producer = await HostAsync(steps);
        var id = await RequestAsync(producer);
        await AnalyzeAsync(producer, id);
        await producer.StopAsync(Ct);
        var consumer = await HostAsync(steps, consumer: true, seed: false);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        await consumer.Services.GetRequiredService<RejectionReviewRequests>().WaitAsync(id, timeout.Token);
        Assert.Equal(RejectionReviewStage.Completed, (await RootAsync(id)).Stage);
        Assert.Equal(1, steps.Calls);
    }

    [Fact]
    public async Task Legacy_rejection_results_remain_readable_but_native_owned_publication_keys_refuse_generic_edits()
    {
        var host = await HostAsync(new Steps());
        var at = DateTimeOffset.UtcNow;
        await using (var context = await pg.Db.CreateDbContextAsync(Ct))
        {
            context.Settings.Add(new() { Key = RejectionReviewService.LatestKey, Value = JsonSerializer.Serialize(new RejectionReview { Summary = "Legacy review" }, CareerJsonContext.Default.RejectionReview), UpdatedAt = at });
            context.Settings.Add(new() { Key = RejectionReviewService.LatestAtKey, Value = at.ToString("O"), UpdatedAt = at });
            await context.SaveChangesAsync(Ct);
        }
        Assert.Equal("Legacy review", (await host.Services.GetRequiredService<RejectionReviewService>().LatestAsync(Ct)).Review!.Summary);
        var settings = host.Services.GetRequiredService<RuntimeSettings>();
        foreach (var key in new[] { RejectionReviewService.LatestKey, RejectionReviewService.LatestAtKey, RejectionReviewSources.RequestKey, RejectionReviewSources.OwnerKey })
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => settings.SetAsync(key, "override", Ct));
            await Assert.ThrowsAsync<InvalidOperationException>(() => settings.RemoveAsync(key, Ct));
        }
        var id = await RequestAsync(host);
        await AnalyzeAsync(host, id);
        await PublishAsync(host, id);
        Assert.Equal("Original review", (await host.Services.GetRequiredService<RejectionReviewService>().LatestAsync(Ct)).Review!.Summary);
    }

    [Fact]
    public async Task Rejection_native_result_and_timestamp_proof_cannot_be_replaced_or_silently_repaired()
    {
        var host = await HostAsync(new Steps());
        var id = await RequestAsync(host);
        await AnalyzeAsync(host, id);
        await PublishAsync(host, id);
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        await context.Settings.Where(row => row.Key == RejectionReviewService.LatestAtKey).ExecuteUpdateAsync(update => update.SetProperty(row => row.Value, "damaged"), Ct);
        await Assert.ThrowsAsync<UnsupportedRejectionReviewException>(() => host.Services.GetRequiredService<RejectionReviewService>().LatestAsync(Ct));
        await Assert.ThrowsAsync<UnsupportedRejectionReviewException>(() => RequestAsync(host));
        Assert.Equal(1, await context.RejectionReviewRequests.CountAsync(Ct));
    }

    [Theory]
    [InlineData("profile")]
    [InlineData("rejections")]
    public async Task Missing_rejection_prerequisites_never_claim_admission_or_call_a_model(string missing)
    {
        var steps = new Steps();
        var host = await HostAsync(steps);
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        if (missing == "profile") await context.Documents.Where(row => row.Key == JobFinder.Pipeline.Profile.ProfileProvider.Key).ExecuteDeleteAsync(Ct);
        else await context.Applications.ExecuteDeleteAsync(Ct);
        var result = await host.Services.GetRequiredService<RejectionReviewService>().ReviewAsync(Ct);
        Assert.False(result.Ok);
        Assert.Empty(await context.RejectionReviewRequests.ToListAsync(Ct));
        Assert.Empty(await context.Notifications.ToListAsync(Ct));
        Assert.Equal(0, steps.Calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Rejection_downgrade_refuses_completed_roots_or_orphaned_future_native_envelopes(bool orphan)
    {
        var host = await HostAsync(new Steps());
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        if (orphan)
        {
            await using var scope = host.Services.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<Wolverine.IMessageBus>().PublishAsync(new AnalyzeRejectionReview { RequestId = Guid.NewGuid() });
            await context.Database.ExecuteSqlRawAsync("UPDATE jobfinder_queues.wolverine_queue_preparation SET message_type = 'jobfinder.preparation.analyze-rejections.v99'", Ct);
        }
        else
        {
            var id = await RequestAsync(host);
            await AnalyzeAsync(host, id);
            await PublishAsync(host, id);
        }
        var error = await Assert.ThrowsAsync<Npgsql.PostgresException>(() => MigrationTestRunner.DowngradeAndRestoreAsync(context, "20261004045533_DurableHrPlaybookPreparation", Ct));
        Assert.Contains("retained rejection", error.Message);
    }

    private sealed class Steps : IRejectionReviewSteps
    {
        public int Calls;
        public bool Unavailable;
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource? Release;
        public void EnsureCompatible(RejectionReviewInputSnapshot input) { }
        public async Task<RejectionReviewResult> AnalyzeAsync(RejectionReviewInputSnapshot input, CancellationToken ct)
        {
            Interlocked.Increment(ref Calls);
            Entered.TrySetResult();
            if (Release is not null) await Release.Task.WaitAsync(ct);
            if (Unavailable) throw new LlmUnavailableException("synthetic unavailable", false);
            return new() { Review = new() { Summary = "Original review" }, Model = "synthetic", GeneratedAt = DateTimeOffset.UtcNow };
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
                (boundary == "admission" && context.ChangeTracker.Entries<RejectionReviewRequest>().Any(entry => entry.State == EntityState.Added)
                || boundary == "checkpoint" && context.ChangeTracker.Entries<RejectionReviewRequest>().Any(entry => entry.State == EntityState.Modified && entry.Entity.Stage == RejectionReviewStage.Publishing)
                || boundary == "publication" && context.ChangeTracker.Entries<AppSetting>().Any(entry => entry.Entity.Key == RejectionReviewService.LatestKey)))
            {
                Failures++;
                throw new IOException("synthetic rejection boundary fault");
            }
            return ValueTask.FromResult(result);
        }
    }
}
