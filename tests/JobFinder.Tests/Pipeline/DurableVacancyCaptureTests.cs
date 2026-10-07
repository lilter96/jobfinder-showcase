using System.Data.Common;
using System.Text;
using JobFinder.Core.Domain;
using JobFinder.Data;
using JobFinder.Integrations.HeadHunter;
using JobFinder.Pipeline;
using JobFinder.Pipeline.Actions;
using JobFinder.Pipeline.Ingest;
using JobFinder.Pipeline.Modules.Candidate;
using JobFinder.Pipeline.Modules.Discovery;
using JobFinder.Pipeline.Profile;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Wolverine;

namespace JobFinder.Tests.Pipeline;

public sealed partial class DurableVacancyAssessmentIntegrationTests
{
    private static HhVacancyDetail CaptureHh(long id = 900111, bool closed = false) => new(id, "rabota.by", "Synthetic Senior .NET Developer", 987654,
        "Synthetic Company", "Minsk", "BY", WorkFormat.Remote, 4500, 5000, "USD", false, EnglishLevel.Unknown,
        "between3And6", ["C#"], "Develop synthetic C# services.", closed, DateTimeOffset.UtcNow,
        $"https://rabota.by/vacancy/{id}", "{}");

    private async Task<CaptureVacancyAssessment> CaptureAsync(IHost host, long id)
    {
        var capture = await host.Services.GetRequiredService<VacancyAssessmentCapture>().RequestAsync(id, Ct);
        Assert.NotNull(capture);
        return new CaptureVacancyAssessment { VacancyId = id, CaptureId = capture.Value, Revision = (await VacancyAsync(id)).AssessmentRevision };
    }

    private async Task<int> CapturePendingAsync()
    {
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        return await context.Database.SqlQuery<int>($"SELECT count(*)::int AS \"Value\" FROM jobfinder_queues.wolverine_queue_preparation").SingleAsync(Ct);
    }

    private async Task<List<StoredEnvelope>> ScheduledCaptureAsync(Guid captureId)
    {
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        var bytes = Encoding.UTF8.GetBytes(captureId.ToString());
        return await context.Database.SqlQuery<StoredEnvelope>($"""
            SELECT message_type, body, execution_time
            FROM jobfinder_queues.wolverine_queue_preparation_scheduled WHERE position({bytes} in body) > 0
            UNION ALL
            SELECT message_type, body, execution_time
            FROM jobfinder_messages.wolverine_incoming_envelopes WHERE status = 'Scheduled' AND position({bytes} in body) > 0
            """).ToListAsync(Ct);
    }

    [Fact]
    public async Task Capture_actual_ingest_commits_source_intent_and_command_before_producer_exit()
    {
        var steps = new Steps();
        var producer = await HostAsync(false, steps);
        var ingestor = producer.Services.GetRequiredService<VacancyIngestor>();
        var result = await ingestor.IngestHhAsync(CaptureHh(), null, null, Ct);
        var vacancy = await VacancyAsync(result.VacancyId);
        Assert.NotNull(vacancy.AssessmentCaptureId);
        Assert.Equal(1, await CapturePendingAsync());
        Assert.Empty(steps.Calls);
        await using (var context = await pg.Db.CreateDbContextAsync(Ct))
        {
            Assert.Single(await context.VacancySources.ToListAsync(Ct));
            Assert.Empty(await context.VacancyAssessmentRequests.ToListAsync(Ct));
        }
        await StopAsync(producer);
        await HostAsync(true, steps);
        await UntilAsync(async () => (await VacancyAsync(vacancy.Id)).Status == VacancyStatus.Scored);
        Assert.Null((await VacancyAsync(vacancy.Id)).AssessmentCaptureId);
        Assert.Equal(2, steps.Calls.Count);
        await AssertExactCardAsync(vacancy.Id);
        await AssertNoApplicationsAsync();
    }

