using JobFinder.Core.Domain;
using JobFinder.Integrations.Telegram;
using JobFinder.Pipeline.Modules.Discovery;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace JobFinder.Tests.Pipeline;

public sealed partial class ChannelMembershipTests
{
    [Theory]
    [InlineData("account")]
    [InlineData("source")]
    [InlineData("ownership")]
    public async Task Captured_history_cannot_admit_posts_after_account_source_or_ownership_change(string change)
    {
        var owner = _host!.Services.GetRequiredService<ChannelCollectionSources>();
        await owner.PublishFolderAsync((await owner.CaptureFolderAsync(Ct))!, [new TgChatInfo(123, 456, "Synthetic", "synthetic", ChannelKind.Channel, true)], Ct);
        var captured = (await owner.CaptureSourceAsync(123, 42, Ct))!;
        if (change == "account") _gateway.AccountId = 99;
        else if (change == "source")
        {
            await using var context = await pg.Db.CreateDbContextAsync(Ct);
            await context.TelegramChannels.ExecuteUpdateAsync(update => update.SetProperty(row => row.Status, ChannelStatus.Rejected), Ct);
        }
        else _host.Services.GetRequiredService<JobFinder.Pipeline.ServerOwnership>().Lost();
        var operation = () => owner.PublishHistoryAsync(captured, 42, [SyntheticPost(1)], Ct);
        if (change == "ownership") await Assert.ThrowsAsync<InvalidOperationException>(operation);
        else Assert.Equal(0, await operation());
        await using var verify = await pg.Db.CreateDbContextAsync(Ct);
        Assert.Empty(await verify.RawPosts.ToListAsync(Ct));
        Assert.Empty(await verify.PostProcessingRequests.ToListAsync(Ct));
    }

    [Fact]
    public async Task History_batch_has_distinct_child_operations_and_duplicate_publication_preserves_counters()
    {
        var owner = _host!.Services.GetRequiredService<ChannelCollectionSources>();
        await owner.PublishFolderAsync((await owner.CaptureFolderAsync(Ct))!, [new TgChatInfo(123, 456, "Synthetic", "synthetic", ChannelKind.Channel, true)], Ct);
        var captured = (await owner.CaptureSourceAsync(123, 42, Ct))!;
        Assert.Equal(2, await owner.PublishHistoryAsync(captured, 42, [SyntheticPost(1), SyntheticPost(2)], Ct));
        Assert.Equal(0, await owner.PublishHistoryAsync(captured, 42, [SyntheticPost(1), SyntheticPost(2)], Ct));
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        var source = await context.TelegramChannels.SingleAsync(Ct);
        Assert.Equal(2, source.PostsSeen);
        Assert.Equal(2, source.LastMessageId);
        var posts = await context.RawPosts.ToListAsync(Ct);
        Assert.Equal(2, posts.Count);
        Assert.Equal(2, posts.Select(post => post.ProcessingRequestId).Distinct().Count());
        Assert.Equal(2, await context.PostProcessingRequests.CountAsync(Ct));
        Assert.Equal(0, _gateway.Effects);
    }

    private static TgPost SyntheticPost(int id) => new(123, "Synthetic", "synthetic", id, "Synthetic history post " + id,
        DateTimeOffset.UtcNow, null, "https://t.me/synthetic/" + id, []);

    [Fact]
    public async Task Contact_backfill_completion_has_one_captured_owner_and_refuses_generic_reset()
    {
        var owner = _host!.Services.GetRequiredService<ChannelCollectionSources>();
        var captured = await owner.CaptureContactsBackfillAsync(Ct);
        Assert.NotNull(captured);
        Assert.False(captured.Completed);
        Assert.True(await owner.CompleteContactsBackfillAsync(captured, Ct));
        Assert.False(await owner.CompleteContactsBackfillAsync(captured, Ct));
        Assert.True((await owner.CaptureContactsBackfillAsync(Ct))!.Completed);
        var settings = _host.Services.GetRequiredService<JobFinder.Pipeline.Settings.RuntimeSettings>();
        await Assert.ThrowsAsync<InvalidOperationException>(() => settings.SetAsync(ChannelCollectionSources.ContactsBackfilledKey, false, Ct));
        await Assert.ThrowsAsync<InvalidOperationException>(() => settings.RemoveAsync(ChannelCollectionSources.ContactsBackfilledKey, Ct));
    }

