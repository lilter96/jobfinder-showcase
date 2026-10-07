using JobFinder.Core.Domain;
using JobFinder.Pipeline.Modules.Discovery;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace JobFinder.Tests.Pipeline;

public sealed partial class ChannelMembershipTests
{
    [Fact]
    public async Task Unsupported_owned_input_is_quarantined_once_without_hiding_the_approved_source()
    {
        var id = await AdmitAsync();
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        var root = await context.ChannelMembershipRequests.SingleAsync(row => row.Id == id, Ct);
        var input = System.Text.Json.JsonSerializer.Deserialize(root.InputJson, ChannelMembershipJsonContext.Default.ChannelMembershipInput)!;
        input.Version = 99;
        root.InputJson = System.Text.Json.JsonSerializer.Serialize(input, ChannelMembershipJsonContext.Default.ChannelMembershipInput);
        root.InputHash = JobFinder.Core.Text.CanonicalJson.Hash(root.InputJson);
        await context.SaveChangesAsync(Ct);
        await Assert.ThrowsAsync<UnsupportedChannelMembershipException>(() => Workflow.RunAsync(new ProcessChannelMembership { RequestId = id, Stage = ChannelMembershipStage.Resolving }, Guid.NewGuid(), Ct));
        Assert.Equal(ChannelMembershipStage.Failed, (await ReadAsync(id)).Stage);
        Assert.Equal(ChannelStatus.Failed, (await context.TelegramChannels.AsNoTracking().SingleAsync(Ct)).Status);
        var count = await context.Notifications.CountAsync(Ct);
        await Workflow.RunAsync(new ProcessChannelMembership { RequestId = id, Stage = ChannelMembershipStage.Resolving }, Guid.NewGuid(), Ct);
        Assert.Equal(count, await context.Notifications.CountAsync(Ct));
        Assert.Equal(0, _gateway.Effects);
    }

    [Theory]
    [InlineData(ChannelMembershipStage.Joining)]
    [InlineData(ChannelMembershipStage.Muting)]
    [InlineData(ChannelMembershipStage.Archiving)]
    [InlineData(ChannelMembershipStage.Filing)]
    public async Task Concurrent_duplicate_envelope_retains_unknown_and_late_receipt_cannot_resume_effect(ChannelMembershipStage stage)
    {
        var id = await AdmitAsync();
        while ((await ReadAsync(id)).Stage != stage) await RunAsync(id);
        var previous = _gateway.Effects;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _gateway.AfterEffect = async () => { entered.TrySetResult(); await release.Task.WaitAsync(Ct); };
        var first = RunAsync(id);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        try
        {
            await Workflow.RunAsync(new ProcessChannelMembership { RequestId = id, Stage = stage }, Guid.NewGuid(), Ct);
            Assert.Equal(ChannelMembershipStage.OutcomeUnknown, (await ReadAsync(id)).Stage);
        }
        finally { release.TrySetResult(); }
        await first;
        Assert.Equal(ChannelMembershipStage.OutcomeUnknown, (await ReadAsync(id)).Stage);
        Assert.Equal(previous + 1, _gateway.Effects);
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        Assert.Single(await context.ChannelMembershipAttempts.Where(row => row.RequestId == id && row.Stage == stage).ToListAsync(Ct));
        Assert.Equal(ChannelMembershipOutcome.OutcomeUnknown, (await context.ChannelMembershipAttempts.SingleAsync(row => row.RequestId == id && row.Stage == stage, Ct)).Outcome);
    }

    [Fact]
    public async Task Cancellation_after_wire_claim_is_unknown_and_never_automatically_repeated()
    {
        var id = await AdmitAsync();
        await RunAsync(id);
        _gateway.Error = new OperationCanceledException("synthetic cancellation while observing issued SDK call");
        await RunAsync(id);
        Assert.Equal(ChannelMembershipStage.OutcomeUnknown, (await ReadAsync(id)).Stage);
        await Workflow.RunAsync(new ProcessChannelMembership { RequestId = id, Stage = ChannelMembershipStage.Joining }, Guid.NewGuid(), Ct);
        Assert.Equal(1, _gateway.Effects);
    }

    [Fact]
    public async Task Wire_success_and_exhausted_SQL_receipt_faults_retain_unknown_without_repeating_wire()
    {
        var id = await AdmitAsync();
        await RunAsync(id);
        _fault.Stage = ChannelMembershipStage.Muting;
        _fault.Remaining = 3;
        await RunAsync(id);
        Assert.Equal(1, _gateway.Effects);
        Assert.Equal(ChannelMembershipStage.OutcomeUnknown, (await ReadAsync(id)).Stage);
        await Workflow.RunAsync(new ProcessChannelMembership { RequestId = id, Stage = ChannelMembershipStage.Joining }, Guid.NewGuid(), Ct);
        Assert.Equal(1, _gateway.Effects);
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        Assert.Equal(ChannelMembershipOutcome.OutcomeUnknown, (await context.ChannelMembershipAttempts.SingleAsync(Ct)).Outcome);
    }

