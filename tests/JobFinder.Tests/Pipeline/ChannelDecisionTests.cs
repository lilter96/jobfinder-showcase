using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using JobFinder.Core.Domain;
using JobFinder.Core.Text;
using JobFinder.Data;
using JobFinder.Pipeline.Actions;
using JobFinder.Pipeline.Modules.Discovery;
using JobFinder.Pipeline.Notifications;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ChannelPage = JobFinder.App.Components.Pages.Channels;

namespace JobFinder.Tests.Pipeline;

public sealed class ChannelDecisionTests(PostgresFixture pg) : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private IHost? _host;
    private ChannelDecisions Owner => _host!.Services.GetRequiredService<ChannelDecisions>();

    public async ValueTask InitializeAsync()
    {
        Assert.SkipWhen(pg.SkipReason is not null, pg.SkipReason ?? "");
        await pg.SeedPoliciesAsync();
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        await context.ChannelMembershipAttempts.ExecuteDeleteAsync(Ct);
        await context.ChannelMembershipRequests.ExecuteDeleteAsync(Ct);
        await ChannelMembershipTests.ClearMembershipEnvelopesAsync(context);
        await context.Settings.Where(row => row.Key == ChannelMembershipRequests.PacingKey).ExecuteDeleteAsync(Ct);
        await context.TelegramChannelDecisionRevisions.ExecuteDeleteAsync(Ct);
        await context.TelegramChannelApprovalBatches.ExecuteDeleteAsync(Ct);
        await context.TelegramChannels.ExecuteDeleteAsync(Ct);
        await NotificationAdmissionAssertions.ResetAsync(context, Ct);
    }

    public async ValueTask DisposeAsync()
    {
        if (_host is null) return;
        try { await _host.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(20)); }
        finally { _host.Dispose(); }
    }

    private async Task StartAsync(AdmissionFault? fault = null, Action<IServiceCollection>? configure = null)
    {
        _host = await PostProcessingTestHost.BuildAsync(pg, consumer: false, fault: fault, configure: configure);
        await _host.StartAsync(Ct);
    }

    private async Task<TelegramChannel> SourceAsync(ChannelStatus status = ChannelStatus.Proposed, string username = "synthetic_channel")
    {
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        var source = new TelegramChannel { Username = username, Title = "Synthetic channel", Kind = ChannelKind.Channel,
            Status = status, AddedAt = new DateTimeOffset(2026, 10, 4, 12, 0, 0, TimeSpan.Zero) };
        context.TelegramChannels.Add(source);
        await context.SaveChangesAsync(Ct);
        return source;
    }

    private async Task<TelegramChannel> ReadAsync(long id)
    {
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        return await context.TelegramChannels.AsNoTracking().SingleAsync(row => row.Id == id, Ct);
    }

    [Fact]
    public async Task Proposal_cards_are_bounded_exact_cohorts_with_native_admission_and_case_insensitive_deduplication()
    {
        await StartAsync();
        var proposals = Enumerable.Range(0, 45).Select(index => new ChannelProposal("synthetic_channel_" + index,
            "Synthetic title", ChannelKind.Channel, new string('Ж', 90))).ToArray();
        Assert.Equal(45, await Owner.ProposeAsync(proposals, Ct));
        Assert.Equal(0, await Owner.ProposeAsync(proposals.Select(proposal => proposal with { Username = "@" + proposal.Username.ToUpperInvariant() }).ToArray(), Ct));
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        var batches = await context.TelegramChannelApprovalBatches.OrderBy(row => row.Id).ToListAsync(Ct);
        Assert.Equal(3, batches.Count);
        var captured = new List<long>();
        foreach (var batch in batches)
        {
            var snapshots = JsonSerializer.Deserialize(batch.InputJson, ChannelDecisionJsonContext.Default.ListChannelDecisionSnapshot)!;
            Assert.InRange(snapshots.Count, 1, ChannelDecisions.CardSize);
            Assert.Equal(CanonicalJson.Hash(batch.InputJson), batch.InputHash);
            captured.AddRange(snapshots.Select(snapshot => snapshot.Id));
            var notice = await context.Notifications.SingleAsync(row => row.Id == batch.NotificationId, Ct);
            foreach (var snapshot in snapshots) Assert.Contains("@" + snapshot.Username, notice.Text);
            Assert.DoesNotContain("… и ещё", notice.Text);
            Assert.InRange(notice.Text.Length, 1, 4096);
            Assert.Equal(Callback.Button($"✅ Одобрить эти {snapshots.Count}", Callback.ApproveChannels, batch.Id), Assert.Single(Assert.Single(notice.Buttons)));
            await NotificationAdmissionAssertions.JoinedAsync(context, notice, Ct);
        }
        Assert.Equal(45, captured.Distinct().Count());
        Assert.Empty(await context.TelegramChannelDecisionRevisions.ToListAsync(Ct));
    }

    [Fact]
    public async Task Escaped_long_reasons_cannot_hide_members_behind_an_actionable_approval_button()
    {
        await StartAsync();
        var proposals = Enumerable.Range(0, ChannelDecisions.CardSize).Select(index => new ChannelProposal(
            ("synthetic_" + index).PadRight(64, '_'), "Synthetic title", ChannelKind.Channel, new string('&', 90))).ToArray();
        Assert.Equal(proposals.Length, await Owner.ProposeAsync(proposals, Ct));
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        var batch = await context.TelegramChannelApprovalBatches.SingleAsync(Ct);
        var notice = await context.Notifications.SingleAsync(Ct);
        foreach (var proposal in proposals) Assert.Contains("@" + proposal.Username, notice.Text);
        Assert.Single(Assert.Single(notice.Buttons));
        Assert.InRange(notice.Text.Length, 1, 4000);
        Assert.True((await Owner.ApproveBatchAsync(batch.Id, "test", Ct)).Ok);
        Assert.Equal(proposals.Length, await context.TelegramChannelDecisionRevisions.CountAsync(Ct));
    }

    [Theory]
    [InlineData("source")]
    [InlineData("batch")]
    [InlineData("root")]
    public async Task Proposal_failure_at_each_intermediate_save_rolls_back_sources_cards_and_native_admission(string boundary)
    {
        var fault = new AdmissionFault { Boundary = boundary };
        await StartAsync(fault);
        await Assert.ThrowsAsync<InvalidOperationException>(() => Owner.ProposeAsync([new("synthetic_new", "New", ChannelKind.Channel, "Synthetic")], Ct));
        Assert.Equal(1, fault.Failures);
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        Assert.Empty(await context.TelegramChannels.ToListAsync(Ct));
        Assert.Empty(await context.TelegramChannelApprovalBatches.ToListAsync(Ct));
        Assert.Empty(await context.Notifications.ToListAsync(Ct));
        Assert.Empty(await context.NotificationDeliveryRequests.ToListAsync(Ct));
        fault.Boundary = null;
        Assert.Equal(1, await Owner.ProposeAsync([new("synthetic_new", "New", ChannelKind.Channel, "Synthetic")], Ct));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Decision_revision_history_and_native_notice_commit_or_roll_back_together(bool fail)
    {
        var fault = new AdmissionFault();
        await StartAsync(fault);
        var source = await SourceAsync();
        var expected = ChannelDecisionSnapshot.Capture(source);
        fault.Boundary = fail ? "root" : null;
        if (fail) await Assert.ThrowsAsync<InvalidOperationException>(() => Owner.SetAsync(expected, ChannelStatus.Approved, "test", Ct));
        else Assert.True((await Owner.SetAsync(expected, ChannelStatus.Approved, "test", Ct)).Ok);
        var saved = await ReadAsync(source.Id);
        Assert.Equal(fail ? expected : expected with { Revision = 1, Status = ChannelStatus.Approved }, ChannelDecisionSnapshot.Capture(saved));
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        if (fail)
        {
            Assert.Equal(1, fault.Failures);
            Assert.Empty(await context.TelegramChannelDecisionRevisions.ToListAsync(Ct));
            Assert.Empty(await context.Notifications.ToListAsync(Ct));
            Assert.Empty(await context.NotificationDeliveryRequests.ToListAsync(Ct));
            fault.Boundary = null;
            Assert.True((await Owner.SetAsync(expected, ChannelStatus.Approved, "test", Ct)).Ok);
        }
        var history = await context.TelegramChannelDecisionRevisions.SingleAsync(Ct);
        Assert.Equal(expected, JsonSerializer.Deserialize(history.BeforeJson, ChannelDecisionJsonContext.Default.ChannelDecisionSnapshot));
        Assert.Equal("test", history.Actor);
        await NotificationAdmissionAssertions.JoinedAsync(context, await context.Notifications.SingleAsync(Ct), Ct);
    }

    [Fact]
    public async Task Bot_batch_never_approves_later_proposals_and_replay_never_reapproves_a_rejected_member()
    {
        await StartAsync();
        var first = await SourceAsync();
        Assert.True((await Owner.PublishCurrentCardAsync(Ct)).Ok);
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        var batch = await context.TelegramChannelApprovalBatches.SingleAsync(Ct);
        var later = await SourceAsync(username: "synthetic_later");
        var approvals = _host!.Services.GetRequiredService<ApprovalService>();
        Assert.False((await approvals.ApproveChannelBatchAsync(0, Ct)).Ok);
        Assert.True((await approvals.ApproveChannelBatchAsync(batch.Id, Ct)).Ok);
        Assert.Equal(ChannelStatus.Proposed, (await ReadAsync(later.Id)).Status);
        Assert.True((await Owner.SetAsync(ChannelDecisionSnapshot.Capture(await ReadAsync(first.Id)), ChannelStatus.Rejected, "test", Ct)).Ok);
        Assert.True((await approvals.ApproveChannelBatchAsync(batch.Id, Ct)).Ok);
        Assert.Equal(ChannelStatus.Rejected, (await ReadAsync(first.Id)).Status);
        Assert.Equal(2, (await ReadAsync(first.Id)).DecisionRevision);
        Assert.Equal(3, await context.Notifications.CountAsync(Ct));
        Assert.Equal(2, await context.TelegramChannelDecisionRevisions.CountAsync(Ct));
    }

    [Fact]
    public async Task A_stale_batch_refuses_all_members_without_marking_the_batch_applied()
    {
        await StartAsync();
        var first = await SourceAsync();
        var second = await SourceAsync(username: "synthetic_second");
        await Owner.PublishCurrentCardAsync(Ct);
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        var batch = await context.TelegramChannelApprovalBatches.SingleAsync(Ct);
        Assert.True((await Owner.SetAsync(ChannelDecisionSnapshot.Capture(first), ChannelStatus.Rejected, "test", Ct)).Ok);
        Assert.False((await Owner.ApproveBatchAsync(batch.Id, "test", Ct)).Ok);
        Assert.Equal(ChannelStatus.Proposed, (await ReadAsync(second.Id)).Status);
        Assert.Null((await context.TelegramChannelApprovalBatches.AsNoTracking().SingleAsync(Ct)).AppliedAt);
        Assert.Equal(2, await context.Notifications.CountAsync(Ct));
    }

    [Fact]
    public async Task Batch_decision_admission_failure_preserves_original_card_and_can_be_retried_without_reproposing()
    {
        var fault = new AdmissionFault();
        await StartAsync(fault);
        var source = await SourceAsync();
        await Owner.PublishCurrentCardAsync(Ct);
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        var batch = await context.TelegramChannelApprovalBatches.SingleAsync(Ct);
        var originalNotice = await context.Notifications.SingleAsync(Ct);
        fault.Boundary = "root";
        await Assert.ThrowsAsync<InvalidOperationException>(() => Owner.ApproveBatchAsync(batch.Id, "test", Ct));
        Assert.Equal(ChannelStatus.Proposed, (await ReadAsync(source.Id)).Status);
        Assert.Null((await context.TelegramChannelApprovalBatches.AsNoTracking().SingleAsync(Ct)).AppliedAt);
        Assert.Empty(await context.TelegramChannelDecisionRevisions.ToListAsync(Ct));
        Assert.Single(await context.Notifications.ToListAsync(Ct));
        await NotificationAdmissionAssertions.JoinedAsync(context, originalNotice, Ct);
        fault.Boundary = null;
        Assert.True((await Owner.ApproveBatchAsync(batch.Id, "test", Ct)).Ok);
        Assert.Single(await context.TelegramChannelApprovalBatches.ToListAsync(Ct));
        Assert.NotNull((await context.TelegramChannelApprovalBatches.AsNoTracking().SingleAsync(Ct)).AppliedAt);
        Assert.Single(await context.TelegramChannelDecisionRevisions.ToListAsync(Ct));
    }

    [Fact]
    public async Task Concurrent_decisions_have_one_winner_and_revision_fences_aba()
    {
        await StartAsync();
        var source = await SourceAsync();
        var original = ChannelDecisionSnapshot.Capture(source);
        var results = await Task.WhenAll(Owner.SetAsync(original, ChannelStatus.Approved, "test", Ct), Owner.SetAsync(original, ChannelStatus.Rejected, "test", Ct));
        Assert.Single(results, result => result.Ok);
        Assert.Single(results, result => !result.Ok);
        var current = ChannelDecisionSnapshot.Capture(await ReadAsync(source.Id));
        var next = current.Status == ChannelStatus.Approved ? ChannelStatus.Rejected : ChannelStatus.Approved;
        Assert.True((await Owner.SetAsync(current, next, "test", Ct)).Ok);
        Assert.True((await Owner.SetAsync(ChannelDecisionSnapshot.Capture(await ReadAsync(source.Id)), current.Status, "test", Ct)).Ok);
        Assert.False((await Owner.SetAsync(current, next, "test", Ct)).Ok);
        Assert.Equal(3, (await ReadAsync(source.Id)).DecisionRevision);
    }

    [Theory]
    [InlineData("identity")]
    [InlineData("peer")]
    [InlineData("kind")]
    [InlineData("status")]
    [InlineData("protocol")]
    [InlineData("overflow")]
    [InlineData("missing")]
    public async Task Changed_or_invalid_displayed_witness_is_not_rebased(string damage)
    {
        await StartAsync();
        var source = await SourceAsync();
        var expected = ChannelDecisionSnapshot.Capture(source);
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        switch (damage)
        {
            case "identity": expected.Username = "different"; break;
            case "peer": expected.PeerId = 123; break;
            case "kind": expected.Kind = ChannelKind.Group; break;
            case "status": expected.Status = ChannelStatus.Failed; break;
            case "protocol": expected.Version = 2; break;
            case "overflow": expected.Revision = long.MaxValue; break;
            case "missing": await context.TelegramChannels.ExecuteDeleteAsync(Ct); break;
        }
        Assert.False((await Owner.SetAsync(expected, ChannelStatus.Approved, "test", Ct)).Ok);
        Assert.Empty(await context.TelegramChannelDecisionRevisions.ToListAsync(Ct));
        Assert.Empty(await context.Notifications.ToListAsync(Ct));
    }

    [Theory]
    [InlineData("hash")]
    [InlineData("protocol")]
    [InlineData("typed")]
    [InlineData("duplicate")]
    [InlineData("empty")]
    [InlineData("null")]
    [InlineData("receipt")]
    [InlineData("card")]
    [InlineData("native")]
    [InlineData("unbound")]
    public async Task Damaged_saved_cohort_or_receipt_fails_closed(string damage)
    {
        await StartAsync();
        await SourceAsync();
        await Owner.PublishCurrentCardAsync(Ct);
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        var batch = await context.TelegramChannelApprovalBatches.SingleAsync(Ct);
        var payload = JsonNode.Parse(batch.InputJson)!.AsArray();
        switch (damage)
        {
            case "hash": batch.InputHash = new string('0', 64); break;
            case "protocol": batch.Version = 2; break;
            case "typed": payload[0]!["revision"] = new JsonArray(); break;
            case "duplicate": payload.Add(payload[0]!.DeepClone()); break;
            case "empty": payload.Clear(); break;
            case "null": payload[0] = null; break;
            case "receipt": batch.AppliedAt = DateTimeOffset.UtcNow; break;
            case "unbound": batch.NotificationId = null; break;
            case "card":
                var notice = await context.Notifications.SingleAsync(Ct);
                notice.Buttons = [[Callback.Button("Approve other", Callback.ApproveChannels, batch.Id + 1)]];
                break;
            case "native":
                var root = await context.NotificationDeliveryRequests.SingleAsync(Ct);
                root.AdmissionHash = new string('0', 64);
                break;
        }
        if (damage is "typed" or "duplicate" or "empty" or "null")
        {
            batch.InputJson = payload.ToJsonString();
            batch.InputHash = CanonicalJson.Hash(batch.InputJson);
        }
        await context.SaveChangesAsync(Ct);
        Assert.False((await Owner.ApproveBatchAsync(batch.Id, "test", Ct)).Ok);
        Assert.Equal(ChannelStatus.Proposed, (await context.TelegramChannels.AsNoTracking().SingleAsync(Ct)).Status);
        Assert.Empty(await context.TelegramChannelDecisionRevisions.ToListAsync(Ct));
        Assert.Single(await context.Notifications.ToListAsync(Ct));
    }

    [Fact]
    public async Task Counters_and_title_refresh_preserve_control_witness_but_stale_membership_status_cannot_overwrite_a_decision()
    {
        await StartAsync();
        var source = await SourceAsync(ChannelStatus.Approved);
        var expected = ChannelDecisionSnapshot.Capture(source);
        await using var collector = await pg.Db.CreateDbContextAsync(Ct);
        var tracked = await collector.TelegramChannels.SingleAsync(Ct);
        tracked.PostsSeen = 9;
        tracked.VacanciesFound = 2;
        tracked.LastMessageId = 99;
        tracked.Title = "Refreshed title";
        await collector.SaveChangesAsync(Ct);
        Assert.True(await Owner.IsCurrentApprovedAsync(expected, Ct));
        Assert.True((await Owner.SetAsync(expected, ChannelStatus.Rejected, "test", Ct)).Ok);
        tracked.Status = ChannelStatus.Joined;
        tracked.JoinedAt = DateTimeOffset.UtcNow;
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => collector.SaveChangesAsync(Ct));
        var saved = await ReadAsync(source.Id);
        Assert.Equal(ChannelStatus.Rejected, saved.Status);
        Assert.Equal(1, saved.DecisionRevision);
        Assert.Equal(9, saved.PostsSeen);
        Assert.Equal(2, saved.VacanciesFound);
        Assert.Equal(99, saved.LastMessageId);
        Assert.Null(saved.JoinedAt);
        Assert.False(await Owner.IsCurrentApprovedAsync(expected, Ct));
    }

    [Fact]
    public async Task Folder_source_owner_discards_a_stale_observation_even_without_a_new_user_revision()
    {
        await StartAsync(configure: services =>
        {
            services.RemoveAll<IChannelMembershipGateway>();
            services.AddSingleton<IChannelMembershipGateway>(new JobFinder.MessagingProbe.ChannelMembershipProbeGateway(pg.Db, Guid.NewGuid()));
        });
        _host!.Services.GetRequiredService<JobFinder.Pipeline.ServerOwnership>().Acquired();
        var source = await SourceAsync(ChannelStatus.Approved);
        var owner = _host.Services.GetRequiredService<ChannelCollectionSources>();
        var captured = await owner.CaptureFolderAsync(Ct);
        Assert.NotNull(captured);
        await using (var current = await pg.Db.CreateDbContextAsync(Ct))
            await current.TelegramChannels.ExecuteUpdateAsync(update => update.SetProperty(row => row.Status, ChannelStatus.Joined), Ct);
        var result = await owner.PublishFolderAsync(captured, [new JobFinder.Integrations.Telegram.TgChatInfo(123, 456,
            "Stale title", source.Username, ChannelKind.Channel, true)], Ct);
        Assert.False(result.Ok);
        var saved = await ReadAsync(source.Id);
        Assert.Equal(ChannelStatus.Joined, saved.Status);
        Assert.Null(saved.Error);
        Assert.Equal(0, saved.DecisionRevision);
        Assert.NotEqual("Stale title", saved.Title);
    }

    [Fact]
    public async Task Runtime_membership_failures_preserve_decision_history_and_require_a_fresh_retry_witness()
    {
        await StartAsync();
        var source = await SourceAsync();
        await Owner.SetAsync(ChannelDecisionSnapshot.Capture(source), ChannelStatus.Approved, "test", Ct);
        var approved = ChannelDecisionSnapshot.Capture(await ReadAsync(source.Id));
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        await context.TelegramChannels.ExecuteUpdateAsync(update => update.SetProperty(row => row.Status, ChannelStatus.Failed)
            .SetProperty(row => row.Error, "synthetic RPC error"), Ct);
        Assert.False((await Owner.SetAsync(approved, ChannelStatus.Approved, "test", Ct)).Ok);
        var failed = ChannelDecisionSnapshot.Capture(await ReadAsync(source.Id));
        Assert.Equal(1, failed.Revision);
        Assert.True((await Owner.SetAsync(failed, ChannelStatus.Approved, "test", Ct)).Ok);
        var current = ChannelDecisionSnapshot.Capture(await ReadAsync(source.Id));
        Assert.Equal(2, current.Revision);
        Assert.True(await Owner.IsCurrentApprovedAsync(current, Ct));
        Assert.Null((await ReadAsync(source.Id)).Error);
        Assert.Equal(2, await context.TelegramChannelDecisionRevisions.CountAsync(Ct));
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("actor")]
    [InlineData("before")]
    [InlineData("reset")]
    public async Task Missing_or_damaged_decision_history_refuses_sync_and_new_controls(string damage)
    {
        await StartAsync();
        var source = await SourceAsync();
        await Owner.SetAsync(ChannelDecisionSnapshot.Capture(source), ChannelStatus.Approved, "test", Ct);
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        if (damage == "missing") await context.TelegramChannelDecisionRevisions.ExecuteDeleteAsync(Ct);
        else if (damage == "actor") await context.TelegramChannelDecisionRevisions.ExecuteUpdateAsync(update => update.SetProperty(row => row.Actor, ""), Ct);
        else if (damage == "before") await context.TelegramChannelDecisionRevisions.ExecuteUpdateAsync(update => update.SetProperty(row => row.BeforeJson, "{\"version\":2}"), Ct);
        else await context.TelegramChannels.ExecuteUpdateAsync(update => update.SetProperty(row => row.Status, ChannelStatus.Proposed), Ct);
        var expected = ChannelDecisionSnapshot.Capture(await ReadAsync(source.Id));
        Assert.False(await Owner.IsCurrentApprovedAsync(expected, Ct));
        Assert.False((await Owner.SetAsync(expected, ChannelStatus.Rejected, "test", Ct)).Ok);
        Assert.Single(await context.Notifications.ToListAsync(Ct));
    }

    [Fact]
    public async Task Legacy_cards_noops_illegal_membership_changes_and_empty_lists_have_no_new_effects()
    {
        await StartAsync();
        var source = await SourceAsync(ChannelStatus.Joined);
        Assert.Empty(Cards.ChannelsProposed([source]).Buttons);
        Assert.Empty(Cards.ChannelsProposed(Enumerable.Repeat(source, ChannelDecisions.CardSize + 1).ToArray(), 1).Buttons);
        Assert.False((await Owner.SetAsync(ChannelDecisionSnapshot.Capture(source), ChannelStatus.Approved, "test", Ct)).Ok);
        await Assert.ThrowsAsync<ArgumentException>(() => Owner.SetAsync(ChannelDecisionSnapshot.Capture(source), ChannelStatus.Joined, "test", Ct));
        var rejected = await SourceAsync(ChannelStatus.Rejected, "synthetic_rejected");
        Assert.True((await Owner.SetAsync(ChannelDecisionSnapshot.Capture(rejected), ChannelStatus.Rejected, "test", Ct)).Ok);
        Assert.True((await Owner.ApproveAsync([], "test", Ct)).Ok);
        Assert.True((await Owner.PublishCurrentCardAsync(Ct)).Ok);
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        Assert.Empty(await context.TelegramChannelDecisionRevisions.ToListAsync(Ct));
        Assert.Empty(await context.TelegramChannelApprovalBatches.ToListAsync(Ct));
        Assert.Empty(await context.Notifications.ToListAsync(Ct));
    }

    [Fact]
    public async Task Cancelled_lock_wait_preserves_sources_and_outbox()
    {
        await StartAsync();
        var source = await SourceAsync();
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        await using var transaction = await context.Database.BeginTransactionAsync(Ct);
        await context.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(hashtextextended('telegram-channel-decisions', 0))", Ct);
        using var cancelled = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        cancelled.CancelAfter(TimeSpan.FromMilliseconds(200));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Owner.SetAsync(ChannelDecisionSnapshot.Capture(source), ChannelStatus.Approved, "test", cancelled.Token));
        await transaction.RollbackAsync(Ct);
        Assert.Equal(0, (await ReadAsync(source.Id)).DecisionRevision);
        Assert.Empty(await context.Notifications.ToListAsync(Ct));
    }

    [Fact]
    public async Task Migration_preserves_legacy_channels_and_downgrade_refuses_retained_card_ownership()
    {
        var source = await SourceAsync(ChannelStatus.Joined);
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        var before = await context.Database.SqlQueryRaw<string>("SELECT to_jsonb(channel)::text AS \"Value\" FROM telegram_channels channel").SingleAsync(Ct);
        await context.GetService<IMigrator>().MigrateAsync("20261004071140_OwnedScrapeLedger", Ct);
        await context.Database.MigrateAsync(Ct);
        Assert.Equal(before, await context.Database.SqlQueryRaw<string>("SELECT to_jsonb(channel)::text AS \"Value\" FROM telegram_channels channel").SingleAsync(Ct));
        Assert.False(context.Database.HasPendingModelChanges());
        await StartAsync();
        await SourceAsync(username: "synthetic_new");
        await Owner.PublishCurrentCardAsync(Ct);
        var error = await Assert.ThrowsAsync<Npgsql.PostgresException>(() => MigrationTestRunner.DowngradeAndRestoreAsync(context, "20261004071140_OwnedScrapeLedger", Ct));
        Assert.Contains("Cannot downgrade retained channel controls", error.MessageText);
        await context.Database.MigrateAsync(Ct);
        Assert.Equal(ChannelStatus.Joined, (await ReadAsync(source.Id)).Status);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Downgrade_refuses_retained_decisions_and_orphaned_revision_tombstones(bool orphan)
    {
        await StartAsync();
        var source = await SourceAsync();
        await Owner.SetAsync(ChannelDecisionSnapshot.Capture(source), ChannelStatus.Rejected, "test", Ct);
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        if (orphan) await context.TelegramChannelDecisionRevisions.ExecuteDeleteAsync(Ct);
        var error = await Assert.ThrowsAsync<Npgsql.PostgresException>(() => MigrationTestRunner.DowngradeAndRestoreAsync(context, "20261004071140_OwnedScrapeLedger", Ct));
        Assert.Contains("Cannot downgrade retained channel controls", error.MessageText);
        await context.Database.MigrateAsync(Ct);
        Assert.Equal(1, (await ReadAsync(source.Id)).DecisionRevision);
        Assert.True(await context.TelegramChannels.AnyAsync(Ct));
    }

    [Theory]
    [InlineData("conflict")]
    [InlineData("failure")]
    [InlineData("missing")]
    public async Task Actual_page_retains_original_choice_and_witness_until_explicit_reload(string situation)
    {
        var fault = new AdmissionFault();
        await StartAsync(fault);
        var source = await SourceAsync();
        var capture = new PageCapture();
        using var services = PageServices(capture);
        await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());
        await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var rendered = await renderer.RenderComponentAsync<ChannelPage>(ParameterView.Empty);
            var original = ((Dictionary<long, ChannelDecisionSnapshot>)Field(capture.Page, "_snapshots"))[source.Id];
            if (situation == "failure") fault.Boundary = "root";
            else if (situation == "conflict") await Owner.SetAsync(original, ChannelStatus.Rejected, "test", Ct);
            else
            {
                await using var context = await pg.Db.CreateDbContextAsync(Ct);
                await context.TelegramChannels.ExecuteDeleteAsync(Ct);
            }
            await CallAsync(capture.Page, "Set", source.Id, ChannelStatus.Approved);
            await CallAsync(capture.Page, "LoadAsync");
            Assert.Equal(original, ((Dictionary<long, ChannelDecisionSnapshot>)Field(capture.Page, "_snapshots"))[source.Id]);
            Assert.Equal(ChannelStatus.Approved, ((Dictionary<long, ChannelStatus>)Field(capture.Page, "_desired"))[source.Id]);
            Assert.Single((Dictionary<long, string>)Field(capture.Page, "_feedback"));
            Assert.False((bool)Field(capture.Page, "_busy"));
            Redraw(capture.Page);
            var html = System.Net.WebUtility.HtmlDecode(rendered.ToHtmlString());
            Assert.Contains("Ваш выбор: Approved", html);
            if (situation == "missing") Assert.Contains($"Канал #{source.Id}: выбор не сохранён", html);
            fault.Boundary = null;
            await CallAsync(capture.Page, "ReloadChannel", source.Id);
            Assert.Empty((Dictionary<long, string>)Field(capture.Page, "_feedback"));
            Assert.Empty((Dictionary<long, ChannelStatus>)Field(capture.Page, "_desired"));
            if (situation == "missing") Assert.Empty((Dictionary<long, ChannelDecisionSnapshot>)Field(capture.Page, "_snapshots"));
            else Assert.Equal(ChannelDecisionSnapshot.Capture(await ReadAsync(source.Id)), ((Dictionary<long, ChannelDecisionSnapshot>)Field(capture.Page, "_snapshots"))[source.Id]);
        });
    }

    [Fact]
    public async Task Actual_page_keeps_conflicted_batch_across_filter_refresh_and_only_explicit_reload_replaces_it()
    {
        await StartAsync();
        var first = await SourceAsync();
        var second = await SourceAsync(username: "synthetic_second");
        var capture = new PageCapture();
        using var services = PageServices(capture);
        await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());
        await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var rendered = await renderer.RenderComponentAsync<ChannelPage>(ParameterView.Empty);
            var original = ((List<ChannelDecisionSnapshot>)Field(capture.Page, "_proposed")).ToArray();
            await Owner.SetAsync(ChannelDecisionSnapshot.Capture(first), ChannelStatus.Rejected, "test", Ct);
            await CallAsync(capture.Page, "ApproveAll");
            Assert.True((bool)Field(capture.Page, "_batchConflict"));
            typeof(ChannelPage).GetField("_status", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(capture.Page, "Rejected");
            await CallAsync(capture.Page, "LoadAsync");
            Assert.Equal(original, (List<ChannelDecisionSnapshot>)Field(capture.Page, "_proposed"));
            Redraw(capture.Page);
            var html = System.Net.WebUtility.HtmlDecode(rendered.ToHtmlString());
            Assert.Contains("Не сохранён выбор подборки", html);
            Assert.Contains("@synthetic_second", html);
            Assert.Equal(ChannelStatus.Proposed, (await ReadAsync(second.Id)).Status);
            await CallAsync(capture.Page, "ReloadControls");
            Assert.Empty((List<ChannelDecisionSnapshot>)Field(capture.Page, "_proposed"));
            typeof(ChannelPage).GetField("_status", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(capture.Page, "Proposed");
            await CallAsync(capture.Page, "LoadAsync");
            Assert.Equal(second.Id, Assert.Single((List<ChannelDecisionSnapshot>)Field(capture.Page, "_proposed")).Id);
            await CallAsync(capture.Page, "ApproveAll");
            Assert.Equal(ChannelStatus.Approved, (await ReadAsync(second.Id)).Status);
            Assert.Equal(ChannelStatus.Rejected, (await ReadAsync(first.Id)).Status);
        });
    }

    private ServiceProvider PageServices(PageCapture capture) => new ServiceCollection().AddLogging().AddSingleton<IComponentActivator>(capture)
        .AddSingleton(pg.Db).AddSingleton(_host!.Services.GetRequiredService<ApprovalService>()).BuildServiceProvider();
    private static object Field(ChannelPage page, string name) => typeof(ChannelPage).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(page)!;
    private static Task CallAsync(ChannelPage page, string method, params object[] arguments) =>
        (Task)typeof(ChannelPage).GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(page, arguments)!;
    private static void Redraw(ChannelPage page) => typeof(ComponentBase).GetMethod("StateHasChanged", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(page, null);

    private sealed class PageCapture : IComponentActivator
    {
        public ChannelPage Page { get; } = new();
        public IComponent CreateInstance(Type type) => type == typeof(ChannelPage) ? Page : (IComponent)Activator.CreateInstance(type)!;
    }

    private sealed class AdmissionFault : SaveChangesInterceptor
    {
        public string? Boundary { get; set; }
        public int Failures { get; private set; }
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (eventData.Context is JobFinderDbContext context && Boundary is not null && (Boundary switch
                {
                    "source" => context.ChangeTracker.Entries<TelegramChannel>().Any(entry => entry.State == EntityState.Added),
                    "batch" => context.ChangeTracker.Entries<TelegramChannelApprovalBatch>().Any(entry => entry.State == EntityState.Added),
                    "root" => context.ChangeTracker.Entries<NotificationDeliveryRequest>().Any(entry => entry.State == EntityState.Added),
                    _ => false,
                }))
            {
                Failures++;
                throw new InvalidOperationException("synthetic channel publication failure");
            }
            return ValueTask.FromResult(result);
        }
    }
}