    [Fact]
    public async Task Manual_folder_observation_publishes_source_and_notice_without_MTProto_effect()
    {
        var owner = _host!.Services.GetRequiredService<ChannelCollectionSources>();
        var captured = await owner.CaptureFolderAsync(Ct);
        Assert.NotNull(captured);
        Assert.True((await owner.PublishFolderAsync(captured, [new TgChatInfo(123, 456, "Synthetic folder", "synthetic", ChannelKind.Channel, true)], Ct)).Ok);
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        var source = await context.TelegramChannels.SingleAsync(Ct);
        Assert.Equal(ChannelStatus.Joined, source.Status);
        Assert.Null(source.MembershipRequestId);
        await NotificationAdmissionAssertions.JoinedAsync(context, await context.Notifications.SingleAsync(Ct), Ct);
        Assert.Equal(0, _gateway.Effects);
    }

    [Theory]
    [InlineData("account")]
    [InlineData("decision")]
    [InlineData("ownership")]
    public async Task Folder_observation_refuses_changed_account_source_or_server_ownership(string change)
    {
        var owner = _host!.Services.GetRequiredService<ChannelCollectionSources>();
        var capture = await owner.CaptureFolderAsync(Ct);
        Assert.NotNull(capture);
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        if (change == "account") _gateway.AccountId = 99;
        else if (change == "decision")
        {
            context.TelegramChannels.Add(new TelegramChannel { Username = "synthetic", Status = ChannelStatus.Rejected, AddedAt = DateTimeOffset.UtcNow });
            await context.SaveChangesAsync(Ct);
        }
        else _host.Services.GetRequiredService<JobFinder.Pipeline.ServerOwnership>().Lost();
        var action = () => owner.PublishFolderAsync(capture, [new TgChatInfo(123, 456, "Synthetic", "synthetic", ChannelKind.Channel, true)], Ct);
        if (change == "ownership") await Assert.ThrowsAsync<InvalidOperationException>(action);
        else Assert.False((await action()).Ok);
        Assert.DoesNotContain(await context.TelegramChannels.ToListAsync(Ct), row => row.Status == ChannelStatus.Joined);
        Assert.Equal(0, _gateway.Effects);
    }

    [Fact]
    public async Task Folder_import_cannot_bypass_membership_root_and_failed_history_checks_original_witness_once()
    {
        var id = await AdmitAsync();
        var owner = _host!.Services.GetRequiredService<ChannelCollectionSources>();
        var capture = await owner.CaptureFolderAsync(Ct);
        Assert.NotNull(capture);
        Assert.True((await owner.PublishFolderAsync(capture, [new TgChatInfo(123, 456, "Synthetic", "synthetic", ChannelKind.Channel, true)], Ct)).Ok);
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        var source = await context.TelegramChannels.SingleAsync(Ct);
        Assert.Equal(ChannelStatus.Approved, source.Status);
        Assert.Equal(id, source.MembershipRequestId);
        for (var index = 0; index < 7; index++) await RunAsync(id);
        source = await context.TelegramChannels.AsNoTracking().SingleAsync(Ct);
        var snapshot = ChannelCollectionSnapshot.Capture(source);
        Assert.True(await owner.PublishAccessFailureAsync(snapshot, 42, "synthetic confirmed access refusal", Ct));
        var notices = await context.Notifications.CountAsync(Ct);
        Assert.False(await owner.PublishAccessFailureAsync(snapshot, 42, "synthetic confirmed access refusal", Ct));
        Assert.Equal(notices, await context.Notifications.CountAsync(Ct));
        Assert.Equal(ChannelStatus.Failed, (await context.TelegramChannels.AsNoTracking().SingleAsync(Ct)).Status);
        Assert.Equal(ChannelMembershipStage.Completed, (await ReadAsync(id)).Stage);
        Assert.Equal(4, _gateway.Effects);
    }
}
