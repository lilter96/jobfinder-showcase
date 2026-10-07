using JobFinder.Core.Domain;
using JobFinder.Core.Text;
using JobFinder.Data;
using JobFinder.Integrations.Threads;
using JobFinder.Pipeline.Modules.Engagement;
using JobFinder.Pipeline.Profile;
using JobFinder.Pipeline.Settings;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Wolverine;

namespace JobFinder.Tests.Pipeline;

public sealed partial class DurableCommentPreparationIntegrationTests
{
    private const string DeliveryText = "Я бы договаривался о сроке обратной связи до следующего этапа.";

    private async Task<IHost> DeliveryHostAsync(bool consumer, Delivery? delivery = null)
    {
        var host = await BuildAsync(consumer, delivery: delivery);
        _hosts.Add(host);
        await host.StartAsync(Ct);
        return host;
    }

    private async Task<CommentDraft> DeliveryDraftAsync(CommentStatus status = CommentStatus.Approved, int attempts = 0, string? text = DeliveryText)
    {
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        var post = Guid.NewGuid().ToString("N");
        var draft = new CommentDraft
        {
            PostId = post, Author = "author", PostUrl = "https://www.threads.com/@author/post/" + post, PostText = Post,
            ChosenText = text, Variants = [DeliveryText], Status = status, Audience = AudienceKind.Recruiter,
            Revision = 1, Attempts = attempts, CreatedAt = DateTimeOffset.UtcNow, DecidedAt = DateTimeOffset.UtcNow,
        };
        context.CommentDrafts.Add(draft);
        await context.SaveChangesAsync(Ct);
        return draft;
    }

    private async Task<CommentDeliveryRequest> DeliveryRootAsync(Guid id)
    {
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        return await context.CommentDeliveryRequests.AsNoTracking().SingleAsync(row => row.Id == id, Ct);
    }

    private async Task<Guid> QueueDeliveryAsync(IHost host, CommentDraft draft) =>
        (await host.Services.GetRequiredService<CommentDeliveryRequests>().RequestAsync(draft.Id, Ct))!.Value;

