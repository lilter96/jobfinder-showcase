using System.Data.Common;
using System.Reflection;
using System.Text.Json;
using System.Threading.Channels;
using JobFinder.Core.Domain;
using JobFinder.Core.Time;
using JobFinder.Data;
using JobFinder.Pipeline.Jobs;
using JobFinder.Pipeline.Notifications;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Npgsql;

namespace JobFinder.Tests.Pipeline;

public sealed class DigestPublicationIntegrationTests(PostgresFixture pg) : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private readonly ObservedTime _time = new(new DateTimeOffset(2026, 10, 5, 6, 15, 0, TimeSpan.Zero));
    private readonly DigestOptions _options = new();
    private IHost? _host;
    private DigestJob? _job;
    private LocalClock Clock => new(_time, "Europe/Minsk");
    private DateTimeOffset Slot => new(2026, 10, 5, 6, 0, 0, TimeSpan.Zero);

    public async ValueTask InitializeAsync()
    {
        Assert.SkipWhen(pg.SkipReason is not null, pg.SkipReason ?? "");
        await pg.SeedPoliciesAsync();
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        await context.EmployerMessages.ExecuteDeleteAsync(Ct);
        await context.Vacancies.ExecuteDeleteAsync(Ct);
        await context.CommentDrafts.ExecuteDeleteAsync(Ct);
        await context.JobRuns.ExecuteDeleteAsync(Ct);
        await context.LlmCalls.ExecuteDeleteAsync(Ct);
        await context.Settings.Where(row => row.Key == DigestPublications.CursorKey).ExecuteDeleteAsync(Ct);
        await NotificationAdmissionAssertions.ResetAsync(context, Ct);
    }

    public async ValueTask DisposeAsync()
    {
        if (_job is not null)
        {
            try { await _job.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(20)); }
            finally { _job.Dispose(); _job = null; }
        }
        if (_host is null) return;
        try { await _host.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(20)); }
        finally { _host.Dispose(); _host = null; }
    }

    private async Task<DigestPublications> PublicationsAsync(IInterceptor? fault = null)
    {
        _host = await PostProcessingTestHost.BuildAsync(pg, consumer: false, fault: fault, configure: services =>
            services.AddSingleton(provider => new DigestPublications(provider.GetRequiredService<IDbContextFactory<JobFinderDbContext>>(),
                provider.GetRequiredService<JobFinder.Pipeline.Messaging.NativePublicationScopes>(), provider.GetRequiredService<Notifier>(),
                _options, Clock, _time)));
        await _host.StartAsync(Ct);
        return _host.Services.GetRequiredService<DigestPublications>();
    }

    private async Task<JobInvocation> InvocationAsync(bool manual = false, DateTimeOffset? at = null)
    {
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        var run = new JobRun
        {
            Job = JobNames.Digest, StartedAt = at ?? _time.GetUtcNow(), Status = JobRunStatus.Running,
            ManualTrigger = manual, ScheduledSlot = manual ? null : Slot,
        };
        context.JobRuns.Add(run);
        await context.SaveChangesAsync(Ct);
        return new(run.Id, run.StartedAt, manual, manual ? null : Slot);
    }

    private DigestJob Job(DigestPublications publications) => new(_options, Clock, publications, new JobTriggers(), pg.Db,
        _host!.Services.GetRequiredService<Notifier>(), _time, NullLogger<DigestJob>.Instance);

    [Fact]
    public async Task Slot_window_receipt_and_native_notice_commit_once_and_survive_diagnostic_cleanup()
    {
        var publications = await PublicationsAsync();
        var invocation = await InvocationAsync();
        await publications.PublishAsync(invocation, Ct);
        await publications.PublishAsync(invocation, Ct);
        var duplicate = await InvocationAsync(at: _time.GetUtcNow().AddMinutes(1));
        Assert.Equal("слот уже опубликован", (await publications.PublishAsync(duplicate, Ct)).Message);
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        var notice = await context.Notifications.SingleAsync(Ct);
        Assert.Equal(invocation.RunId, notice.EntityId);
        Assert.Equal(Slot, await publications.LastSlotAsync(Ct));
        var cursor = await CursorAsync(context);
        Assert.Equal(notice.Id, cursor.NotificationId);
        Assert.Equal(invocation.StartedAt, cursor.PublishedAt);
        Assert.All(await context.JobRuns.ToListAsync(Ct), row => Assert.Equal(JobRunStatus.Ok, row.Status));
        await NotificationAdmissionAssertions.JoinedAsync(context, notice, Ct);
        await context.JobRuns.ExecuteDeleteAsync(Ct);
        Assert.Equal(Slot, await publications.LastSlotAsync(Ct));
        Assert.Empty(await context.NotificationDeliveryAttempts.ToListAsync(Ct));
    }

    [Fact]
    public async Task Concurrent_runs_for_one_slot_commit_only_one_notice()
    {
        var publications = await PublicationsAsync();
        var invocations = new List<JobInvocation>();
        for (var index = 0; index < 4; index++) invocations.Add(await InvocationAsync());
        var results = await Task.WhenAll(invocations.Select(invocation => publications.PublishAsync(invocation, Ct)));
        Assert.Equal(3, results.Count(row => row.Message == "слот уже опубликован"));
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        await NotificationAdmissionAssertions.JoinedAsync(context, await context.Notifications.SingleAsync(Ct), Ct);
        Assert.Equal(4, await context.JobRuns.CountAsync(row => row.Status == JobRunStatus.Ok, Ct));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Admission_or_cursor_failure_rolls_back_the_slot_window_notice_command_and_success_receipt(bool atCursor)
    {
        var fault = new PublicationFault { AtCursor = atCursor };
        var publications = await PublicationsAsync(fault);
        var invocation = await InvocationAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => publications.PublishAsync(invocation, Ct));
        Assert.True(fault.SourceSaveCompleted);
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        Assert.Equal(JobRunStatus.Running, (await context.JobRuns.SingleAsync(Ct)).Status);
        Assert.Empty(await context.Notifications.ToListAsync(Ct));
        Assert.Empty(await context.NotificationDeliveryRequests.ToListAsync(Ct));
        Assert.Null(await publications.LastSlotAsync(Ct));
        Assert.False(await context.Settings.AnyAsync(row => row.Key == DigestPublications.CursorKey, Ct));
        Assert.Equal(0, await CommandsAsync(context));
        fault.Active = false;
        await publications.PublishAsync(invocation, Ct);
        Assert.Equal(Slot, await publications.LastSlotAsync(Ct));
        Assert.Equal(JobRunStatus.Ok, (await context.JobRuns.AsNoTracking().SingleAsync(Ct)).Status);
        await NotificationAdmissionAssertions.JoinedAsync(context, await context.Notifications.SingleAsync(Ct), Ct);
    }

    [Fact]
    public async Task Separate_manual_requests_do_not_claim_a_calendar_slot_but_replaying_the_same_request_is_warm()
    {
        var publications = await PublicationsAsync();
        var manual = await InvocationAsync(manual: true);
        await publications.PublishAsync(manual, Ct);
        await publications.PublishAsync(manual, Ct);
        await publications.PublishAsync(await InvocationAsync(manual: true), Ct);
        Assert.Null(await publications.LastSlotAsync(Ct));
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        Assert.Equal(2, await context.Notifications.CountAsync(Ct));
        await publications.PublishAsync(await InvocationAsync(), Ct);
        Assert.Equal(3, await context.Notifications.CountAsync(Ct));
        Assert.Equal(Slot, await publications.LastSlotAsync(Ct));
        await publications.PublishAsync(await InvocationAsync(manual: true), Ct);
        Assert.Equal(4, await context.Notifications.CountAsync(Ct));
        Assert.Equal(Slot, await publications.LastSlotAsync(Ct));
    }

    [Fact]
    public async Task Committed_slot_survives_publisher_restart_even_when_the_diagnostic_receipt_is_wrong()
    {
        var publications = await PublicationsAsync();
        await publications.PublishAsync(await InvocationAsync(), Ct);
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        await context.JobRuns.ExecuteUpdateAsync(update => update.SetProperty(run => run.Status, JobRunStatus.Failed), Ct);
        await DisposeAsync();
        publications = await PublicationsAsync();
        Assert.Equal(Slot, await publications.LastSlotAsync(Ct));
        using var job = Job(publications);
        Assert.Null(await CatchUpAsync(job));
        Assert.Equal("слот уже опубликован", (await publications.PublishAsync(await InvocationAsync(), Ct)).Message);
        Assert.Single(await context.Notifications.ToListAsync(Ct));
    }

    [Theory]
    [InlineData(15, true)]
    [InlineData(570, false)]
    [InlineData(1321, false)]
    public async Task Catch_up_uses_the_slot_cursor_not_manual_or_legacy_Ok_receipts_and_keeps_minimum_lead(int minutesAfterSlot, bool catchUp)
    {
        _time.SetUtcNow(Slot.AddMinutes(minutesAfterSlot));
        var publications = await PublicationsAsync();
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        context.JobRuns.Add(new JobRun { Job = JobNames.Digest, StartedAt = _time.GetUtcNow(), Status = JobRunStatus.Ok });
        await context.SaveChangesAsync(Ct);
        await publications.PublishAsync(await InvocationAsync(manual: true), Ct);
        using var job = Job(publications);
        var delay = await CatchUpAsync(job);
        Assert.Equal(catchUp, delay is not null);
        if (delay is { } value) Assert.InRange(value, TimeSpan.FromSeconds(20), TimeSpan.FromSeconds(60));
        Assert.Null(await publications.LastSlotAsync(Ct));
    }

    [Theory]
    [InlineData("json")]
    [InlineData("version")]
    [InlineData("notice")]
    [InlineData("text")]
    [InlineData("root")]
    public async Task A_damaged_cursor_or_native_notice_witness_fails_closed_without_claiming_a_new_slot(string damage)
    {
        var publications = await PublicationsAsync();
        await publications.PublishAsync(await InvocationAsync(), Ct);
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        var setting = await context.Settings.SingleAsync(row => row.Key == DigestPublications.CursorKey, Ct);
        var cursor = await CursorAsync(context);
        if (damage is "json" or "version" or "notice")
        {
            if (damage == "version") cursor.Version = 999;
            if (damage == "notice") cursor.NotificationId++;
            setting.Value = damage == "json" ? "broken-json" : JsonSerializer.Serialize(cursor, DigestPublicationJsonContext.Default.DigestPublicationCursor);
        }
        else if (damage == "text") (await context.Notifications.SingleAsync(Ct)).Text = "changed notice";
        else (await context.NotificationDeliveryRequests.SingleAsync(Ct)).InputHash = "damaged";
        await context.SaveChangesAsync(Ct);
        if (damage == "root") await Assert.ThrowsAsync<UnsupportedNotificationDeliveryException>(() => publications.LastSlotAsync(Ct));
        else await Assert.ThrowsAsync<InvalidOperationException>(() => publications.LastSlotAsync(Ct));
        var invocation = await InvocationAsync(manual: true);
        if (damage == "root") await Assert.ThrowsAsync<UnsupportedNotificationDeliveryException>(() => publications.PublishAsync(invocation, Ct));
        else await Assert.ThrowsAsync<InvalidOperationException>(() => publications.PublishAsync(invocation, Ct));
        Assert.Single(await context.Notifications.ToListAsync(Ct));
        Assert.Equal(JobRunStatus.Running, (await context.JobRuns.AsNoTracking().SingleAsync(row => row.Id == invocation.RunId, Ct)).Status);
    }

    [Fact]
    public async Task Reporting_preserves_counts_cost_dry_run_and_escaped_top_vacancies()
    {
        var publications = await PublicationsAsync();
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        var vacancy = new Vacancy
        {
            Title = "Senior <.NET>", CompanyName = "Synthetic & Company", Description = "Synthetic", Fingerprint = Guid.NewGuid().ToString("N"),
            Url = "https://example.invalid/jobs/1", Status = VacancyStatus.Scored, Decision = Decision.Approval, Score = 90,
            FirstSeenAt = _time.GetUtcNow().AddMinutes(-30), UpdatedAt = _time.GetUtcNow().AddMinutes(-30),
        };
        context.Vacancies.Add(vacancy);
        context.Applications.Add(new Application { Vacancy = vacancy, Status = ApplicationStatus.DryRun, CreatedAt = _time.GetUtcNow().AddMinutes(-20) });
        context.LlmCalls.Add(new LlmCall { Purpose = "synthetic-digest", Model = "synthetic/model", At = _time.GetUtcNow().AddMinutes(-20), CostUsd = 0.123m });
        await context.SaveChangesAsync(Ct);
        Assert.Equal(1, (await publications.PublishAsync(await InvocationAsync(), Ct)).Items);
        var notice = await context.Notifications.SingleAsync(Ct);
        Assert.Contains("05.10 09:15", notice.Text);
        Assert.Contains("Новых вакансий: <b>1</b>", notice.Text);
        Assert.Contains("Dry-run держит откликов: <b>1</b>", notice.Text);
        Assert.Contains("LLM сегодня: $0.123", notice.Text);
        Assert.Contains("&lt;.NET&gt;", notice.Text);
        Assert.Contains("Synthetic &amp; Company", notice.Text);
        await NotificationAdmissionAssertions.JoinedAsync(context, notice, Ct);
    }

    [Theory]
    [InlineData("started")]
    [InlineData("job")]
    [InlineData("failed")]
    [InlineData("missing")]
    [InlineData("future-slot")]
    [InlineData("manual-slot")]
    [InlineData("saved-mode")]
    [InlineData("saved-slot")]
    [InlineData("legacy-mode")]
    public async Task Changed_run_witnesses_and_invalid_invocations_cannot_publish(string change)
    {
        var publications = await PublicationsAsync();
        var invocation = await InvocationAsync();
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        var run = await context.JobRuns.SingleAsync(Ct);
        switch (change)
        {
            case "started": run.StartedAt = run.StartedAt.AddSeconds(1); break;
            case "job": run.Job = "other-synthetic-job"; break;
            case "failed": run.Status = JobRunStatus.Failed; break;
            case "missing": invocation = invocation with { RunId = invocation.RunId + 900 }; break;
            case "future-slot": invocation = invocation with { Slot = invocation.StartedAt.AddMinutes(1) }; break;
            case "manual-slot": invocation = invocation with { Manual = true }; break;
            case "saved-mode": run.ManualTrigger = true; break;
            case "saved-slot": run.ScheduledSlot = Slot.AddSeconds(-1); break;
            case "legacy-mode": run.ManualTrigger = null; break;
        }
        await context.SaveChangesAsync(Ct);
        if (change is "future-slot" or "manual-slot")
            await Assert.ThrowsAsync<ArgumentException>(() => publications.PublishAsync(invocation, Ct));
        else await Assert.ThrowsAsync<InvalidOperationException>(() => publications.PublishAsync(invocation, Ct));
        Assert.Empty(await context.Notifications.ToListAsync(Ct));
        Assert.Empty(await context.NotificationDeliveryRequests.ToListAsync(Ct));
        Assert.Null(await publications.LastSlotAsync(Ct));
        Assert.Equal(0, await CommandsAsync(context));
    }

    [Fact]
    public async Task A_warm_manual_request_cannot_be_replayed_as_a_scheduled_slot_claim()
    {
        var publications = await PublicationsAsync();
        var invocation = await InvocationAsync(manual: true);
        await publications.PublishAsync(invocation, Ct);
        await Assert.ThrowsAsync<InvalidOperationException>(() => publications.PublishAsync(invocation with { Manual = false, Slot = Slot }, Ct));
        Assert.Null(await publications.LastSlotAsync(Ct));
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        Assert.Single(await context.Notifications.ToListAsync(Ct));
        Assert.Equal(JobRunStatus.Ok, (await context.JobRuns.SingleAsync(Ct)).Status);
    }

    [Fact]
    public async Task Migration_rollback_retains_native_slot_evidence_even_after_diagnostic_cleanup()
    {
        var publications = await PublicationsAsync();
        await publications.PublishAsync(await InvocationAsync(), Ct);
        await DisposeAsync();
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        await context.JobRuns.ExecuteDeleteAsync(Ct);
        var migrations = (await context.Database.GetAppliedMigrationsAsync(Ct)).ToArray();
        var migrationIndex = Array.FindIndex(migrations, migration => migration.EndsWith("_DigestRunWitness", StringComparison.Ordinal));
        Assert.True(migrationIndex > 0);
        try
        {
            var error = await Assert.ThrowsAsync<PostgresException>(() => MigrationTestRunner.DowngradeAndRestoreAsync(context, migrations[migrationIndex - 1], Ct));
            Assert.Contains("Cannot roll back digest run witnesses", error.MessageText);
            Assert.Equal(Slot, await publications.LastSlotAsync(Ct));
            await NotificationAdmissionAssertions.JoinedAsync(context, await context.Notifications.SingleAsync(Ct), Ct);
            Assert.Contains(migrations[migrationIndex], await context.Database.GetAppliedMigrationsAsync(Ct));
        }
        finally { await context.Database.MigrateAsync(CancellationToken.None); }
    }

    [Fact]
    public async Task Unpublished_legacy_diagnostics_allow_downgrade_and_reupgrade_without_inventing_a_manual_mode()
    {
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        context.JobRuns.Add(new JobRun { Job = JobNames.Digest, StartedAt = _time.GetUtcNow(), Status = JobRunStatus.Ok });
        await context.SaveChangesAsync(Ct);
        var migrations = (await context.Database.GetAppliedMigrationsAsync(Ct)).ToArray();
        var migrationIndex = Array.FindIndex(migrations, migration => migration.EndsWith("_DigestRunWitness", StringComparison.Ordinal));
        Assert.True(migrationIndex > 0);
        try
        {
            await context.GetService<IMigrator>().MigrateAsync(migrations[migrationIndex - 1], Ct);
            Assert.Equal(1, await context.Database.SqlQuery<int>($"SELECT count(*)::int AS \"Value\" FROM job_runs").SingleAsync(Ct));
            Assert.DoesNotContain(migrations[migrationIndex], await context.Database.GetAppliedMigrationsAsync(Ct));
        }
        finally { await context.Database.MigrateAsync(CancellationToken.None); }
        var legacy = await context.JobRuns.AsNoTracking().SingleAsync(Ct);
        Assert.Null(legacy.ManualTrigger);
        Assert.Null(legacy.ScheduledSlot);
        Assert.Equal(JobRunStatus.Ok, legacy.Status);
    }

    [Fact]
    public async Task The_actual_scheduler_distinguishes_a_manual_signal_before_the_next_regular_slot()
    {
        _time.SetUtcNow(Slot.AddMinutes(-1));
        var publications = await PublicationsAsync();
        var triggers = new JobTriggers();
        _job = new(_options, Clock, publications, triggers, pg.Db, _host!.Services.GetRequiredService<Notifier>(), _time, NullLogger<DigestJob>.Instance);
        await _job.StartAsync(Ct);
        Assert.Equal(TimeSpan.FromMinutes(1), await _time.NextTimerAsync(Ct));
        Assert.True(triggers.Trigger(JobNames.Digest));
        await WaitNoticesAsync(1);
        Assert.Equal(TimeSpan.FromMinutes(1), await _time.NextTimerAsync(Ct));
        Assert.Null(await publications.LastSlotAsync(Ct));
        _time.Advance(TimeSpan.FromMinutes(1));
        await WaitNoticesAsync(2);
        Assert.Equal(TimeSpan.FromHours(10), await _time.NextTimerAsync(Ct));
        Assert.Equal(Slot, await publications.LastSlotAsync(Ct));
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        Assert.Equal(2, await context.JobRuns.CountAsync(run => run.Status == JobRunStatus.Ok, Ct));
        foreach (var notice in await context.Notifications.ToListAsync(Ct)) await NotificationAdmissionAssertions.JoinedAsync(context, notice, Ct);
    }

    [Fact]
    public async Task The_actual_scheduler_can_retry_a_failed_manual_publication_without_consuming_the_window_or_slot()
    {
        _time.SetUtcNow(Slot.AddMinutes(-1));
        var fault = new PublicationFault();
        var publications = await PublicationsAsync(fault);
        var triggers = new JobTriggers();
        _job = new(_options, Clock, publications, triggers, pg.Db, _host!.Services.GetRequiredService<Notifier>(), _time, NullLogger<DigestJob>.Instance);
        await _job.StartAsync(Ct);
        await _time.NextTimerAsync(Ct);
        Assert.True(triggers.Trigger(JobNames.Digest));
        await _time.NextTimerAsync(Ct);
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        Assert.Equal(JobRunStatus.Failed, (await context.JobRuns.SingleAsync(Ct)).Status);
        Assert.Empty(await context.Notifications.ToListAsync(Ct));
        Assert.Null(await publications.LastSlotAsync(Ct));
        fault.Active = false;
        Assert.True(triggers.Trigger(JobNames.Digest));
        await WaitNoticesAsync(1);
        await _time.NextTimerAsync(Ct);
        Assert.Equal(1, await context.JobRuns.CountAsync(run => run.Status == JobRunStatus.Ok, Ct));
        Assert.Equal(1, await context.JobRuns.CountAsync(run => run.Status == JobRunStatus.Failed, Ct));
        Assert.Null(await publications.LastSlotAsync(Ct));
        await NotificationAdmissionAssertions.JoinedAsync(context, await context.Notifications.SingleAsync(Ct), Ct);
    }

    [Fact]
    public async Task A_post_commit_failure_cannot_overwrite_the_sources_Ok_receipt_or_republish_its_slot()
    {
        var fault = new PostCommitFault();
        var publications = await PublicationsAsync(fault);
        using var job = Job(publications);
        var method = typeof(ScheduledJob).GetMethod("RunRecordedAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
        await ((Task)method.Invoke(job, [Ct])!).WaitAsync(TimeSpan.FromSeconds(30), Ct);
        Assert.True(fault.Committed);
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        Assert.Equal(JobRunStatus.Ok, (await context.JobRuns.SingleAsync(Ct)).Status);
        Assert.Equal(Slot, await publications.LastSlotAsync(Ct));
        await NotificationAdmissionAssertions.JoinedAsync(context, await context.Notifications.SingleAsync(Ct), Ct);
        Assert.True(await CommandsAsync(context) > 0);
        Assert.Equal("слот уже опубликован", (await publications.PublishAsync(await InvocationAsync(), Ct)).Message);
        Assert.Single(await context.Notifications.ToListAsync(Ct));
        Assert.Empty(await context.NotificationDeliveryAttempts.ToListAsync(Ct));
    }

    private async Task WaitNoticesAsync(int count)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(20);
        while (DateTimeOffset.UtcNow < deadline)
        {
            await using var context = await pg.Db.CreateDbContextAsync(Ct);
            if (await context.Notifications.CountAsync(Ct) == count) return;
            await Task.Delay(TimeSpan.FromMilliseconds(20), Ct);
        }
        Assert.Fail("digest publication did not finish");
    }

    private static Task<TimeSpan?> CatchUpAsync(DigestJob job) => (Task<TimeSpan?>)typeof(ScheduledJob)
        .GetMethod("CatchUpAsync", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(job, [Ct])!;

    private static async Task<DigestPublicationCursor> CursorAsync(JobFinderDbContext context) => JsonSerializer.Deserialize(
        (await context.Settings.AsNoTracking().SingleAsync(row => row.Key == DigestPublications.CursorKey, Ct)).Value,
        DigestPublicationJsonContext.Default.DigestPublicationCursor)!;

    private static Task<int> CommandsAsync(JobFinderDbContext context) => context.Database.SqlQuery<int>($"""
        SELECT ((SELECT count(*) FROM jobfinder_queues.wolverine_queue_notifications) +
          (SELECT count(*) FROM jobfinder_messages.wolverine_outgoing_envelopes WHERE message_type LIKE 'jobfinder.notifications.%'))::int AS "Value"
        """).SingleAsync(Ct);

    private sealed class PublicationFault : SaveChangesInterceptor
    {
        public bool Active { get; set; } = true;
        public bool AtCursor { get; set; }
        public bool SourceSaveCompleted { get; private set; }

        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (Active && eventData.Context is JobFinderDbContext context && (AtCursor
                ? context.ChangeTracker.Entries<AppSetting>().Any(entry => entry.Entity.Key == DigestPublications.CursorKey && entry.State is EntityState.Added or EntityState.Modified)
                : context.ChangeTracker.Entries<NotificationDeliveryRequest>().Any(entry => entry.State == EntityState.Added)))
            {
                SourceSaveCompleted = context.ChangeTracker.Entries<JobRun>().Any(entry => entry.State == EntityState.Unchanged && entry.Entity.Status == JobRunStatus.Ok)
                    && context.ChangeTracker.Entries<Notification>().Any(entry => entry.State is EntityState.Unchanged or EntityState.Modified && entry.Entity.Id > 0);
                throw new InvalidOperationException("synthetic digest publication failure");
            }
            return ValueTask.FromResult(result);
        }
    }

    private sealed class PostCommitFault : DbTransactionInterceptor
    {
        public bool Committed { get; private set; }
        public override Task TransactionCommittedAsync(DbTransaction transaction, TransactionEndEventData eventData, CancellationToken cancellationToken = default)
        {
            if (!Committed && eventData.Context is JobFinderDbContext context && context.Notifications.Local.Any(notice => notice.Kind == NotificationKind.Digest))
            {
                Committed = true;
                throw new InvalidOperationException("synthetic failure after digest commit");
            }
            return Task.CompletedTask;
        }
    }

    private sealed class ObservedTime(DateTimeOffset now) : TimeProvider
    {
        private FakeTimeProvider _time = new(now);
        private bool _startedTimers;
        private readonly Channel<TimeSpan> _timers = Channel.CreateUnbounded<TimeSpan>();
        public override DateTimeOffset GetUtcNow() => _time.GetUtcNow();
        public void Advance(TimeSpan delay) => _time.Advance(delay);
        public void SetUtcNow(DateTimeOffset value)
        {
            Assert.False(_startedTimers);
            _time = new(value);
        }
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            _startedTimers = true;
            var timer = _time.CreateTimer(callback, state, dueTime, period);
            Assert.True(_timers.Writer.TryWrite(dueTime));
            return timer;
        }
        public Task<TimeSpan> NextTimerAsync(CancellationToken ct) => _timers.Reader.ReadAsync(ct).AsTask().WaitAsync(TimeSpan.FromSeconds(20), ct);
    }
}
