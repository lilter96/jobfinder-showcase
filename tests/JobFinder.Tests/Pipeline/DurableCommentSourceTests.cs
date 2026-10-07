using JobFinder.Core.Domain;
using JobFinder.Core.Policies;
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
using Microsoft.Extensions.Time.Testing;
using Wolverine;

namespace JobFinder.Tests.Pipeline;

public sealed partial class DurableCommentPreparationIntegrationTests
{
    private async Task<PolicySnapshot<EngagementPolicy>> SourceProfileAsync(IHost host, bool auto = false, int cap = 10)
    {
        var profiles = host.Services.GetRequiredService<ProfileProvider>();
        await profiles.SaveAsync(Profile, (await profiles.GetSnapshotAsync(Ct))?.Version ?? 0, "synthetic-source", Ct);
        var policies = host.Services.GetRequiredService<EngagementPolicyStore>();
        var policy = await policies.ReadAsync(Ct);
        return await policies.PublishAsync(new EngagementPolicy { AutoPublish = auto, MaxDraftsPerDay = cap, AuthorCooldownDays = 7 }, policy.Version, "synthetic-source", Ct);
    }

    private Task<Guid?> CollectAsync(IHost host, PolicySnapshot<EngagementPolicy> policy, string id = "901", string author = "synthetic-author") =>
        host.Services.GetRequiredService<CommentSources>().CollectAsync(new ThreadsPost(id, author, Post, DateTimeOffset.UtcNow, "https://threads.invalid/post/" + id), policy, 48, Ct);

    [Fact]
    public async Task Author_cooldown_and_pending_reservations_ignore_Threads_username_case()
    {
        _steps.HoldFirst = true;
        var host = await HostAsync(true);
        var policy = await SourceProfileAsync(host);
        var id = (await CollectAsync(host, policy, author: "Synthetic-Author"))!.Value;
        await _steps.Entered.Task.WaitAsync(Ct);
        Assert.Null(await CollectAsync(host, policy, "902", "synthetic-author"));
        _steps.Release.TrySetResult();
        await host.Services.GetRequiredService<CommentSources>().WaitAsync(id, Ct);
        Assert.Null(await CollectAsync(host, policy, "903", "SYNTHETIC-AUTHOR"));
        Assert.Equal(1, Assert.Single(_steps.Drafts).Value);
    }

    [Fact]
    public async Task A_sparse_imported_candidate_document_is_normalized_before_source_witness_comparison()
    {
        var producer = await HostAsync(false);
        var policy = await SourceProfileAsync(producer);
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        await context.Documents.Where(row => row.Key == ProfileProvider.Key)
            .ExecuteUpdateAsync(update => update.SetProperty(row => row.Content, "{\"name\":\"Synthetic Candidate\",\"reviewed\":true}"), Ct);
        var id = (await CollectAsync(producer, policy))!.Value;
        var consumer = await HostAsync(true);
        var draft = await consumer.Services.GetRequiredService<CommentSources>().WaitAsync(id, Ct);
        Assert.Equal(CommentStatus.Pending, draft!.Status);
        Assert.Equal(1, Assert.Single(_steps.Drafts).Value);
    }

