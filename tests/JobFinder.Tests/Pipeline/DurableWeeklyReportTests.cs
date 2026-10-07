using System.Data.Common;
using System.Reflection;
using System.Text.Json;
using JobFinder.Core.Domain;
using JobFinder.Core.Options;
using JobFinder.Core.Profile;
using JobFinder.Core.Text;
using JobFinder.Data;
using JobFinder.Llm.Client;
using JobFinder.Llm.Tasks;
using JobFinder.Pipeline;
using JobFinder.Pipeline.Jobs;
using JobFinder.Pipeline.Maintenance;
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
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace JobFinder.Tests.Pipeline;

public sealed class DurableWeeklyReportTests(PostgresFixture pg) : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 4, 15, 10, 0, TimeSpan.Zero));
    private readonly List<IHost> _hosts = [];

    public async ValueTask InitializeAsync()
    {
        Assert.SkipWhen(pg.SkipReason is not null, pg.SkipReason ?? "");
        await pg.SeedPoliciesAsync();
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        await context.WeeklyReportRequests.ExecuteDeleteAsync(Ct);
        await context.JobRuns.ExecuteDeleteAsync(Ct);
        await context.Applications.ExecuteDeleteAsync(Ct);
        await context.Vacancies.ExecuteDeleteAsync(Ct);
        await context.Documents.Where(row => row.Key.StartsWith("candidate.")).ExecuteDeleteAsync(Ct);
        await context.CandidateRevisions.ExecuteDeleteAsync(Ct);
        await context.Settings.Where(row => row.Key.StartsWith("report.") || row.Key == WeeklyReportSources.CursorKey).ExecuteDeleteAsync(Ct);
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

    private async Task<IHost> HostAsync(Steps steps, bool consumer = false, IInterceptor? fault = null, bool seed = true, bool resume = true)
    {
        var host = await PostProcessingTestHost.BuildAsync(pg, consumer, fault: fault, configure: services =>
        {
            services.RemoveAll<TimeProvider>();
            services.AddSingleton<TimeProvider>(_time);
            services.RemoveAll<IWeeklyReportSteps>();
            services.AddSingleton<IWeeklyReportSteps>(steps);
            if (!consumer) services.AddHostedService<SingleInstanceGuard>();
        });
        _hosts.Add(host);
        if (resume)
        {
            var directory = host.Services.GetRequiredService<PathsOptions>().Profile;
            Directory.CreateDirectory(directory);
            await File.WriteAllTextAsync(Path.Combine(directory, "resume.txt"), "Synthetic original backend resume", Ct);
        }
        await host.StartAsync(Ct);
        if (seed)
        {
            await host.Services.GetRequiredService<ICandidatePublisher>().PublishProfileAsync(new CandidateProfile
                { Name = "Synthetic Candidate", Summary = "Original supported facts", Reviewed = true }, 0, "synthetic", Ct);
            await using var context = await pg.Db.CreateDbContextAsync(Ct);
            for (var index = 0; index < 2; index++)
                context.Vacancies.Add(new Vacancy { Title = "Backend " + index, Url = "https://vacancy.invalid/" + index,
                    FirstSeenAt = _time.GetUtcNow().AddDays(-1), Fingerprint = Guid.NewGuid().ToString("N"), Score = 80,
                    Evaluation = new VacancyEvaluation { Score = 80, Keywords = ["C#", "PostgreSQL"] } });
            await context.SaveChangesAsync(Ct);
            await NotificationAdmissionAssertions.ResetAsync(context, Ct);
        }
        return host;
    }

    private async Task<WeeklyReportRequest> RootAsync(Guid id)
    {
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        return await context.WeeklyReportRequests.AsNoTracking().SingleAsync(row => row.Id == id, Ct);
    }
    private static Task<Guid> RequestAsync(IHost host, JobInvocation? invocation = null, bool force = false) => host.Services.GetRequiredService<WeeklyReportSources>().RequestAsync(Ct, invocation, force);
    private Task PrepareAsync(IHost host, Guid id) => DriveAsync(host.Services.GetRequiredService<WeeklyReportWorkflow>().PrepareAsync(new() { RequestId = id }, Guid.NewGuid(), Ct));
    private Task PublishAsync(IHost host, Guid id) => DriveAsync(host.Services.GetRequiredService<WeeklyReportWorkflow>().PublishAsync(new() { RequestId = id }, Guid.NewGuid(), Ct));
    private async Task DriveAsync(Task operation)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        while (!operation.IsCompleted)
        {
            _time.Advance(TimeSpan.FromMilliseconds(50));
            await Task.WhenAny(operation, Task.Delay(TimeSpan.FromMilliseconds(10), timeout.Token));
            timeout.Token.ThrowIfCancellationRequested();
        }
        await operation;
    }
    private async Task<JobInvocation> InvocationAsync(bool manual = false, DateTimeOffset? at = null)
    {
        var startedAt = at ?? _time.GetUtcNow();
        var slot = manual ? (DateTimeOffset?)null : startedAt.AddMinutes(-10);
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        var run = new JobRun { Job = JobNames.WeeklyReport, StartedAt = startedAt, ScheduledSlot = slot, ManualTrigger = manual, Status = JobRunStatus.Running };
        context.JobRuns.Add(run);
        await context.SaveChangesAsync(Ct);
        return new(run.Id, startedAt, manual, slot);
    }

    [Fact]
    public async Task Replayed_weekly_receipt_keeps_the_original_manual_mode_and_start_instead_of_rebinding_native_history()
    {
        var host = await HostAsync(new Steps());
        var invocation = await InvocationAsync(manual: true);
        var id = await RequestAsync(host, invocation);
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        await context.JobRuns.Where(row => row.Id == invocation.RunId).ExecuteUpdateAsync(update => update
            .SetProperty(row => row.ManualTrigger, false).SetProperty(row => row.ScheduledSlot, (DateTimeOffset?)invocation.StartedAt.AddMinutes(-10)), Ct);
        await Assert.ThrowsAsync<UnsupportedWeeklyReportException>(() => RequestAsync(host, invocation with { Manual = false, Slot = invocation.StartedAt.AddMinutes(-10) }));
        await context.JobRuns.Where(row => row.Id == invocation.RunId).ExecuteUpdateAsync(update => update
            .SetProperty(row => row.ManualTrigger, true).SetProperty(row => row.ScheduledSlot, (DateTimeOffset?)null)
            .SetProperty(row => row.StartedAt, invocation.StartedAt.AddHours(-1)), Ct);
        await Assert.ThrowsAsync<UnsupportedWeeklyReportException>(() => RequestAsync(host, invocation with { StartedAt = invocation.StartedAt.AddHours(-1) }));
        Assert.Equal(id, (await context.WeeklyReportRequests.SingleAsync(Ct)).Id);
        Assert.Single(await context.Notifications.ToListAsync(Ct));
        Assert.Null(await host.Services.GetRequiredService<WeeklyReportSources>().LastSlotAsync(Ct));
    }

    [Fact]
    public async Task Duplicate_weekly_slot_receipt_replay_remains_readable_after_a_later_slot_advances_the_cursor()
    {
        var host = await HostAsync(new Steps());
        var first = await InvocationAsync();
        var id = await RequestAsync(host, first);
        var repeated = await InvocationAsync();
        Assert.Equal(id, await RequestAsync(host, repeated));
        _time.Advance(TimeSpan.FromDays(7));
        var next = await InvocationAsync();
        var nextId = await RequestAsync(host, next);
        Assert.NotEqual(id, nextId);
        Assert.Equal(id, await RequestAsync(host, repeated));
        Assert.Equal(next.Slot, await host.Services.GetRequiredService<WeeklyReportSources>().LastSlotAsync(Ct));
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        Assert.Equal(2, await context.Notifications.CountAsync(Ct));
        Assert.Equal(3, await context.JobRuns.CountAsync(row => row.Status == JobRunStatus.Ok, Ct));
    }

    [Fact]
    public async Task Weekly_statistics_receipt_cursor_and_native_advice_command_commit_before_paid_work()
    {
        var steps = new Steps();
        var host = await HostAsync(steps);
        var invocation = await InvocationAsync();
        var id = await RequestAsync(host, invocation);
        var root = await RootAsync(id);
        var input = WeeklyReportRequests.ReadInput(root);
        Assert.Equal(WeeklyReportStage.Preparing, root.Stage);
        Assert.Contains("Original supported facts", input.ProfileJson!);
        Assert.Equal(invocation.StartedAt, input.CapturedAt);
        Assert.Equal(invocation.Slot, await host.Services.GetRequiredService<WeeklyReportSources>().LastSlotAsync(Ct));
        Assert.DoesNotContain("synthetic-key", root.InputJson);
        Assert.Equal(0, steps.Calls);
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        var run = await context.JobRuns.SingleAsync(Ct);
        Assert.Equal(JobRunStatus.Ok, run.Status);
        Assert.Equal(id, run.WeeklyReportRequestId);
        var notice = Assert.Single(await context.Notifications.ToListAsync(Ct));
        Assert.Equal(NotificationKind.Digest, notice.Kind);
        Assert.Equal(root.NotificationId, notice.Id);
        await NotificationAdmissionAssertions.JoinedAsync(context, notice, Ct);
        Assert.Contains("Найдено вакансий: <b>2</b>", (await host.Services.GetRequiredService<WeeklyReportJob>().LatestAsync(Ct)).Html!);
    }

    [Fact]
    public async Task Weekly_saved_advice_publishes_atomically_without_paying_again_on_replay_or_warm_dashboard_request()
    {
        var steps = new Steps();
        var host = await HostAsync(steps);
        var id = await RequestAsync(host);
        await PrepareAsync(host, id);
        Assert.Equal(WeeklyReportStage.Publishing, (await RootAsync(id)).Stage);
        Assert.Null((await host.Services.GetRequiredService<WeeklyReportJob>().LatestAsync(Ct)).Advice);
        await PrepareAsync(host, id);
        await PublishAsync(host, id);
        await PublishAsync(host, id);
        Assert.Equal(id, await RequestAsync(host));
        Assert.Equal(1, steps.Calls);
        var latest = await host.Services.GetRequiredService<WeeklyReportJob>().LatestAsync(Ct);
        Assert.Contains("Original advice", latest.Html!);
        Assert.Equal("Original advice", latest.Advice!.Title);
        var root = await RootAsync(id);
        Assert.True(root.AdvicePublished);
        Assert.Equal(WeeklyReportStage.Completed, root.Stage);
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        Assert.Equal(2, await context.Notifications.CountAsync(Ct));
        Assert.Contains("Original advice", (await context.Notifications.OrderBy(row => row.Id).LastAsync(Ct)).Text);
    }

    [Theory]
    [InlineData("admission")]
    [InlineData("checkpoint")]
    [InlineData("publication")]
    public async Task Weekly_boundaries_roll_back_source_notices_receipts_and_cursors_and_reuse_paid_checkpoints(string boundary)
    {
        var fault = new Fault(boundary);
        var steps = new Steps();
        var host = await HostAsync(steps, fault: fault);
        var invocation = await InvocationAsync();
        fault.Armed = boundary == "admission";
        if (fault.Armed)
        {
            await Assert.ThrowsAsync<IOException>(() => RequestAsync(host, invocation));
            await using var failed = await pg.Db.CreateDbContextAsync(Ct);
            Assert.Empty(await failed.WeeklyReportRequests.ToListAsync(Ct));
            Assert.Empty(await failed.Notifications.ToListAsync(Ct));
            Assert.Equal(JobRunStatus.Running, (await failed.JobRuns.SingleAsync(Ct)).Status);
            Assert.Null(await host.Services.GetRequiredService<WeeklyReportSources>().LastSlotAsync(Ct));
        }
        fault.Armed = false;
        var id = await RequestAsync(host, invocation);
        fault.Armed = boundary == "checkpoint";
        await PrepareAsync(host, id);
        Assert.Equal(1, steps.Calls);
        fault.Armed = boundary == "publication";
        await PublishAsync(host, id);
        if (boundary == "publication")
        {
            Assert.Equal(WeeklyReportStage.Publishing, (await RootAsync(id)).Stage);
            Assert.Null((await host.Services.GetRequiredService<WeeklyReportJob>().LatestAsync(Ct)).Advice);
            fault.Armed = false;
            await using var retry = await pg.Db.CreateDbContextAsync(Ct);
            await retry.WeeklyReportRequests.Where(row => row.Id == id).ExecuteUpdateAsync(update => update.SetProperty(row => row.RetryAt, (DateTimeOffset?)null), Ct);
            await PublishAsync(host, id);
        }
        Assert.Equal(WeeklyReportStage.Completed, (await RootAsync(id)).Stage);
        Assert.Equal(1, steps.Calls);
        Assert.Equal(1, fault.Failures);
        Assert.Equal(invocation.Slot, await host.Services.GetRequiredService<WeeklyReportSources>().LastSlotAsync(Ct));
    }

    [Fact]
    public async Task Advice_outage_keeps_weekly_statistics_published_and_resumes_the_same_frozen_request()
    {
        var steps = new Steps { Unavailable = true };
        var host = await HostAsync(steps);
        var invocation = await InvocationAsync();
        var id = await RequestAsync(host, invocation);
        var before = await RootAsync(id);
        await PrepareAsync(host, id);
        var paused = await RootAsync(id);
        Assert.Equal(before.InputHash, paused.InputHash);
        Assert.Equal(0, paused.Failures);
        Assert.Null(paused.ResultJson);
        Assert.NotNull(paused.RetryAt);
        Assert.NotNull((await host.Services.GetRequiredService<WeeklyReportJob>().LatestAsync(Ct)).Html);
        Assert.Equal(invocation.Slot, await host.Services.GetRequiredService<WeeklyReportSources>().LastSlotAsync(Ct));
        steps.Unavailable = false;
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        await context.WeeklyReportRequests.Where(row => row.Id == id).ExecuteUpdateAsync(update => update.SetProperty(row => row.RetryAt, (DateTimeOffset?)null), Ct);
        await PrepareAsync(host, id);
        await PublishAsync(host, id);
        Assert.Equal(WeeklyReportStage.Completed, (await RootAsync(id)).Stage);
        Assert.Equal(2, steps.Calls);
    }

    [Fact]
    public async Task Manual_weekly_report_does_not_claim_calendar_slot_and_replayed_slots_never_prepare_or_notify_twice()
    {
        var steps = new Steps();
        var host = await HostAsync(steps);
        var manual = await InvocationAsync(manual: true);
        await RequestAsync(host, manual);
        Assert.Null(await host.Services.GetRequiredService<WeeklyReportSources>().LastSlotAsync(Ct));
        var planned = await InvocationAsync();
        var id = await RequestAsync(host, planned);
        Assert.Equal(id, await RequestAsync(host, planned));
        var repeat = await InvocationAsync();
        Assert.Equal(id, await RequestAsync(host, repeat));
        Assert.Equal(0, steps.Calls);
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        Assert.Equal(2, await context.WeeklyReportRequests.CountAsync(Ct));
        Assert.Equal(2, await context.Notifications.CountAsync(Ct));
        Assert.Equal(3, await context.JobRuns.CountAsync(row => row.Status == JobRunStatus.Ok, Ct));
    }

    [Theory]
    [InlineData("candidate")]
    [InlineData("resume")]
    [InlineData("newer-report")]
    public async Task Changed_weekly_candidate_or_newer_report_retains_late_paid_output_without_overwriting_current_advice(string change)
    {
        var steps = new Steps { Release = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        var host = await HostAsync(steps);
        var id = await RequestAsync(host);
        var running = PrepareAsync(host, id);
        await steps.Entered.Task.WaitAsync(Ct);
        Guid? current = null;
        if (change == "candidate")
            await host.Services.GetRequiredService<ICandidatePublisher>().PublishProfileAsync(new CandidateProfile { Name = "Changed Candidate" }, 1, "synthetic", Ct);
        if (change == "resume")
            await File.WriteAllTextAsync(Path.Combine(host.Services.GetRequiredService<PathsOptions>().Profile, "resume.txt"), "Changed resume text", Ct);
        if (change == "newer-report") current = await RequestAsync(host, force: true);
        steps.Release.SetResult();
        await running;
        var root = await RootAsync(id);
        Assert.Equal(WeeklyReportStage.Cancelled, root.Stage);
        Assert.NotNull(root.ResultJson);
        Assert.Null((await host.Services.GetRequiredService<WeeklyReportJob>().LatestAsync(Ct)).Advice);
        if (current is { } winner)
        {
            await PrepareAsync(host, winner);
            await PublishAsync(host, winner);
            await using var context = await pg.Db.CreateDbContextAsync(Ct);
            Assert.Equal(winner.ToString("D"), (await context.Settings.SingleAsync(row => row.Key == WeeklyReportSources.AdviceOwnerKey, Ct)).Value);
        }
    }

    [Fact]
    public async Task Weekly_without_resume_or_demand_is_completed_during_admission_and_preserves_previous_unowned_advice()
    {
        var steps = new Steps();
        var host = await HostAsync(steps, resume: false);
        await using (var context = await pg.Db.CreateDbContextAsync(Ct))
        {
            context.Settings.Add(new() { Key = WeeklyReportJob.LatestAdviceKey, Value = JsonSerializer.Serialize(new HhResumeAdvice { Title = "Legacy advice" }, CareerJsonContext.Default.HhResumeAdvice), UpdatedAt = _time.GetUtcNow() });
            await context.SaveChangesAsync(Ct);
        }
        var result = await host.Services.GetRequiredService<WeeklyReportJob>().BuildAsync(Ct);
        Assert.Null(result.Advice);
        Assert.Equal(0, steps.Calls);
        Assert.Equal("Legacy advice", (await host.Services.GetRequiredService<WeeklyReportJob>().LatestAsync(Ct)).Advice!.Title);
        await using var final = await pg.Db.CreateDbContextAsync(Ct);
        Assert.Equal(WeeklyReportStage.Completed, (await final.WeeklyReportRequests.SingleAsync(Ct)).Stage);
        Assert.Equal(0, await final.Database.SqlQuery<int>($"SELECT count(*)::int AS \"Value\" FROM jobfinder_queues.wolverine_queue_preparation").SingleAsync(Ct));
    }

    [Fact]
    public async Task Saved_weekly_advice_restart_uses_the_real_listener_without_rebuilding_or_repeating_statistics()
    {
        var steps = new Steps();
        var producer = await HostAsync(steps);
        var id = await RequestAsync(producer);
        await PrepareAsync(producer, id);
        await producer.StopAsync(Ct);
        var consumer = await HostAsync(steps, consumer: true, seed: false);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        await DriveAsync(consumer.Services.GetRequiredService<WeeklyReportRequests>().WaitAsync(id, timeout.Token));
        Assert.Equal(1, steps.Calls);
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        Assert.Equal(2, await context.Notifications.CountAsync(Ct));
    }

    [Fact]
    public async Task Weekly_known_result_save_exhaustion_retains_paid_text_without_automatic_redraft_and_statistics_stay_readable()
    {
        var fault = new Fault("checkpoint") { Limit = 3 };
        var steps = new Steps();
        var host = await HostAsync(steps, fault: fault);
        var id = await RequestAsync(host);
        fault.Armed = true;
        await Assert.ThrowsAsync<WeeklyReportCheckpointException>(() => PrepareAsync(host, id));
        var root = await RootAsync(id);
        Assert.Equal(WeeklyReportStage.Failed, root.Stage);
        Assert.NotNull(root.ResultJson);
        Assert.Equal(CanonicalJson.Hash(root.ResultJson), root.ResultHash);
        await PrepareAsync(host, id);
        Assert.Equal(1, steps.Calls);
        Assert.NotNull((await host.Services.GetRequiredService<WeeklyReportJob>().LatestAsync(Ct)).Html);
    }

    [Theory]
    [InlineData("report")]
    [InlineData("advice")]
    [InlineData("cursor")]
    public async Task Damaged_weekly_native_proof_refuses_read_and_admission_instead_of_silently_repairing_it(string proof)
    {
        var host = await HostAsync(new Steps());
        var id = await RequestAsync(host, await InvocationAsync());
        await PrepareAsync(host, id);
        await PublishAsync(host, id);
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        var key = proof == "report" ? WeeklyReportJob.LatestReportKey : proof == "advice" ? WeeklyReportJob.LatestAdviceKey : WeeklyReportSources.CursorKey;
        await context.Settings.Where(row => row.Key == key).ExecuteUpdateAsync(update => update.SetProperty(row => row.Value, proof == "report" ? "tampered HTML" : "{}"), Ct);
        if (proof == "cursor") await Assert.ThrowsAsync<UnsupportedWeeklyReportException>(() => host.Services.GetRequiredService<WeeklyReportSources>().LastSlotAsync(Ct));
        else await Assert.ThrowsAsync<UnsupportedWeeklyReportException>(() => host.Services.GetRequiredService<WeeklyReportJob>().LatestAsync(Ct));
        await Assert.ThrowsAsync<UnsupportedWeeklyReportException>(() => RequestAsync(host));
        Assert.Equal(1, await context.WeeklyReportRequests.CountAsync(Ct));
    }

    [Fact]
    public async Task Actual_weekly_scheduler_preserves_atomic_source_Ok_after_post_commit_failure_and_does_not_republish_slot()
    {
        var fault = new PostCommitFault();
        var host = await HostAsync(new Steps(), fault: fault);
        var job = host.Services.GetRequiredService<WeeklyReportJob>();
        var method = typeof(ScheduledJob).GetMethod("RunRecordedAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
        await ((Task)method.Invoke(job, [Ct])!);
        Assert.True(fault.Committed);
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        var run = await context.JobRuns.SingleAsync(Ct);
        Assert.Equal(JobRunStatus.Ok, run.Status);
        Assert.NotNull(run.WeeklyReportRequestId);
        Assert.Equal(run.ScheduledSlot, await host.Services.GetRequiredService<WeeklyReportSources>().LastSlotAsync(Ct));
        Assert.Single(await context.Notifications.ToListAsync(Ct));
    }

    [Fact]
    public async Task Weekly_receipts_and_roots_survive_noise_retention_and_recovery_does_not_reclassify_native_markers()
    {
        var host = await HostAsync(new Steps());
        var invocation = await InvocationAsync(manual: true, at: _time.GetUtcNow().AddDays(-40));
        var id = await RequestAsync(host, invocation);
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        var ordinary = new JobRun { Job = "synthetic-noise", StartedAt = _time.GetUtcNow().AddDays(-40), Status = JobRunStatus.Ok };
        context.JobRuns.Add(ordinary);
        await context.SaveChangesAsync(Ct);
        await host.Services.GetRequiredService<OperationalRetention>().SweepAsync(10, Ct);
        Assert.True(await context.JobRuns.AnyAsync(row => row.Id == invocation.RunId, Ct));
        Assert.False(await context.JobRuns.AnyAsync(row => row.Id == ordinary.Id, Ct));
        await context.JobRuns.Where(row => row.Id == invocation.RunId).ExecuteUpdateAsync(update => update.SetProperty(row => row.Status, JobRunStatus.Running), Ct);
        var recovery = new StartupRecovery(pg.Db, host.Services.GetRequiredService<Notifier>(), _time, host.Services.GetRequiredService<ServerOwnership>(),
            host.Services.GetRequiredService<IHostApplicationLifetime>(), NullLogger<StartupRecovery>.Instance);
        await recovery.StartAsync(Ct);
        Assert.Equal(JobRunStatus.Running, (await context.JobRuns.AsNoTracking().SingleAsync(Ct)).Status);
        Assert.Equal(id, (await RootAsync(id)).Id);
    }

    [Fact]
    public async Task Weekly_owned_settings_refuse_generic_writers_and_legacy_report_remains_readable()
    {
        var host = await HostAsync(new Steps());
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        context.Settings.Add(new() { Key = WeeklyReportJob.LatestReportKey, Value = "Legacy report", UpdatedAt = _time.GetUtcNow() });
        await context.SaveChangesAsync(Ct);
        Assert.Equal("Legacy report", (await host.Services.GetRequiredService<WeeklyReportJob>().LatestAsync(Ct)).Html);
        var settings = host.Services.GetRequiredService<RuntimeSettings>();
        foreach (var key in new[] { WeeklyReportJob.LatestReportKey, WeeklyReportJob.LatestAdviceKey, WeeklyReportSources.OwnerKey, WeeklyReportSources.AdviceOwnerKey, WeeklyReportSources.CursorKey })
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => settings.SetAsync(key, "override", Ct));
            await Assert.ThrowsAsync<InvalidOperationException>(() => settings.RemoveAsync(key, Ct));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Weekly_downgrade_refuses_saved_statistical_receipts_or_orphaned_future_envelopes(bool orphan)
    {
        var host = await HostAsync(new Steps());
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        if (orphan)
        {
            await using var scope = host.Services.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<Wolverine.IMessageBus>().PublishAsync(new PrepareWeeklyReport { RequestId = Guid.NewGuid() });
            await context.Database.ExecuteSqlRawAsync("UPDATE jobfinder_queues.wolverine_queue_preparation SET message_type = 'jobfinder.preparation.prepare-weekly-report.v99'", Ct);
        }
        else await RequestAsync(host, await InvocationAsync());
        var error = await Assert.ThrowsAsync<Npgsql.PostgresException>(() => MigrationTestRunner.DowngradeAndRestoreAsync(context, "20261004053531_DurableRejectionReview", Ct));
        Assert.Contains("retained weekly", error.Message);
    }

    private sealed class Steps : IWeeklyReportSteps
    {
        public int Calls;
        public bool Unavailable;
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource? Release;
        public void EnsureCompatible(WeeklyReportInput input) { }
        public async Task<WeeklyReportResult> PrepareAsync(WeeklyReportInput input, CancellationToken ct)
        {
            Interlocked.Increment(ref Calls);
            Entered.TrySetResult();
            if (Release is not null) await Release.Task.WaitAsync(ct);
            if (Unavailable) throw new LlmUnavailableException("synthetic unavailable", false);
            var advice = new HhResumeAdvice { Title = "Original advice", About = "Original supported summary", Improvements = ["Supported improvement"] };
            return new() { Html = WeeklyReportSteps.Compose(input.Frame, advice), Sent = input.Frame.Sent, Advice = advice, Model = "synthetic", GeneratedAt = DateTimeOffset.UtcNow };
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
                (boundary == "admission" && context.ChangeTracker.Entries<WeeklyReportRequest>().Any(entry => entry.State == EntityState.Added)
                || boundary == "checkpoint" && context.ChangeTracker.Entries<WeeklyReportRequest>().Any(entry => entry.State == EntityState.Modified && entry.Entity.Stage == WeeklyReportStage.Publishing)
                || boundary == "publication" && context.ChangeTracker.Entries<AppSetting>().Any(entry => entry.Entity.Key == WeeklyReportJob.LatestAdviceKey)))
            {
                Failures++;
                throw new IOException("synthetic weekly boundary fault");
            }
            return ValueTask.FromResult(result);
        }
    }
    private sealed class PostCommitFault : DbTransactionInterceptor
    {
        public bool Committed;
        public override Task TransactionCommittedAsync(DbTransaction transaction, TransactionEndEventData data, CancellationToken ct = default)
        {
            if (!Committed && data.Context is JobFinderDbContext context && context.Notifications.Local.Any(notice => notice.Kind == NotificationKind.Digest))
            {
                Committed = true;
                throw new IOException("synthetic failure after weekly commit");
            }
            return Task.CompletedTask;
        }
    }
}
