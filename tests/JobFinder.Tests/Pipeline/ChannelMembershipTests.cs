using JobFinder.Core.Domain;
using JobFinder.Data;
using JobFinder.Pipeline;
using JobFinder.Pipeline.Modules.Discovery;
using JobFinder.Pipeline.Notifications;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace JobFinder.Tests.Pipeline;

public sealed partial class ChannelMembershipTests(PostgresFixture pg) : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private IHost? _host;
    private readonly Gateway _gateway = new();
    private readonly MembershipFault _fault = new();
    private ChannelMembershipWorkflow Workflow => _host!.Services.GetRequiredService<ChannelMembershipWorkflow>();

    public async ValueTask InitializeAsync()
    {
        Assert.SkipWhen(pg.SkipReason is not null, pg.SkipReason ?? "");
        await pg.SeedPoliciesAsync();
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        await context.ChannelMembershipAttempts.ExecuteDeleteAsync(Ct);
        await context.ChannelMembershipRequests.ExecuteDeleteAsync(Ct);
        await ClearMembershipEnvelopesAsync(context);
        await context.PostProcessingRequests.ExecuteDeleteAsync(Ct);
        await context.RawPosts.ExecuteDeleteAsync(Ct);
        await context.TelegramChannelDecisionRevisions.ExecuteDeleteAsync(Ct);
        await context.TelegramChannelApprovalBatches.ExecuteDeleteAsync(Ct);
        await context.TelegramChannels.ExecuteDeleteAsync(Ct);
        await context.Settings.Where(row => row.Key == ChannelMembershipRequests.PacingKey || row.Key == ChannelMembershipRequests.FloodKey || row.Key == ChannelCollectionSources.ContactsBackfilledKey).ExecuteDeleteAsync(Ct);
        await NotificationAdmissionAssertions.ResetAsync(context, Ct);
        _host = await PostProcessingTestHost.BuildAsync(pg, false, fault: _fault, configure: services =>
        {
            services.RemoveAll<IChannelMembershipGateway>();
            services.AddSingleton<IChannelMembershipGateway>(_gateway);
        });
        await _host.StartAsync(Ct);
        _host.Services.GetRequiredService<ServerOwnership>().Acquired();
    }

    public async ValueTask DisposeAsync()
    {
        if (_host is null) return;
        await _host.StopAsync(CancellationToken.None);
        _host.Dispose();
    }

    private async Task<Guid> AdmitAsync()
    {
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        var source = new TelegramChannel { Username = "synthetic", Title = "Synthetic", Kind = ChannelKind.Channel,
            Status = ChannelStatus.Proposed, AddedAt = new DateTimeOffset(2026, 10, 4, 12, 0, 0, TimeSpan.Zero) };
        context.TelegramChannels.Add(source);
        await context.SaveChangesAsync(Ct);
        Assert.True((await _host!.Services.GetRequiredService<ChannelDecisions>().SetAsync(ChannelDecisionSnapshot.Capture(source), ChannelStatus.Approved, "synthetic", Ct)).Ok);
        return (await context.TelegramChannels.AsNoTracking().SingleAsync(row => row.Id == source.Id, Ct)).MembershipRequestId!.Value;
    }

    internal static Task ClearMembershipEnvelopesAsync(JobFinderDbContext context) => context.Database.ExecuteSqlRawAsync("""
        DO $cleanup$
        DECLARE storage text;
        BEGIN
          FOREACH storage IN ARRAY ARRAY['jobfinder_queues.wolverine_queue_discovery', 'jobfinder_queues.wolverine_queue_discovery_scheduled',
            'jobfinder_messages.wolverine_incoming_envelopes', 'jobfinder_messages.wolverine_outgoing_envelopes', 'jobfinder_messages.wolverine_dead_letters'] LOOP
            IF to_regclass(storage) IS NOT NULL THEN
              EXECUTE format('DELETE FROM %s WHERE message_type LIKE ''jobfinder.discovery.channel-membership.%%'' OR message_type LIKE ''%%ProcessChannelMembership%%''', storage);
            END IF;
          END LOOP;
        END
        $cleanup$;
        """, Ct);

    private async Task<ChannelMembershipRequest> ReadAsync(Guid id)
    {
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        return await context.ChannelMembershipRequests.AsNoTracking().SingleAsync(row => row.Id == id, Ct);
    }

    private async Task RunAsync(Guid id)
    {
        var root = await ReadAsync(id);
        await Workflow.RunAsync(new ProcessChannelMembership { RequestId = id, Stage = root.Stage }, Guid.NewGuid(), Ct);
    }

    [Fact]
    public async Task All_checkpoints_and_duplicate_business_commands_publish_one_source_and_bound_receipts()
    {
        var id = await AdmitAsync();
        for (var index = 0; index < 7; index++) await RunAsync(id);
        Assert.Equal(ChannelMembershipStage.Completed, (await ReadAsync(id)).Stage);
        await Workflow.RunAsync(new ProcessChannelMembership { RequestId = id, Stage = ChannelMembershipStage.Joining }, Guid.NewGuid(), Ct);
        Assert.Equal(4, _gateway.Effects);
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        Assert.Equal(ChannelStatus.Joined, (await context.TelegramChannels.SingleAsync(Ct)).Status);
        var attempts = await context.ChannelMembershipAttempts.ToListAsync(Ct);
        Assert.Equal(4, attempts.Count);
        Assert.All(attempts, attempt => Assert.Equal(ChannelMembershipOutcome.Confirmed, attempt.Outcome));
    }

    [Fact]
    public async Task Invite_request_is_retained_without_certifying_membership_or_repeating_effect()
    {
        var id = await AdmitAsync();
        await RunAsync(id);
        _gateway.Error = new ChannelMembershipRpcException("INVITE_REQUEST_SENT", inviteRequested: true);
        await RunAsync(id);
        await Workflow.RunAsync(new ProcessChannelMembership { RequestId = id, Stage = ChannelMembershipStage.Joining }, Guid.NewGuid(), Ct);
        Assert.Equal(1, _gateway.Effects);
        Assert.Equal(ChannelMembershipStage.Failed, (await ReadAsync(id)).Stage);
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        Assert.Equal(ChannelMembershipOutcome.InviteRequested, (await context.ChannelMembershipAttempts.SingleAsync(Ct)).Outcome);
        Assert.Equal(ChannelStatus.Failed, (await context.TelegramChannels.SingleAsync(Ct)).Status);
    }

    [Fact]
    public async Task Unknown_effect_is_not_repeated_and_exact_checked_reconciliation_admits_no_effect()
    {
        var id = await AdmitAsync();
        await RunAsync(id);
        _gateway.Error = new IOException("synthetic lost wire response");
        await RunAsync(id);
        var unknown = await ReadAsync(id);
        Assert.Equal(ChannelMembershipStage.OutcomeUnknown, unknown.Stage);
        await Workflow.RunAsync(new ProcessChannelMembership { RequestId = id, Stage = ChannelMembershipStage.Joining }, Guid.NewGuid(), Ct);
        Assert.Equal(1, _gateway.Effects);
        var ledger = _host!.Services.GetRequiredService<ChannelMembershipLedger>();
        Assert.False((await ledger.ReconcileAsync(id, Guid.NewGuid(), true, "synthetic owner checked membership", Ct)).Ok);
        Assert.True((await ledger.ReconcileAsync(id, unknown.AttemptId!.Value, true, "synthetic owner checked membership", Ct)).Ok);
        Assert.Equal(ChannelMembershipStage.Reconciled, (await ReadAsync(id)).Stage);
        Assert.Equal(1, _gateway.Effects);
    }

    [Fact]
    public async Task Changed_account_defers_without_effect_and_ownership_loss_refuses_execution()
    {
        var id = await AdmitAsync();
        await RunAsync(id);
        _gateway.AccountId = 99;
        await RunAsync(id);
        Assert.Equal(0, _gateway.Effects);
        Assert.NotNull((await ReadAsync(id)).RetryAt);
        _host!.Services.GetRequiredService<ServerOwnership>().Lost();
        await Assert.ThrowsAsync<InvalidOperationException>(() => RunAsync(id));
        Assert.Equal(0, _gateway.Effects);
    }

    [Fact]
    public async Task Source_change_after_wire_retains_receipt_and_cannot_publish_source()
    {
        var id = await AdmitAsync();
        await RunAsync(id);
        _gateway.AfterEffect = async () =>
        {
            await using var context = await pg.Db.CreateDbContextAsync(Ct);
            await context.TelegramChannels.ExecuteUpdateAsync(update => update.SetProperty(row => row.Title, "Changed")
                .SetProperty(row => row.DecisionRevision, 19L), Ct);
        };
        await RunAsync(id);
        Assert.Equal(ChannelMembershipStage.Cancelled, (await ReadAsync(id)).Stage);
        await using var saved = await pg.Db.CreateDbContextAsync(Ct);
        Assert.Equal(ChannelMembershipOutcome.Confirmed, (await saved.ChannelMembershipAttempts.SingleAsync(Ct)).Outcome);
        Assert.Equal("Changed", (await saved.TelegramChannels.SingleAsync(Ct)).Title);
        Assert.Equal(1, _gateway.Effects);
    }

    [Fact]
    public async Task Damaged_marker_cannot_hide_newer_active_root_from_decisions()
    {
        var id = await AdmitAsync();
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        var current = await context.ChannelMembershipRequests.SingleAsync(row => row.Id == id, Ct);
        current.Stage = ChannelMembershipStage.Cancelled;
        current.CompletedAt = DateTimeOffset.UtcNow;
        var next = new ChannelMembershipRequest { Id = Guid.NewGuid(), ChannelId = current.ChannelId,
            DecisionRevision = current.DecisionRevision + 1, Stage = ChannelMembershipStage.Resolving,
            InputJson = current.InputJson, InputHash = current.InputHash, CreatedAt = DateTimeOffset.UtcNow };
        context.ChannelMembershipRequests.Add(next);
        await context.SaveChangesAsync(Ct);
        Assert.False(await ChannelMembershipRequests.DecisionAllowedAsync(context, await context.TelegramChannels.SingleAsync(Ct), Ct));
    }

    [Fact]
    public async Task Importer_quarantines_missing_marker_once_and_preserves_retained_root()
    {
        var id = await AdmitAsync();
        await using (var context = await pg.Db.CreateDbContextAsync(Ct))
            await context.TelegramChannels.ExecuteUpdateAsync(update => update.SetProperty(row => row.MembershipRequestId, (Guid?)null), Ct);
        var importer = _host!.Services.GetRequiredService<ChannelMembershipRequests>();
        Assert.Equal(0, await importer.ImportApprovedAsync(Ct));
        await using var saved = await pg.Db.CreateDbContextAsync(Ct);
        var count = await saved.Notifications.CountAsync(Ct);
        Assert.Equal(ChannelStatus.Failed, (await saved.TelegramChannels.SingleAsync(Ct)).Status);
        Assert.Equal(0, await importer.ImportApprovedAsync(Ct));
        Assert.Equal(count, await saved.Notifications.CountAsync(Ct));
        Assert.Equal(id, (await saved.ChannelMembershipRequests.SingleAsync(Ct)).Id);
    }

    private sealed class Gateway : IChannelMembershipGateway
    {
        public long? AccountId { get; set; } = 42;
        public int Effects { get; private set; }
        public Exception? Error { get; set; }
        public Func<Task>? AfterEffect { get; set; }
        public Func<Task>? BeforeEffect { get; set; }
        public ChannelMembershipHistory History { get; set; } = new();
        public Task<ChannelMembershipChat?> ResolveAsync(ChannelMembershipInput input, long accountId, CancellationToken ct) =>
            Task.FromResult<ChannelMembershipChat?>(new() { Id = 123, AccessHash = 456, Username = "synthetic", Title = "Synthetic", Kind = ChannelKind.Channel });
        public async Task ApplyAsync(ChannelMembershipStage stage, ChannelMembershipChat chat, string folder, long accountId, Func<CancellationToken, Task> verify, CancellationToken ct)
        {
            if (BeforeEffect is not null) await BeforeEffect();
            await verify(ct);
            Effects++;
            if (AfterEffect is not null) await AfterEffect();
            if (Error is not null) throw Error;
        }
        public Task<ChannelMembershipHistory> HistoryAsync(ChannelMembershipChat chat, long accountId, CancellationToken ct) => Task.FromResult(History);
    }

    private sealed class MembershipFault : SaveChangesInterceptor
    {
        public ChannelMembershipStage? Stage { get; set; }
        public int Remaining { get; set; }
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (Remaining > 0 && eventData.Context!.ChangeTracker.Entries<ChannelMembershipRequest>().Any(entry => entry.Entity.Stage == Stage))
            {
                Remaining--;
                throw new IOException("synthetic membership SQL persistence fault");
            }
            return ValueTask.FromResult(result);
        }
    }
}