    [Fact]
    public async Task An_unsupported_native_source_command_retires_its_reservation_once_without_paying()
    {
        var producer = await HostAsync(false);
        var id = (await CollectAsync(producer, await SourceProfileAsync(producer)))!.Value;
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        await context.Database.ExecuteSqlRawAsync("DELETE FROM jobfinder_queues.wolverine_queue_preparation; DELETE FROM jobfinder_messages.wolverine_outgoing_envelopes", Ct);
        await producer.Services.GetRequiredService<IMessageBus>().PublishAsync(new PrepareCommentSource { RequestId = id, Version = 2 });
        await HostAsync(true);
        await UntilAsync(async () => (await SourceAsync(id)).Stage == CommentSourceStage.Failed);
        await producer.Services.GetRequiredService<IMessageBus>().PublishAsync(new PrepareCommentSource { RequestId = id, Version = 2 });
        await UntilAsync(async () => await context.Database.SqlQuery<int>($"SELECT count(*)::int AS \"Value\" FROM jobfinder_queues.wolverine_queue_preparation").SingleAsync(Ct) == 0);
        Assert.Empty(_steps.Drafts);
        Assert.Empty(await context.CommentDrafts.ToListAsync(Ct));
        Assert.Single(await context.Notifications.Where(row => row.Kind == NotificationKind.Alert).ToListAsync(Ct));
        Assert.Null((await SourceAsync(id)).ClaimEnvelopeId);
        Assert.Equal(1, await context.Database.SqlQuery<int>($"SELECT count(*)::int AS \"Value\" FROM jobfinder_messages.wolverine_dead_letters WHERE message_type LIKE '%PrepareCommentSource%'").SingleAsync(Ct));
    }

    private async Task<CommentSourceRequest> SourceAsync(Guid id)
    {
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        return await context.CommentSourceRequests.AsNoTracking().SingleAsync(row => row.Id == id, Ct);
    }

    [Fact]
    public async Task A_post_that_expires_during_the_provider_call_keeps_the_paid_result_but_publishes_no_card()
    {
        _steps.HoldFirst = true;
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var host = await BuildAsync(true, time: time);
        _hosts.Add(host);
        await host.StartAsync(Ct);
        var policy = await SourceProfileAsync(host);
        var id = (await host.Services.GetRequiredService<CommentSources>().CollectAsync(
            new ThreadsPost("expiring-901", "synthetic-author", Post, time.GetUtcNow(), "https://threads.invalid/post/expiring-901"), policy, 48, Ct))!.Value;
        await _steps.Entered.Task.WaitAsync(Ct);
        time.Advance(TimeSpan.FromHours(49));
        _steps.Release.TrySetResult();
        await UntilAsync(async () => (await SourceAsync(id)).Stage == CommentSourceStage.Cancelled);
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        Assert.Empty(await context.CommentDrafts.ToListAsync(Ct));
        Assert.Empty(await context.Notifications.Where(row => row.Kind == NotificationKind.CommentDraft).ToListAsync(Ct));
        Assert.Equal(CommentPreparationStage.Completed, (await context.CommentPreparationRequests.SingleAsync(Ct)).Stage);
        Assert.Equal(1, Assert.Single(_steps.Drafts).Value);
        Assert.Contains("пост устарел", (await SourceAsync(id)).Error!);
    }

    private async Task<CommentDraft> SeedSourceDraftAsync()
    {
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        var draft = new CommentDraft
        {
            PostId = "seed-901", PostUrl = "https://threads.invalid/post/seed-901", Author = "synthetic-author",
            PostText = Post, Status = CommentStatus.Pending, Audience = AudienceKind.Recruiter, CreatedAt = DateTimeOffset.UtcNow,
            Variants = Good().Variants,
        };
        context.CommentDrafts.Add(draft);
        await context.SaveChangesAsync(Ct);
        return draft;
    }

    [Fact]
    public async Task Source_witness_survives_the_PostgreSQL_timestamp_precision_roundtrip()
    {
        var draft = await SeedSourceDraftAsync();
        var now = DateTimeOffset.UtcNow;
        draft.CreatedAt = now.AddTicks(7 - now.Ticks % TimeSpan.TicksPerMicrosecond);
        draft.PostPublishedAt = now.AddTicks(3 - now.Ticks % TimeSpan.TicksPerMicrosecond);
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        context.CommentDrafts.Update(draft);
        await context.SaveChangesAsync(Ct);
        var reloaded = await context.CommentDrafts.AsNoTracking().SingleAsync(row => row.Id == draft.Id, Ct);
        Assert.NotEqual(draft.CreatedAt, reloaded.CreatedAt);
        Assert.NotEqual(draft.PostPublishedAt, reloaded.PostPublishedAt);
        Assert.Equal(CommentSources.Snapshot(draft), CommentSources.Snapshot(reloaded));
    }