    private async Task WakeDeliveryAsync(IHost host, Guid id)
    {
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        await context.CommentDeliveryRequests.Where(row => row.Id == id).ExecuteUpdateAsync(update => update.SetProperty(row => row.RetryAt, (DateTimeOffset?)null), Ct);
        await context.Settings.Where(row => row.Key == CommentDeliveryWorkflow.PacingKey).ExecuteDeleteAsync(Ct);
        await host.Services.GetRequiredService<IMessageBus>().PublishAsync(new DispatchCommentDelivery { RequestId = id });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Delivery_rollback_refuses_roots_and_orphaned_publication_envelopes(bool orphaned)
    {
        var host = await DeliveryHostAsync(false);
        await SourceProfileAsync(host);
        await QueueDeliveryAsync(host, await DeliveryDraftAsync());
        await host.StopAsync(Ct);
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        var notificationError = await Assert.ThrowsAsync<Npgsql.PostgresException>(() => MigrationTestRunner.DowngradeAndRestoreAsync(context, "20261001075512_DurableCommentSources", Ct));
        Assert.Contains("Cannot roll back notification delivery", notificationError.MessageText);
        await NotificationAdmissionAssertions.ResetAsync(context, Ct);
        if (orphaned)
        {
            await context.CommentDeliveryRequests.ExecuteDeleteAsync(Ct);
            await context.CommentDrafts.ExecuteDeleteAsync(Ct);
        }
        try
        {
            var error = await Assert.ThrowsAsync<Npgsql.PostgresException>(() => MigrationTestRunner.DowngradeAndRestoreAsync(context, "20261001075512_DurableCommentSources", Ct));
            Assert.Contains("Cannot roll back comment delivery", error.MessageText);
            Assert.Contains("20261001100738_DurableCommentDelivery", await context.Database.GetAppliedMigrationsAsync(Ct));
        }
        finally { await context.Database.MigrateAsync(CancellationToken.None); }
    }

    [Fact]
    public async Task Approval_handoff_failure_rolls_back_source_root_and_native_command_together()
    {
        var host = await BuildAsync(false, fault: new ApprovalHandoffFault());
        _hosts.Add(host);
        await host.StartAsync(Ct);
        await SourceProfileAsync(host);
        var draft = await DeliveryDraftAsync(CommentStatus.Pending);
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        var decisions = host.Services.GetRequiredService<CommentDecisions>();
        await Assert.ThrowsAsync<InvalidOperationException>(() => decisions.PickAsync(CommentDecisions.Snapshot(draft), 0, Ct));
        var unchanged = await context.CommentDrafts.SingleAsync(Ct);
        Assert.Equal(CommentStatus.Pending, unchanged.Status);
        Assert.Equal(draft.Revision, unchanged.Revision);
        Assert.Null(unchanged.DeliveryRequestId);
        Assert.Empty(await context.CommentDeliveryRequests.ToListAsync(Ct));
        Assert.Equal(0, await context.Database.SqlQuery<int>($"SELECT count(*)::int AS \"Value\" FROM jobfinder_queues.wolverine_queue_preparation WHERE message_type LIKE '%PrepareCommentDelivery%'").SingleAsync(Ct));
        Assert.True((await decisions.PickAsync(CommentDecisions.Snapshot(draft), 0, Ct)).Ok);
        Assert.Single(await context.CommentDeliveryRequests.ToListAsync(Ct));
    }

    [Fact]
    public async Task Unknown_reconciliation_refuses_a_changed_attempt_witness_without_overwriting_it()
    {
        var host = await DeliveryHostAsync(false);
        await SourceProfileAsync(host);
        var id = await QueueDeliveryAsync(host, await DeliveryDraftAsync(attempts: 1));
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        await context.CommentDeliveryAttempts.ExecuteUpdateAsync(update => update.SetProperty(row => row.Text, "other text"), Ct);
        var result = await host.Services.GetRequiredService<CommentDeliveryLedger>().ReconcileAsync(id, true, "Synthetic checked thread", Ct);
        Assert.False(result.Ok);
        Assert.Equal(CommentDeliveryStage.OutcomeUnknown, (await DeliveryRootAsync(id)).Stage);
        Assert.Equal(DeliveryAttemptStatus.OutcomeUnknown, (await context.CommentDeliveryAttempts.SingleAsync(Ct)).Status);
    }

    [Fact]
    public async Task Delivery_producer_exit_duplicate_commands_and_receipt_replay_publish_only_once()
    {
        var producer = await DeliveryHostAsync(false);
        await SourceProfileAsync(producer);
        var draft = await DeliveryDraftAsync();
        var id = await QueueDeliveryAsync(producer, draft);
        Assert.Equal(id, await QueueDeliveryAsync(producer, draft));
        Assert.Equal(CommentDeliveryStage.Preparing, (await DeliveryRootAsync(id)).Stage);
        await producer.StopAsync(Ct);
        var delivery = new Delivery();
        var consumer = await DeliveryHostAsync(true, delivery);
        await UntilAsync(async () => (await DeliveryRootAsync(id)).Stage == CommentDeliveryStage.Completed);
        await consumer.Services.GetRequiredService<IMessageBus>().PublishAsync(new DispatchCommentDelivery { RequestId = id });
        await consumer.Services.GetRequiredService<IMessageBus>().PublishAsync(new PrepareCommentDelivery { RequestId = id });
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        await UntilAsync(async () => await context.Database.SqlQuery<int>($"SELECT count(*)::int AS \"Value\" FROM jobfinder_queues.wolverine_queue_engagement").SingleAsync(Ct) == 0);
        Assert.Equal(1, delivery.Effects);
        var attempt = Assert.Single(await context.CommentDeliveryAttempts.ToListAsync(Ct));
        Assert.Equal(DeliveryAttemptStatus.Confirmed, attempt.Status);
        Assert.Equal(DeliveryText, attempt.Text);
        Assert.Equal(CanonicalJson.Hash(System.Text.Json.JsonSerializer.Serialize(DeliveryText)), attempt.TextHash);
        Assert.Equal(CommentStatus.Posted, (await context.CommentDrafts.SingleAsync(Ct)).Status);
        Assert.Single(await context.Notifications.Where(row => row.Text.Contains("Комментарий опубликован")).ToListAsync(Ct));
    }

    [Fact]
    public async Task Manual_approval_and_native_root_commit_together_and_cancellation_uses_the_captured_revision()
    {
        var host = await DeliveryHostAsync(false);
        await SourceProfileAsync(host);
        var draft = await DeliveryDraftAsync(CommentStatus.Pending);
        var decisions = host.Services.GetRequiredService<CommentDecisions>();
        var old = CommentDecisions.Snapshot(draft);
        Assert.True((await decisions.PickAsync(old, 0, Ct)).Ok);
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        var approved = await context.CommentDrafts.AsNoTracking().SingleAsync(Ct);
        Assert.NotNull(approved.DeliveryRequestId);
        Assert.Single(await context.CommentDeliveryRequests.ToListAsync(Ct));
        Assert.False((await decisions.CancelAsync(old, Ct)).Ok);
        Assert.True((await decisions.CancelAsync(CommentDecisions.Snapshot(approved), Ct)).Ok);
        Assert.Equal(CommentDeliveryStage.Cancelled, (await DeliveryRootAsync(approved.DeliveryRequestId.Value)).Stage);
        var delivery = new Delivery();
        await DeliveryHostAsync(true, delivery);
        Assert.Equal(0, delivery.Effects);
        Assert.Equal(CommentStatus.Rejected, (await context.CommentDrafts.AsNoTracking().SingleAsync(Ct)).Status);
    }

    [Fact]
    public async Task Emergency_pause_before_preparation_schedules_without_network_work()
    {
        var delivery = new Delivery();
        var host = await DeliveryHostAsync(true, delivery);
        await SourceProfileAsync(host);
        await host.Services.GetRequiredService<RuntimeSettings>().SetAsync(RuntimeSettings.CommentsPaused, true, Ct);
        var id = await QueueDeliveryAsync(host, await DeliveryDraftAsync());
        await UntilAsync(async () => (await DeliveryRootAsync(id)).RetryAt is not null);
        Assert.Equal(CommentDeliveryStage.Preparing, (await DeliveryRootAsync(id)).Stage);
        Assert.Equal(0, delivery.Preparations);
        Assert.Equal(0, delivery.Effects);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Immediate_effect_gate_rechecks_emergency_pause_and_candidate_version(bool changeCandidate)
    {
        var delivery = new Delivery { Hold = true };
        var host = await DeliveryHostAsync(true, delivery);
        await SourceProfileAsync(host);
        var id = await QueueDeliveryAsync(host, await DeliveryDraftAsync());
        await delivery.Entered.Task.WaitAsync(Ct);
        if (changeCandidate)
        {
            var profiles = host.Services.GetRequiredService<ProfileProvider>();
            await profiles.SaveAsync(Profile with { Name = "Changed synthetic candidate" }, (await profiles.GetSnapshotAsync(Ct))!.Version, "synthetic-boundary", Ct);
        }
        else await host.Services.GetRequiredService<RuntimeSettings>().SetAsync(RuntimeSettings.CommentsPaused, true, Ct);
        delivery.Release.TrySetResult();
        await UntilAsync(async () => (await DeliveryRootAsync(id)).Stage == CommentDeliveryStage.Ready);
        Assert.Equal(0, delivery.Effects);
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        Assert.Equal(DeliveryAttemptStatus.KnownNotSent, (await context.CommentDeliveryAttempts.SingleAsync(Ct)).Status);
        if (changeCandidate)
        {
            await WakeDeliveryAsync(host, id);
            await UntilAsync(async () => (await DeliveryRootAsync(id)).Stage == CommentDeliveryStage.Cancelled);
            Assert.Equal(CommentStatus.Pending, (await context.CommentDrafts.AsNoTracking().SingleAsync(Ct)).Status);
        }
    }

    [Fact]
    public async Task An_exception_after_effect_is_unknown_with_one_notice_and_no_automatic_retry()
    {
        var delivery = new Delivery { ThrowAfterEffect = true };
        var host = await DeliveryHostAsync(true, delivery);
        await SourceProfileAsync(host);
        var draft = await DeliveryDraftAsync();
        var id = await QueueDeliveryAsync(host, draft);
        await UntilAsync(async () => (await DeliveryRootAsync(id)).Stage == CommentDeliveryStage.OutcomeUnknown);
        await host.Services.GetRequiredService<IMessageBus>().PublishAsync(new DispatchCommentDelivery { RequestId = id });
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        await UntilAsync(async () => await context.Database.SqlQuery<int>($"SELECT count(*)::int AS \"Value\" FROM jobfinder_queues.wolverine_queue_engagement").SingleAsync(Ct) == 0);
        Assert.Equal(1, delivery.Effects);
        Assert.Equal(DeliveryAttemptStatus.OutcomeUnknown, (await context.CommentDeliveryAttempts.SingleAsync(Ct)).Status);
        Assert.Equal(CommentStatus.OutcomeUnknown, (await context.CommentDrafts.SingleAsync(Ct)).Status);
        Assert.Single(await context.Notifications.Where(row => row.Text.Contains("Неизвестный исход комментария")).ToListAsync(Ct));
        Assert.False((await host.Services.GetRequiredService<CommentDecisions>().CancelAsync(CommentDecisions.Snapshot(draft) with { Status = CommentStatus.OutcomeUnknown }, Ct)).Ok);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Manual_reconciliation_records_checked_outcome_without_confirming_or_resending(bool sent)
    {
        var delivery = new Delivery { ThrowAfterEffect = true };
        var host = await DeliveryHostAsync(true, delivery);
        await SourceProfileAsync(host);
        var id = await QueueDeliveryAsync(host, await DeliveryDraftAsync());
        await UntilAsync(async () => (await DeliveryRootAsync(id)).Stage == CommentDeliveryStage.OutcomeUnknown);
        var ledger = host.Services.GetRequiredService<CommentDeliveryLedger>();
        Assert.False((await ledger.ReconcileAsync(id, sent, "", Ct)).Ok);
        Assert.True((await ledger.ReconcileAsync(id, sent, "Synthetic checked thread", Ct)).Ok);
        Assert.False((await ledger.ReconcileAsync(id, sent, "Synthetic repeated check", Ct)).Ok);
        Assert.Equal(1, delivery.Effects);
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        var attempt = await context.CommentDeliveryAttempts.SingleAsync(Ct);
        Assert.Equal(DeliveryAttemptStatus.Reconciled, attempt.Status);
        Assert.Equal(sent, attempt.ReconciledSent);
        Assert.Equal(sent ? CommentStatus.Posted : CommentStatus.Rejected, (await context.CommentDrafts.SingleAsync(Ct)).Status);
        Assert.Null((await context.CommentDrafts.SingleAsync(Ct)).PublishedAt);
    }

    [Fact]
    public async Task Malformed_legacy_attempts_are_saved_as_forensic_unknown_even_without_a_reviewed_candidate()
    {
        var host = await DeliveryHostAsync(false);
        var profiles = host.Services.GetRequiredService<ProfileProvider>();
        await profiles.SaveAsync(Profile with { Reviewed = false }, (await profiles.GetSnapshotAsync(Ct))?.Version ?? 0, "synthetic-legacy", Ct);
        var draft = await DeliveryDraftAsync(attempts: 2, text: "");
        var id = await QueueDeliveryAsync(host, draft);
        var root = await DeliveryRootAsync(id);
        Assert.Equal(CommentDeliveryStage.OutcomeUnknown, root.Stage);
        Assert.Throws<UnsupportedCommentDeliveryException>(() => CommentDeliveryInputs.Read(root));
        Assert.True(CommentDeliveryInputs.Witness(root).LegacyUncertain);
        Assert.True((await host.Services.GetRequiredService<CommentDeliveryLedger>().ReconcileAsync(id, false, "Synthetic old attempt checked absent", Ct)).Ok);
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        Assert.Equal(DeliveryAttemptStatus.Reconciled, (await context.CommentDeliveryAttempts.SingleAsync(Ct)).Status);
        Assert.Single(await context.CommentDeliveryRequests.ToListAsync(Ct));
    }

    [Fact]
    public async Task Zero_current_cap_schedules_instead_of_preparing_or_sending()
    {
        var delivery = new Delivery();
        var host = await DeliveryHostAsync(true, delivery);
        await SourceProfileAsync(host, cap: 0);
        var id = await QueueDeliveryAsync(host, await DeliveryDraftAsync());
        await UntilAsync(async () => (await DeliveryRootAsync(id)).RetryAt is not null);
        Assert.Equal(0, delivery.Preparations);
        Assert.Equal(0, delivery.Effects);
    }

    [Fact]
    public async Task A_comment_held_past_the_post_age_limit_expires_without_preparation_or_publication()
    {
        var delivery = new Delivery();
        var host = await DeliveryHostAsync(true, delivery);
        await SourceProfileAsync(host);
        var draft = await DeliveryDraftAsync();
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        await context.CommentDrafts.Where(row => row.Id == draft.Id).ExecuteUpdateAsync(update => update.SetProperty(row => row.PostPublishedAt, DateTimeOffset.UtcNow.AddDays(-3)), Ct);
        var id = await QueueDeliveryAsync(host, draft);
        await UntilAsync(async () => (await DeliveryRootAsync(id)).Stage == CommentDeliveryStage.Cancelled);
        Assert.Equal(CommentStatus.Expired, (await context.CommentDrafts.SingleAsync(Ct)).Status);
        Assert.Equal(0, delivery.Preparations);
        Assert.Equal(0, delivery.Effects);
    }

    [Theory]
    [InlineData(false, false, false, 1, false)]
    [InlineData(false, false, true, 1, false)]
    [InlineData(false, true, false, 1, true)]
    [InlineData(true, false, false, 2, true)]
    public async Task Delivery_cap_preserves_legacy_history_without_double_counting_native_receipts(bool native, bool yesterday, bool missingReceiptTime, int cap, bool allowed)
    {
        var producer = await DeliveryHostAsync(false);
        await SourceProfileAsync(producer, cap: cap);
        var posted = await DeliveryDraftAsync(CommentStatus.Posted);
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        var receiptTime = DateTimeOffset.UtcNow.AddDays(yesterday ? -1 : 0);
        await context.CommentDrafts.Where(row => row.Id == posted.Id).ExecuteUpdateAsync(update => update
            .SetProperty(row => row.PublishedAt, missingReceiptTime ? (DateTimeOffset?)null : receiptTime)
            .SetProperty(row => row.DecidedAt, receiptTime), Ct);
        if (native)
        {
            context.CommentDeliveryAttempts.Add(new CommentDeliveryAttempt
            {
                Id = Guid.NewGuid(), RequestId = Guid.NewGuid(), DraftId = posted.Id, Revision = posted.Revision,
                Target = posted.PostUrl, Text = DeliveryText, TextHash = CanonicalJson.Hash(System.Text.Json.JsonSerializer.Serialize(DeliveryText)),
                InputHash = new string('a', 64), PreparedHash = new string('b', 64),
                Status = DeliveryAttemptStatus.Confirmed, CreatedAt = receiptTime, CompletedAt = receiptTime,
            });
            await context.SaveChangesAsync(Ct);
        }
        var id = await QueueDeliveryAsync(producer, await DeliveryDraftAsync());
        await producer.StopAsync(Ct);
        var delivery = new Delivery();
        await DeliveryHostAsync(true, delivery);
        await UntilAsync(async () => (await DeliveryRootAsync(id)) is { Stage: CommentDeliveryStage.Completed } or { RetryAt: not null });
        Assert.Equal(allowed ? CommentDeliveryStage.Completed : CommentDeliveryStage.Preparing, (await DeliveryRootAsync(id)).Stage);
        Assert.Equal(allowed ? 1 : 0, delivery.Preparations);
        Assert.Equal(allowed ? 1 : 0, delivery.Effects);
    }

    [Fact]
    public async Task Immediate_effect_gate_counts_legacy_publications_added_after_preparation()
    {
        var delivery = new Delivery { Hold = true };
        var host = await DeliveryHostAsync(true, delivery);
        await SourceProfileAsync(host, cap: 1);
        var id = await QueueDeliveryAsync(host, await DeliveryDraftAsync());
        await delivery.Entered.Task.WaitAsync(Ct);
        await DeliveryDraftAsync(CommentStatus.Posted);
        delivery.Release.TrySetResult();
        await UntilAsync(async () => (await DeliveryRootAsync(id)).Stage == CommentDeliveryStage.Ready);
        Assert.Equal(0, delivery.Effects);
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        Assert.Equal(DeliveryAttemptStatus.KnownNotSent, (await context.CommentDeliveryAttempts.SingleAsync(Ct)).Status);
    }

    [Fact]
    public async Task A_sending_root_blocks_another_comment_even_after_the_pacing_cursor_expires()
    {
        var delivery = new Delivery { Hold = true };
        var host = await DeliveryHostAsync(true, delivery);
        await SourceProfileAsync(host);
        var first = await QueueDeliveryAsync(host, await DeliveryDraftAsync());
        await delivery.Entered.Task.WaitAsync(Ct);
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        await context.Settings.Where(row => row.Key == CommentDeliveryWorkflow.PacingKey).ExecuteDeleteAsync(Ct);
        var second = await QueueDeliveryAsync(host, await DeliveryDraftAsync());
        await UntilAsync(async () => (await DeliveryRootAsync(second)) is { Stage: CommentDeliveryStage.Ready, RetryAt: not null });
        Assert.Equal(CommentDeliveryStage.Sending, (await DeliveryRootAsync(first)).Stage);
        Assert.Single(await context.CommentDeliveryAttempts.ToListAsync(Ct));
        delivery.Release.TrySetResult();
        await UntilAsync(async () => (await DeliveryRootAsync(first)).Stage == CommentDeliveryStage.Completed);
        Assert.Equal(1, delivery.Effects);
    }

    [Fact]
    public async Task Unsupported_delivery_commands_retire_once_and_enter_native_dead_letter_without_effect()
    {
        var producer = await DeliveryHostAsync(false);
        await SourceProfileAsync(producer);
        var id = await QueueDeliveryAsync(producer, await DeliveryDraftAsync());
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        await context.Database.ExecuteSqlRawAsync("DELETE FROM jobfinder_queues.wolverine_queue_preparation; DELETE FROM jobfinder_messages.wolverine_outgoing_envelopes", Ct);
        await producer.Services.GetRequiredService<IMessageBus>().PublishAsync(new PrepareCommentDelivery { RequestId = id, Version = 2 });
        var delivery = new Delivery();
        await DeliveryHostAsync(true, delivery);
        await UntilAsync(async () => (await DeliveryRootAsync(id)).Stage == CommentDeliveryStage.Failed);
        await UntilAsync(async () => await context.Database.SqlQuery<int>($"SELECT count(*)::int AS \"Value\" FROM jobfinder_messages.wolverine_dead_letters WHERE message_type LIKE '%PrepareCommentDelivery%'").SingleAsync(Ct) == 1);
        Assert.Equal(0, delivery.Preparations);
        Assert.Equal(0, delivery.Effects);
        Assert.Single(await context.Notifications.Where(row => row.Text.Contains("не опубликован")).ToListAsync(Ct));
    }

    private sealed class Delivery : ICommentDeliveryTransport
    {
        private int _effects;
        private int _preparations;
        public int Effects => Volatile.Read(ref _effects);
        public int Preparations => Volatile.Read(ref _preparations);
        public bool Hold { get; set; }
        public bool ThrowAfterEffect { get; set; }
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ThreadsPublicationRoute CaptureRoute(ThreadsPost post) => new() { Mode = ThreadsPublicationMode.Browser, Endpoint = "https://www.threads.com" };
        public Task<ThreadsPreparedPublication> PrepareAsync(CommentDeliveryInput input, ThreadsPreparedPublication? saved, CancellationToken ct)
        {
            Interlocked.Increment(ref _preparations);
            return Task.FromResult(new ThreadsPreparedPublication { State = ThreadsPreparationState.Ready, AccountId = "candidate", Username = "candidate" });
        }
        public async Task<ThreadsPublicationReceipt> PublishAsync(CommentDeliveryInput input, ThreadsPreparedPublication prepared, Func<CancellationToken, Task<bool>> mayPublish, CancellationToken ct)
        {
            Entered.TrySetResult();
            if (Hold) await Release.Task.WaitAsync(ct);
            if (!await mayPublish(ct)) return new() { Status = DeliveryAttemptStatus.KnownNotSent, Retryable = true, Error = "synthetic revoked permission" };
            Interlocked.Increment(ref _effects);
            if (ThrowAfterEffect) throw new IOException("synthetic lost publication receipt");
            return new() { Status = DeliveryAttemptStatus.Confirmed, ExternalId = "https://www.threads.com/@candidate/post/receipt" };
        }
    }

    private sealed class ApprovalHandoffFault : SaveChangesInterceptor
    {
        private int _failed;
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData, InterceptionResult<int> result, CancellationToken ct = default)
        {
            if (eventData.Context!.ChangeTracker.Entries<CommentDeliveryRequest>().Any(entry => entry.State == EntityState.Added)
                && Interlocked.Exchange(ref _failed, 1) == 0) throw new InvalidOperationException("synthetic atomic approval handoff fault");
            return ValueTask.FromResult(result);
        }
    }
}