    [Fact]
    public async Task Capture_failed_ingest_commit_leaves_no_source_company_intent_or_command()
    {
        var fault = new CaptureCommitFault();
        var producer = await HostAsync(false, new Steps(), fault);
        await using var before = await pg.Db.CreateDbContextAsync(Ct);
        var companies = await before.Companies.CountAsync(Ct);
        await Assert.ThrowsAsync<IOException>(() => producer.Services.GetRequiredService<VacancyIngestor>().IngestHhAsync(CaptureHh(), null, null, Ct));
        Assert.True(fault.Fired);
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        Assert.Empty(await context.Vacancies.ToListAsync(Ct));
        Assert.Empty(await context.VacancySources.ToListAsync(Ct));
        Assert.Equal(companies, await context.Companies.CountAsync(Ct));
        Assert.Equal(0, await CapturePendingAsync());
        Assert.Empty(await context.VacancyAssessmentRequests.ToListAsync(Ct));
    }

    [Fact]
    public async Task Capture_repeated_source_and_concurrent_imports_keep_one_pending_intent()
    {
        var producer = await HostAsync(false, new Steps());
        var ingestor = producer.Services.GetRequiredService<VacancyIngestor>();
        var first = await ingestor.IngestHhAsync(CaptureHh(), null, null, Ct);
        var token = (await VacancyAsync(first.VacancyId)).AssessmentCaptureId;
        var again = await ingestor.IngestHhAsync(CaptureHh(), null, null, Ct);
        Assert.False(again.Created);
        var ids = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => producer.Services.GetRequiredService<VacancyAssessmentCapture>().RequestAsync(first.VacancyId, Ct)));
        Assert.All(ids, id => Assert.Equal(token, id));
        Assert.Equal(1, await CapturePendingAsync());
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        Assert.Single(await context.VacancySources.ToListAsync(Ct));
    }

    [Fact]
    public async Task Capture_closed_ingest_does_not_publish_assessment_work()
    {
        var producer = await HostAsync(false, new Steps());
        var result = await producer.Services.GetRequiredService<VacancyIngestor>().IngestHhAsync(CaptureHh(closed: true), null, null, Ct);
        Assert.Equal(VacancyStatus.Rejected, (await VacancyAsync(result.VacancyId)).Status);
        Assert.Null((await VacancyAsync(result.VacancyId)).AssessmentCaptureId);
        Assert.Equal(0, await CapturePendingAsync());
    }

    [Fact]
    public async Task Capture_duplicate_native_commands_publish_one_business_assessment()
    {
        var steps = new Steps();
        var hold = Barrier();
        steps.OnStage = (stage, _, ct) => stage == VacancyAssessmentStage.Scoring ? hold.WaitAsync(ct) : Task.CompletedTask;
        var producer = await HostAsync(false, steps);
        var vacancy = await SeedAsync();
        var command = await CaptureAsync(producer, vacancy.Id);
        await producer.Services.GetRequiredService<IMessageBus>().PublishAsync(command);
        await producer.Services.GetRequiredService<IMessageBus>().PublishAsync(command);
        await StopAsync(producer);
        await HostAsync(true, steps);
        await hold.Entered.Task.WaitAsync(Timeout, Ct);
        await using (var context = await pg.Db.CreateDbContextAsync(Ct))
            Assert.Single(await context.VacancyAssessmentRequests.ToListAsync(Ct));
        Assert.Single(steps.Calls);
        Assert.Null((await VacancyAsync(vacancy.Id)).AssessmentCaptureId);
        hold.Release.TrySetResult();
        await UntilAsync(async () => (await VacancyAsync(vacancy.Id)).Status == VacancyStatus.Scored);
    }

    [Theory]
    [InlineData(ApplicationStatus.Pending)]
    [InlineData(ApplicationStatus.DryRun)]
    [InlineData(ApplicationStatus.Sending)]
    [InlineData(ApplicationStatus.OutcomeUnknown)]
    public async Task Capture_unresolved_delivery_is_not_queued(ApplicationStatus state)
    {
        var producer = await HostAsync(false, new Steps());
        var vacancy = await SeedAsync();
        await using (var context = await pg.Db.CreateDbContextAsync(Ct))
        {
            context.Applications.Add(new Application { VacancyId = vacancy.Id, Status = state, CreatedAt = DateTimeOffset.UtcNow });
            await context.SaveChangesAsync(Ct);
        }
        Assert.Null(await producer.Services.GetRequiredService<VacancyAssessmentCapture>().RequestAsync(vacancy.Id, Ct));
        Assert.Null((await VacancyAsync(vacancy.Id)).AssessmentCaptureId);
        Assert.Equal(0, await CapturePendingAsync());
    }

    [Fact]
    public async Task Capture_delivery_becoming_unresolved_after_queueing_retires_without_paid_work()
    {
        var producer = await HostAsync(false, new Steps());
        var vacancy = await SeedAsync();
        await CaptureAsync(producer, vacancy.Id);
        await using (var context = await pg.Db.CreateDbContextAsync(Ct))
        {
            context.Applications.Add(new Application { VacancyId = vacancy.Id, Status = ApplicationStatus.OutcomeUnknown, CreatedAt = DateTimeOffset.UtcNow });
            await context.SaveChangesAsync(Ct);
        }
        await StopAsync(producer);
        var steps = new Steps();
        await HostAsync(true, steps);
        await UntilAsync(async () => (await VacancyAsync(vacancy.Id)).AssessmentCaptureId is null);
        Assert.Empty(steps.Calls);
        await using var read = await pg.Db.CreateDbContextAsync(Ct);
        Assert.Empty(await read.VacancyAssessmentRequests.ToListAsync(Ct));
    }

    [Fact]
    public async Task Capture_manual_rescore_commits_replacement_command_and_rejects_the_old_generation()
    {
        var producer = await HostAsync(false, new Steps());
        var vacancy = await SeedAsync();
        var old = await CaptureAsync(producer, vacancy.Id);
        var result = await producer.Services.GetRequiredService<ApprovalService>().RescoreAsync(vacancy.Id, Ct);
        Assert.True(result.Ok, result.Message);
        var current = await VacancyAsync(vacancy.Id);
        Assert.Equal(1, current.AssessmentRevision);
        Assert.NotNull(current.AssessmentCaptureId);
        Assert.NotEqual(old.CaptureId, current.AssessmentCaptureId);
        Assert.Equal(2, await CapturePendingAsync());
        await StopAsync(producer);
        var steps = new Steps();
        await HostAsync(true, steps);
        await UntilAsync(async () => (await VacancyAsync(vacancy.Id)).Status == VacancyStatus.Scored);
        Assert.All(steps.Calls, call => Assert.Equal(1, call.Input.Vacancy.Revision));
        Assert.Equal(2, steps.Calls.Count);
        Assert.Null((await VacancyAsync(vacancy.Id)).AssessmentCaptureId);
    }

    [Fact]
    public async Task Capture_manual_rescore_commit_failure_preserves_the_old_generation_and_has_no_new_command()
    {
        var vacancy = await SeedAsync();
        var producer = await HostAsync(false, new Steps(), new CaptureCommitFault());
        var old = await EnqueueAsync(producer, vacancy.Id);
        await Assert.ThrowsAsync<IOException>(() => producer.Services.GetRequiredService<ApprovalService>().RescoreAsync(vacancy.Id, Ct));
        Assert.Equal(0, (await VacancyAsync(vacancy.Id)).AssessmentRevision);
        Assert.Null((await VacancyAsync(vacancy.Id)).AssessmentCaptureId);
        Assert.Equal(VacancyAssessmentStage.Scoring, (await RequestAsync(old)).Stage);
        Assert.Equal(0, await CapturePendingAsync());
    }

    [Fact]
    public async Task Capture_missing_reviewed_profile_defers_the_intent_without_business_attempts()
    {
        var producer = await HostAsync(false, new Steps());
        var vacancy = await SeedAsync();
        var command = await CaptureAsync(producer, vacancy.Id);
        var profile = (await producer.Services.GetRequiredService<ProfileProvider>().GetSnapshotAsync(Ct))!;
        profile.Value.Reviewed = false;
        await producer.Services.GetRequiredService<ICandidatePublisher>().PublishProfileAsync(profile.Value, profile.Version, "synthetic-test", Ct);
        await StopAsync(producer);
        var steps = new Steps();
        await HostAsync(true, steps);
        await UntilAsync(async () => (await ScheduledCaptureAsync(command.CaptureId)).Count == 1);
        var scheduled = Assert.Single(await ScheduledCaptureAsync(command.CaptureId));
        Assert.Equal("jobfinder.discovery.vacancy-capture.v1", scheduled.MessageType);
        Assert.True(scheduled.ExecutionTime > DateTimeOffset.UtcNow.AddMinutes(1));
        Assert.Equal(0, (await VacancyAsync(vacancy.Id)).Attempts);
        Assert.NotNull((await VacancyAsync(vacancy.Id)).AssessmentCaptureId);
        Assert.Empty(steps.Calls);
    }

    [Fact]
    public async Task Capture_future_command_is_quarantined_without_releasing_a_different_intent()
    {
        var producer = await HostAsync(false, new Steps());
        var vacancy = await SeedAsync();
        var owned = await CaptureAsync(producer, vacancy.Id);
        await StopAsync(producer);
        using var host = await BuildHostAsync(true, new Steps());
        host.Services.GetRequiredService<ServerOwnership>().Acquired();
        var command = new CaptureVacancyAssessment { VacancyId = vacancy.Id, CaptureId = Guid.NewGuid(), Version = 2 };
        await Assert.ThrowsAsync<UnsupportedVacancyAssessmentException>(() => host.Services.GetRequiredService<VacancyCaptureWorkflow>().RunAsync(command, Ct));
        Assert.Equal(owned.CaptureId, (await VacancyAsync(vacancy.Id)).AssessmentCaptureId);
        Assert.Empty(await CardsAsync());
    }

    [Fact]
    public async Task Capture_assessment_commit_fault_preserves_intent_and_retries_without_paid_work()
    {
        var fault = new CaptureAssessmentFault(false);
        var steps = new Steps();
        var producer = await HostAsync(false, steps, fault);
        producer.Services.GetRequiredService<ServerOwnership>().Acquired();
        var vacancy = await SeedAsync();
        var command = await CaptureAsync(producer, vacancy.Id);
        var workflow = producer.Services.GetRequiredService<VacancyCaptureWorkflow>();
        await workflow.RunAsync(command, Ct);
        Assert.True(fault.Fired);
        Assert.Equal(command.CaptureId, (await VacancyAsync(vacancy.Id)).AssessmentCaptureId);
        Assert.Equal(1, (await VacancyAsync(vacancy.Id)).Attempts);
        Assert.Single(await ScheduledCaptureAsync(command.CaptureId));
        await using (var context = await pg.Db.CreateDbContextAsync(Ct))
        {
            Assert.Empty(await context.VacancyAssessmentRequests.ToListAsync(Ct));
            Assert.Equal(0, await context.Database.SqlQuery<int>($"SELECT count(*)::int AS \"Value\" FROM jobfinder_queues.wolverine_queue_scoring").SingleAsync(Ct));
        }
        Assert.Empty(steps.Calls);
        await workflow.RunAsync(command, Ct);
        Assert.Null((await VacancyAsync(vacancy.Id)).AssessmentCaptureId);
        await StopAsync(producer);
        await HostAsync(true, steps);
        await UntilAsync(async () => (await VacancyAsync(vacancy.Id)).Status == VacancyStatus.Scored);
        Assert.Equal(2, steps.Calls.Count);
        await AssertExactCardAsync(vacancy.Id);
    }

    [Fact]
    public async Task Capture_four_failures_retire_the_owned_intent_with_one_atomic_alert()
    {
        var steps = new Steps();
        var producer = await HostAsync(false, steps, new CaptureAssessmentFault(true));
        producer.Services.GetRequiredService<ServerOwnership>().Acquired();
        var vacancy = await SeedAsync();
        var command = await CaptureAsync(producer, vacancy.Id);
        var workflow = producer.Services.GetRequiredService<VacancyCaptureWorkflow>();
        for (var attempt = 1; attempt <= 4; attempt++)
        {
            await workflow.RunAsync(command, Ct);
            Assert.Equal(attempt, (await VacancyAsync(vacancy.Id)).Attempts);
        }
        await workflow.RunAsync(command, Ct);
        var failed = await VacancyAsync(vacancy.Id);
        Assert.Equal(VacancyStatus.Failed, failed.Status);
        Assert.Null(failed.AssessmentCaptureId);
        Assert.Contains("synthetic assessment capture", failed.LastError!);
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        Assert.Equal(1, await context.Notifications.CountAsync(row => row.Kind == NotificationKind.Alert && row.EntityId == vacancy.Id, Ct));
        Assert.Empty(await context.VacancyAssessmentRequests.ToListAsync(Ct));
        Assert.Empty(steps.Calls);
    }

    [Fact]
    public async Task Capture_full_admission_defers_only_the_waiting_intent_and_releases_slots()
    {
        var steps = new Steps();
        var hold = Barrier();
        steps.OnStage = (stage, _, ct) => stage == VacancyAssessmentStage.Scoring ? hold.WaitAsync(ct) : Task.CompletedTask;
        var host = await HostAsync(true, steps);
        var first = await SeedAsync();
        var second = await SeedAsync();
        var third = await SeedAsync();
        await CaptureAsync(host, first.Id);
        await CaptureAsync(host, second.Id);
        await UntilAsync(() => Task.FromResult(steps.Calls.Count == 2));
        var waiting = await CaptureAsync(host, third.Id);
        await UntilAsync(async () => (await ScheduledCaptureAsync(waiting.CaptureId)).Count > 0);
        await using (var context = await pg.Db.CreateDbContextAsync(Ct))
            Assert.Equal(2, await context.VacancyAssessmentRequests.CountAsync(Ct));
        Assert.Equal(0, (await VacancyAsync(third.Id)).Attempts);
        hold.Release.TrySetResult();
        await UntilAsync(async () => (await VacancyAsync(third.Id)).Status == VacancyStatus.Scored);
        Assert.Equal(6, steps.Calls.Count);
        Assert.All(steps.Calls.GroupBy(call => call.Input.Vacancy.Id), calls => Assert.Equal(2, calls.Count()));
        Assert.Null((await VacancyAsync(third.Id)).AssessmentCaptureId);
    }

    [Fact]
    public async Task Capture_source_change_before_consumption_uses_current_source_not_the_ingest_placeholder()
    {
        var steps = new Steps();
        var producer = await HostAsync(false, steps);
        var vacancy = await SeedAsync();
        await CaptureAsync(producer, vacancy.Id);
        await using (var context = await pg.Db.CreateDbContextAsync(Ct))
            await context.Vacancies.Where(row => row.Id == vacancy.Id).ExecuteUpdateAsync(update => update.SetProperty(row => row.Description, "Updated synthetic PostgreSQL services."), Ct);
        await StopAsync(producer);
        await HostAsync(true, steps);
        await UntilAsync(async () => (await VacancyAsync(vacancy.Id)).Status == VacancyStatus.Scored);
        Assert.All(steps.Calls, call => Assert.Equal("Updated synthetic PostgreSQL services.", call.Input.Vacancy.Description));
    }

    [Fact]
    public async Task Capture_future_owned_command_releases_only_its_intent_and_is_natively_dead_lettered()
    {
        var steps = new Steps();
        var producer = await HostAsync(false, steps);
        var vacancy = await SeedAsync();
        var token = Guid.NewGuid();
        await using (var context = await pg.Db.CreateDbContextAsync(Ct))
            await context.Vacancies.Where(row => row.Id == vacancy.Id).ExecuteUpdateAsync(update => update.SetProperty(row => row.AssessmentCaptureId, token), Ct);
        await producer.Services.GetRequiredService<IMessageBus>().PublishAsync(new CaptureVacancyAssessment { VacancyId = vacancy.Id, CaptureId = token, Version = 2 });
        await StopAsync(producer);
        var host = await HostAsync(true, steps);
        await UntilAsync(async () =>
        {
            await using var context = await pg.Db.CreateDbContextAsync(Ct);
            return await context.Database.SqlQuery<int>($"SELECT count(*)::int AS \"Value\" FROM jobfinder_messages.wolverine_dead_letters").SingleAsync(Ct) == 1;
        });
        Assert.Null((await VacancyAsync(vacancy.Id)).AssessmentCaptureId);
        Assert.Empty(steps.Calls);
        await using (var context = await pg.Db.CreateDbContextAsync(Ct))
            Assert.Equal(1, await context.Notifications.CountAsync(row => row.Kind == NotificationKind.Alert && row.EntityId == vacancy.Id, Ct));
        await CaptureAsync(host, vacancy.Id);
        await UntilAsync(async () => (await VacancyAsync(vacancy.Id)).Status == VacancyStatus.Scored);
        Assert.Equal(2, steps.Calls.Count);
    }

    [Fact]
    public async Task Capture_ownership_loss_leaves_the_intent_for_the_next_owner_without_publication()
    {
        var steps = new Steps();
        var producer = await HostAsync(false, steps);
        var vacancy = await SeedAsync();
        var command = await CaptureAsync(producer, vacancy.Id);
        producer.Services.GetRequiredService<ServerOwnership>().Lost();
        await Assert.ThrowsAsync<InvalidOperationException>(() => producer.Services.GetRequiredService<VacancyCaptureWorkflow>().RunAsync(command, Ct));
        Assert.Equal(command.CaptureId, (await VacancyAsync(vacancy.Id)).AssessmentCaptureId);
        Assert.Equal(1, await CapturePendingAsync());
        Assert.Empty(steps.Calls);
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        Assert.Empty(await context.VacancyAssessmentRequests.ToListAsync(Ct));
    }

    [Fact]
    public async Task Capture_ownership_lost_during_snapshot_fencing_aborts_the_handoff_transaction()
    {
        var steps = new Steps();
        var fault = new CaptureOwnershipFault();
        var producer = await HostAsync(false, steps, fault);
        fault.Ownership = producer.Services.GetRequiredService<ServerOwnership>();
        fault.Ownership.Acquired();
        var vacancy = await SeedAsync();
        var command = await CaptureAsync(producer, vacancy.Id);
        await Assert.ThrowsAsync<InvalidOperationException>(() => producer.Services.GetRequiredService<VacancyCaptureWorkflow>().RunAsync(command, Ct));
        Assert.False(fault.Ownership.IsOwned);
        Assert.Equal(command.CaptureId, (await VacancyAsync(vacancy.Id)).AssessmentCaptureId);
        Assert.Equal(0, (await VacancyAsync(vacancy.Id)).Attempts);
        Assert.Equal(1, await CapturePendingAsync());
        Assert.Empty(steps.Calls);
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        Assert.Empty(await context.VacancyAssessmentRequests.ToListAsync(Ct));
    }

    [Fact]
    public async Task Capture_source_handoff_requires_an_existing_transaction()
    {
        var host = await HostAsync(false, new Steps());
        var vacancy = await SeedAsync();
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        await Assert.ThrowsAsync<InvalidOperationException>(() => host.Services.GetRequiredService<VacancyAssessmentCapture>().SaveAsync(context, vacancy, Ct));
        Assert.Null((await VacancyAsync(vacancy.Id)).AssessmentCaptureId);
        Assert.Equal(0, await CapturePendingAsync());
    }

    [Fact]
    public async Task Capture_source_handoff_refuses_a_detached_vacancy_even_with_a_transaction()
    {
        var host = await HostAsync(false, new Steps());
        var vacancy = await SeedAsync();
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        await using var transaction = await context.Database.BeginTransactionAsync(Ct);
        await Assert.ThrowsAsync<InvalidOperationException>(() => host.Services.GetRequiredService<VacancyAssessmentCapture>().SaveAsync(context, vacancy, Ct));
        Assert.Null((await VacancyAsync(vacancy.Id)).AssessmentCaptureId);
        Assert.Equal(0, await CapturePendingAsync());
    }

    [Fact]
    public async Task Capture_guarded_downgrade_refuses_a_retired_intents_still_queued_command()
    {
        var host = await HostAsync(false, new Steps());
        var vacancy = await SeedAsync();
        await CaptureAsync(host, vacancy.Id);
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        await context.Vacancies.Where(row => row.Id == vacancy.Id).ExecuteUpdateAsync(update => update.SetProperty(row => row.AssessmentCaptureId, (Guid?)null), Ct);
        try
        {
            await Assert.ThrowsAsync<Npgsql.PostgresException>(() => MigrationTestRunner.DowngradeAndRestoreAsync(context, "20260930182958_DurableLetterPreparation", Ct));
            Assert.Null(await context.Database.SqlQuery<Guid?>($"SELECT assessment_capture_id AS \"Value\" FROM vacancies WHERE id = {vacancy.Id}").SingleAsync(Ct));
            Assert.Equal(1, await CapturePendingAsync());
            Assert.Contains("20260930193511_AtomicVacancyCapture", await context.Database.GetAppliedMigrationsAsync(Ct));
        }
        finally { await context.Database.MigrateAsync(CancellationToken.None); }
        Assert.Null((await VacancyAsync(vacancy.Id)).AssessmentCaptureId);
    }

    [Fact]
    public async Task Capture_guarded_downgrade_refuses_live_intents_without_losing_them()
    {
        var host = await HostAsync(false, new Steps());
        var vacancy = await SeedAsync();
        var command = await CaptureAsync(host, vacancy.Id);
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        try
        {
            await Assert.ThrowsAsync<Npgsql.PostgresException>(() => MigrationTestRunner.DowngradeAndRestoreAsync(context, "20260930182958_DurableLetterPreparation", Ct));
            Assert.Equal(command.CaptureId, await context.Database.SqlQuery<Guid?>($"SELECT assessment_capture_id AS \"Value\" FROM vacancies WHERE id = {vacancy.Id}").SingleAsync(Ct));
            Assert.Equal(1, await CapturePendingAsync());
            Assert.Contains("20260930193511_AtomicVacancyCapture", await context.Database.GetAppliedMigrationsAsync(Ct));
        }
        finally { await context.Database.MigrateAsync(CancellationToken.None); }
        Assert.Equal(command.CaptureId, (await VacancyAsync(vacancy.Id)).AssessmentCaptureId);
    }

    private sealed class CaptureCommitFault : DbTransactionInterceptor
    {
        private int _armed = 1;
        public bool Fired { get; private set; }
        public override ValueTask<InterceptionResult> TransactionCommittingAsync(DbTransaction transaction, TransactionEventData eventData,
            InterceptionResult result, CancellationToken ct = default)
        {
            if (eventData.Context?.ChangeTracker.Entries<Vacancy>().Any(entry => entry.Entity.AssessmentCaptureId is not null) == true
                && Interlocked.Exchange(ref _armed, 0) == 1)
            {
                Fired = true;
                throw new IOException("synthetic atomic source/capture commit fault");
            }
            return ValueTask.FromResult(result);
        }
    }

    private sealed class CaptureAssessmentFault(bool persistent) : DbTransactionInterceptor
    {
        private int _armed = 1;
        public bool Fired { get; private set; }

        public override ValueTask<InterceptionResult> TransactionCommittingAsync(DbTransaction transaction, TransactionEventData eventData,
            InterceptionResult result, CancellationToken ct = default)
        {
            if (eventData.Context?.ChangeTracker.Entries<VacancyAssessmentRequest>().Any(entry => entry.Entity.Stage == VacancyAssessmentStage.Scoring) == true
                && (persistent || Interlocked.Exchange(ref _armed, 0) == 1))
            {
                Fired = true;
                throw new IOException("synthetic assessment capture commit fault");
            }
            return ValueTask.FromResult(result);
        }
    }

    private sealed class CaptureOwnershipFault : DbCommandInterceptor
    {
        public ServerOwnership? Ownership { get; set; }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData,
            InterceptionResult<DbDataReader> result, CancellationToken ct = default)
        {
            if (command.CommandText.Contains("app_documents", StringComparison.Ordinal) && command.CommandText.Contains("FOR SHARE", StringComparison.Ordinal))
                Ownership?.Lost();
            return ValueTask.FromResult(result);
        }
    }
}