    [Fact]
    public async Task Source_producer_exit_and_duplicate_collection_publish_one_draft_and_card()
    {
        var producer = await HostAsync(false);
        var policy = await SourceProfileAsync(producer);
        var requests = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => CollectAsync(producer, policy)));
        var id = Assert.Single(requests, request => request is not null)!.Value;
        await producer.StopAsync(Ct);
        Assert.Empty(_steps.Drafts);
        var consumer = await HostAsync(true);
        var draft = await consumer.Services.GetRequiredService<CommentSources>().WaitAsync(id, Ct);
        Assert.Equal(id, draft!.SourceRequestId);
        Assert.Equal(CommentStatus.Pending, draft.Status);
        var source = await SourceAsync(id);
        Assert.Equal(draft.Id, source.DraftId);
        Assert.Null(source.ClaimEnvelopeId);
        Assert.Equal(1, _steps.Drafts[source.PreparationRequestId!.Value]);
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        Assert.Single(await context.CommentDrafts.ToListAsync(Ct));
        var card = Assert.Single(await context.Notifications.Where(row => row.Kind == NotificationKind.CommentDraft && row.EntityId == draft.Id).ToListAsync(Ct));
        await NotificationAdmissionAssertions.JoinedAsync(context, card, Ct);
        await consumer.Services.GetRequiredService<IMessageBus>().PublishAsync(new PrepareCommentSource { RequestId = id });
        Assert.Null(await CollectAsync(consumer, policy));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Source_handoff_and_publication_faults_preserve_atomicity_without_repeating_paid_work(bool publication)
    {
        var fault = new SourceFault(publication);
        var host = await HostAsync(true, fault);
        var policy = await SourceProfileAsync(host);
        var id = (await CollectAsync(host, policy))!.Value;
        await fault.Entered.Task.WaitAsync(Ct);
        await using (var context = await pg.Db.CreateDbContextAsync(Ct))
        {
            Assert.Empty(await context.CommentDrafts.ToListAsync(Ct));
            Assert.Empty(await context.Notifications.Where(row => row.Kind == NotificationKind.CommentDraft).ToListAsync(Ct));
            Assert.Equal(publication ? 1 : 0, await context.CommentPreparationRequests.CountAsync(Ct));
        }
        var draft = await host.Services.GetRequiredService<CommentSources>().WaitAsync(id, Ct);
        Assert.Equal(CommentStatus.Pending, draft!.Status);
        Assert.Equal(1, Assert.Single(_steps.Drafts).Value);
        Assert.Equal(1, Assert.Single(_steps.Checks).Value);
        await using var read = await pg.Db.CreateDbContextAsync(Ct);
        Assert.Single(await read.CommentDrafts.ToListAsync(Ct));
        Assert.Single(await read.Notifications.Where(row => row.Kind == NotificationKind.CommentDraft).ToListAsync(Ct));
    }

    [Fact]
    public async Task Held_child_releases_the_source_handler_and_the_daily_slot_is_reserved_before_payment()
    {
        _steps.HoldFirst = true;
        var host = await HostAsync(true);
        var policy = await SourceProfileAsync(host, cap: 1);
        var id = (await CollectAsync(host, policy))!.Value;
        await _steps.Entered.Task.WaitAsync(Ct);
        await UntilAsync(async () => (await SourceAsync(id)).Stage == CommentSourceStage.Waiting);
        Assert.Null((await SourceAsync(id)).ClaimEnvelopeId);
        Assert.Null(await CollectAsync(host, policy, "902", "another-author"));
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        Assert.Equal(0, await context.Database.SqlQuery<int>($"SELECT count(*)::int AS \"Value\" FROM jobfinder_queues.wolverine_queue_preparation_scheduled WHERE message_type LIKE '%PrepareCommentSource%'").SingleAsync(Ct));
        _steps.Release.TrySetResult();
        await host.Services.GetRequiredService<CommentSources>().WaitAsync(id, Ct);
        Assert.Equal(1, Assert.Single(_steps.Drafts).Value);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Changed_candidate_and_zero_cap_cancel_publication_without_losing_the_paid_checkpoint(bool cap)
    {
        _steps.HoldFirst = true;
        var host = await HostAsync(true);
        var policy = await SourceProfileAsync(host, auto: true);
        var id = (await CollectAsync(host, policy))!.Value;
        await _steps.Entered.Task.WaitAsync(Ct);
        if (cap)
        {
            var policies = host.Services.GetRequiredService<EngagementPolicyStore>();
            await policies.PublishAsync(policy.Value with { MaxDraftsPerDay = 0 }, policy.Version, "restrictive-edit", Ct);
        }
        else
        {
            var profiles = host.Services.GetRequiredService<ProfileProvider>();
            var snapshot = await profiles.GetSnapshotAsync(Ct);
            await profiles.SaveAsync(snapshot!.Value with { Name = "Changed candidate" }, snapshot.Version, "candidate-edit", Ct);
        }
        _steps.Release.TrySetResult();
        await UntilAsync(async () => (await SourceAsync(id)).Stage == CommentSourceStage.Cancelled);
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        Assert.Empty(await context.CommentDrafts.ToListAsync(Ct));
        Assert.Equal(CommentPreparationStage.Completed, (await context.CommentPreparationRequests.SingleAsync(Ct)).Stage);
        Assert.Equal(1, Assert.Single(_steps.Drafts).Value);
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task Current_preferences_can_downgrade_but_cannot_upgrade_the_captured_approval(bool before, bool after)
    {
        _steps.HoldFirst = true;
        var host = await HostAsync(true);
        var policy = await SourceProfileAsync(host, auto: before);
        var id = (await CollectAsync(host, policy))!.Value;
        await _steps.Entered.Task.WaitAsync(Ct);
        await host.Services.GetRequiredService<EngagementPolicyStore>().PublishAsync(policy.Value with { AutoPublish = after }, policy.Version, "preference-edit", Ct);
        _steps.Release.TrySetResult();
        var draft = await host.Services.GetRequiredService<CommentSources>().WaitAsync(id, Ct);
        Assert.Equal(CommentStatus.Pending, draft!.Status);
        Assert.Null(draft.ChosenText);
    }

    [Fact]
    public async Task Pause_after_payment_defers_source_publication_and_unpause_reuses_the_exact_paid_result()
    {
        _steps.HoldFirst = true;
        var host = await HostAsync(true);
        var policy = await SourceProfileAsync(host);
        var id = (await CollectAsync(host, policy))!.Value;
        await _steps.Entered.Task.WaitAsync(Ct);
        var automation = host.Services.GetRequiredService<RuntimeSettings>().Automation;
        var before = await automation.ReadAsync(Ct);
        await automation.PublishAsync(before.Value with { CommentsPaused = true }, before.Version, "pause", Ct);
        _steps.Release.TrySetResult();
        await UntilAsync(async () => (await SourceAsync(id)).RetryAt > DateTimeOffset.UtcNow.AddSeconds(10));
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        Assert.Empty(await context.CommentDrafts.ToListAsync(Ct));
        var paused = await automation.ReadAsync(Ct);
        await automation.PublishAsync(paused.Value with { CommentsPaused = false }, paused.Version, "resume", Ct);
        await context.CommentSourceRequests.Where(row => row.Id == id).ExecuteUpdateAsync(update => update.SetProperty(row => row.RetryAt, (DateTimeOffset?)null), Ct);
        await host.Services.GetRequiredService<IMessageBus>().PublishAsync(new PrepareCommentSource { RequestId = id });
        await host.Services.GetRequiredService<CommentSources>().WaitAsync(id, Ct);
        Assert.Equal(1, Assert.Single(_steps.Drafts).Value);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Refresh_commits_retirement_replacement_and_card_intents_together(bool alreadySent)
    {
        var host = await HostAsync(true);
        await SourceProfileAsync(host);
        var previous = await SeedSourceDraftAsync();
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        var oldCard = new Notification
        {
            Kind = NotificationKind.CommentDraft, EntityId = previous.Id, Text = "Synthetic old card", CreatedAt = DateTimeOffset.UtcNow,
            Status = alreadySent ? NotificationStatus.Sent : NotificationStatus.Pending, TelegramMessageId = alreadySent ? 321 : null,
        };
        context.Notifications.Add(oldCard);
        await context.SaveChangesAsync(Ct);
        var sources = host.Services.GetRequiredService<CommentSources>();
        var id = (await sources.RefreshAsync(CommentSources.Snapshot(previous), Ct))!.Value;
        var fresh = await sources.WaitAsync(id, Ct);
        Assert.Equal(CommentStatus.Pending, fresh!.Status);
        Assert.Equal(previous.PostId, fresh.PostId);
        Assert.NotEqual(previous.Id, fresh.Id);
        context.ChangeTracker.Clear();
        var retired = await context.CommentDrafts.SingleAsync(row => row.Id == previous.Id, Ct);
        Assert.Equal(CommentStatus.Expired, retired.Status);
        Assert.Equal(previous.Revision + 1, retired.Revision);
        Assert.Contains("#expired-" + previous.Id, retired.PostId);
        Assert.Single(await context.Notifications.Where(row => row.Kind == NotificationKind.CommentDraft && row.EntityId == fresh.Id).ToListAsync(Ct));
        if (alreadySent) Assert.Single(await context.Notifications.Where(row => row.EditKeyboardMessageId == 321 && row.EntityId == previous.Id).ToListAsync(Ct));
        else Assert.Equal(NotificationStatus.Failed, (await context.Notifications.SingleAsync(row => row.Id == oldCard.Id, Ct)).Status);
    }

    [Fact]
    public async Task Approval_during_refresh_fences_late_replacement_and_keeps_the_approved_text()
    {
        _steps.HoldFirst = true;
        var host = await HostAsync(true);
        await SourceProfileAsync(host);
        var previous = await SeedSourceDraftAsync();
        var sources = host.Services.GetRequiredService<CommentSources>();
        var id = (await sources.RefreshAsync(CommentSources.Snapshot(previous), Ct))!.Value;
        await _steps.Entered.Task.WaitAsync(Ct);
        var result = await host.Services.GetRequiredService<CommentDecisions>().PickAsync(CommentDecisions.Snapshot(previous), 0, Ct);
        Assert.True(result.Ok, result.Message);
        _steps.Release.TrySetResult();
        await UntilAsync(async () => (await SourceAsync(id)).Stage == CommentSourceStage.Cancelled);
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        var draft = Assert.Single(await context.CommentDrafts.ToListAsync(Ct));
        Assert.Equal(CommentStatus.Approved, draft.Status);
        Assert.Equal(Good().Variants[0], draft.ChosenText);
        Assert.Equal(previous.PostId, draft.PostId);
        Assert.Empty(await context.Notifications.Where(row => row.Kind == NotificationKind.CommentDraft).ToListAsync(Ct));
    }

    [Fact]
    public async Task Concurrent_pick_and_reject_have_one_revision_winner_and_a_stale_edit_cannot_rebase()
    {
        var host = await HostAsync(false);
        var draft = await SeedSourceDraftAsync();
        var decisions = host.Services.GetRequiredService<CommentDecisions>();
        var snapshot = CommentDecisions.Snapshot(draft);
        var results = await Task.WhenAll(decisions.PickAsync(snapshot, 0, Ct), decisions.RejectAsync(snapshot, Ct));
        Assert.Single(results, result => result.Ok);
        Assert.False((await decisions.SetAsync(snapshot, Good().Variants[1], Ct)).Ok);
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        var stored = await context.CommentDrafts.SingleAsync(Ct);
        Assert.Equal(1, stored.Revision);
        Assert.Contains(stored.Status, new[] { CommentStatus.Approved, CommentStatus.Rejected });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Source_rollback_refuses_roots_or_orphaned_commands(bool orphaned)
    {
        var host = await HostAsync(false);
        var policy = await SourceProfileAsync(host);
        await CollectAsync(host, policy);
        await host.StopAsync(Ct);
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        if (orphaned) await context.CommentSourceRequests.ExecuteDeleteAsync(Ct);
        try
        {
            var notificationError = await Assert.ThrowsAsync<Npgsql.PostgresException>(() => MigrationTestRunner.DowngradeAndRestoreAsync(context, "20261001064525_DurableCommentPreparation", Ct));
            Assert.Contains("Cannot roll back notification delivery", notificationError.MessageText);
            Assert.Contains("20261001075512_DurableCommentSources", await context.Database.GetAppliedMigrationsAsync(Ct));
            await NotificationAdmissionAssertions.ResetAsync(context, Ct);
            var error = await Assert.ThrowsAsync<Npgsql.PostgresException>(() => MigrationTestRunner.DowngradeAndRestoreAsync(context, "20261001064525_DurableCommentPreparation", Ct));
            Assert.Contains("Cannot roll back comment sources", error.MessageText);
            Assert.Contains("20261001075512_DurableCommentSources", await context.Database.GetAppliedMigrationsAsync(Ct));
        }
        finally { await context.Database.MigrateAsync(CancellationToken.None); }
    }

    private sealed class SourceFault(bool publication) : SaveChangesInterceptor
    {
        private int _failed;
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData, InterceptionResult<int> result, CancellationToken ct = default)
        {
            if (eventData.Context!.ChangeTracker.Entries<CommentSourceRequest>().Any(entry => entry.State == EntityState.Modified
                && entry.Entity.Stage == (publication ? CommentSourceStage.Completed : CommentSourceStage.Waiting)) && Interlocked.Exchange(ref _failed, 1) == 0)
            {
                Entered.TrySetResult();
                throw new InvalidOperationException("synthetic source checkpoint fault");
            }
            return ValueTask.FromResult(result);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_damaged_paid_child_cannot_leave_its_source_claim_or_reservation_active(bool wrongRecipe)
    {
        var fault = new SourceFault(publication: true);
        var host = await HostAsync(true, fault);
        var id = (await CollectAsync(host, await SourceProfileAsync(host)))!.Value;
        await fault.Entered.Task.WaitAsync(Ct);
        var source = await SourceAsync(id);
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        var child = await context.CommentPreparationRequests.SingleAsync(row => row.Id == source.PreparationRequestId, Ct);
        Assert.Equal(CommentPreparationStage.Completed, child.Stage);
        if (wrongRecipe) child.BusinessKey = "synthetic-wrong-recipe";
        else child.InputHash = "synthetic-damaged-input";
        await context.SaveChangesAsync(Ct);
        await UntilAsync(async () => (await SourceAsync(id)).Stage == CommentSourceStage.Failed);
        await UntilAsync(async () => await context.Database.SqlQuery<int>($"SELECT count(*)::int AS \"Value\" FROM jobfinder_messages.wolverine_dead_letters WHERE message_type LIKE '%PrepareCommentSource%'").SingleAsync(Ct) == 1);
        Assert.Null((await SourceAsync(id)).ClaimEnvelopeId);
        Assert.Empty(await context.CommentDrafts.ToListAsync(Ct));
        Assert.Empty(await context.Notifications.Where(row => row.Kind == NotificationKind.CommentDraft).ToListAsync(Ct));
        Assert.Single(await context.Notifications.Where(row => row.Kind == NotificationKind.Alert).ToListAsync(Ct));
        Assert.Equal(1, Assert.Single(_steps.Drafts).Value);
    }
}