    [Fact]
    public async Task History_and_final_source_publication_are_atomic_and_retry_deduplicates_posts()
    {
        var id = await AdmitAsync();
        _gateway.History = new ChannelMembershipHistory { Posts = [new RawPost { Source = SourceKind.Telegram, ChatRef = "123",
            ExternalId = "1", Text = "Synthetic history post", Status = RawPostStatus.New,
            PublishedAt = DateTimeOffset.UtcNow, FetchedAt = DateTimeOffset.UtcNow },
            new RawPost { Source = SourceKind.Telegram, ChatRef = "123", ExternalId = "2", Text = "Second synthetic history post",
                Status = RawPostStatus.New, PublishedAt = DateTimeOffset.UtcNow, FetchedAt = DateTimeOffset.UtcNow }] };
        for (var index = 0; index < 6; index++) await RunAsync(id);
        _fault.Stage = ChannelMembershipStage.Completed;
        _fault.Remaining = 1;
        await RunAsync(id);
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        Assert.Equal(ChannelStatus.Approved, (await context.TelegramChannels.AsNoTracking().SingleAsync(Ct)).Status);
        Assert.Empty(await context.RawPosts.ToListAsync(Ct));
        Assert.Equal(ChannelMembershipStage.Publishing, (await ReadAsync(id)).Stage);
        await context.ChannelMembershipRequests.Where(row => row.Id == id).ExecuteUpdateAsync(update => update.SetProperty(row => row.RetryAt, (DateTimeOffset?)null), Ct);
        await RunAsync(id);
        Assert.Equal(ChannelMembershipStage.Completed, (await ReadAsync(id)).Stage);
        var source = await context.TelegramChannels.AsNoTracking().SingleAsync(Ct);
        Assert.Equal(ChannelStatus.Joined, source.Status);
        Assert.Equal(2, source.PostsSeen);
        var capturedPosts = await context.RawPosts.ToListAsync(Ct);
        Assert.Equal(2, capturedPosts.Count);
        Assert.All(capturedPosts, post => Assert.NotNull(post.ProcessingRequestId));
        Assert.Equal(2, capturedPosts.Select(post => post.ProcessingRequestId).Distinct().Count());
        Assert.Equal(2, await context.PostProcessingRequests.CountAsync(Ct));
        await Workflow.RunAsync(new ProcessChannelMembership { RequestId = id, Stage = ChannelMembershipStage.Publishing }, Guid.NewGuid(), Ct);
        Assert.Equal(2, await context.RawPosts.CountAsync(Ct));
        Assert.Equal(4, _gateway.Effects);
    }

    [Theory]
    [InlineData(ChannelMembershipRequests.PacingKey)]
    [InlineData(ChannelMembershipRequests.FloodKey)]
    public async Task Damaged_shared_cursor_fails_closed_without_repair_or_Telegram_effect(string key)
    {
        var id = await AdmitAsync();
        await RunAsync(id);
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        context.Settings.Add(new AppSetting { Key = key, Value = "damaged", UpdatedAt = DateTimeOffset.UtcNow });
        await context.SaveChangesAsync(Ct);
        await Assert.ThrowsAsync<UnsupportedChannelMembershipException>(() => RunAsync(id));
        Assert.Equal(0, _gateway.Effects);
        Assert.Equal("damaged", (await context.Settings.AsNoTracking().SingleAsync(row => row.Key == key, Ct)).Value);
        Assert.Equal(ChannelMembershipStage.Failed, (await ReadAsync(id)).Stage);
    }

    [Fact]
    public async Task Flood_wait_retains_known_not_applied_receipt_and_shared_pause_without_resetting_history()
    {
        var id = await AdmitAsync();
        await RunAsync(id);
        _gateway.Error = new ChannelMembershipRpcException("FLOOD_WAIT_120", TimeSpan.FromMinutes(2));
        await RunAsync(id);
        var root = await ReadAsync(id);
        Assert.Equal(ChannelMembershipStage.Joining, root.Stage);
        Assert.Null(root.AttemptId);
        Assert.NotNull(root.RetryAt);
        Assert.NotNull(root.ChatJson);
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        Assert.Equal(ChannelMembershipOutcome.KnownNotApplied, (await context.ChannelMembershipAttempts.SingleAsync(Ct)).Outcome);
        Assert.NotNull(await context.Settings.SingleOrDefaultAsync(row => row.Key == ChannelMembershipRequests.FloodKey, Ct));
        await RunAsync(id);
        Assert.Equal(1, _gateway.Effects);
    }

    [Fact]
    public async Task Missing_prior_receipt_refuses_next_effect_before_the_wire()
    {
        var id = await AdmitAsync();
        await RunAsync(id);
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        await context.ChannelMembershipRequests.Where(row => row.Id == id).ExecuteUpdateAsync(update => update.SetProperty(row => row.Stage, ChannelMembershipStage.Muting), Ct);
        await Assert.ThrowsAsync<UnsupportedChannelMembershipException>(() => RunAsync(id));
        Assert.Equal(0, _gateway.Effects);
        Assert.Equal(ChannelMembershipStage.Failed, (await ReadAsync(id)).Stage);
    }
}
