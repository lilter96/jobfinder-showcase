using System.Text.Json;
using JobFinder.Core.Domain;
using JobFinder.Pipeline.Modules.Engagement;
using JobFinder.Pipeline.Notifications;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace JobFinder.Tests.Pipeline;

public sealed partial class NativeNotificationDeliveryTests
{
    private async Task<CommentDraft> ExpiringDraftAsync()
    {
        var draft = new CommentDraft
        {
            PostId = "expiry-" + Guid.NewGuid().ToString("N"), PostUrl = "https://threads.invalid/post/expiry", Author = "synthetic",
            PostText = "Синтетический пост", Status = CommentStatus.Pending, CreatedAt = DateTimeOffset.UtcNow,
            PostPublishedAt = DateTimeOffset.UtcNow.AddDays(-90), Revision = 1,
        };
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        context.CommentDrafts.Add(draft);
        await context.SaveChangesAsync(Ct);
        return draft;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Real_expiry_owner_retires_a_card_before_or_during_the_SDK_effect_without_losing_its_primary_receipt(bool during)
    {
        var producer = await HostAsync(false);
        var draft = await ExpiringDraftAsync();
        var notice = await NoticeAsync();
        notice.Kind = NotificationKind.CommentDraft;
        notice.EntityId = draft.Id;
        var id = await producer.Services.GetRequiredService<NotificationDeliveryRequests>().PublishAsync(notice, Ct);
        var witness = CommentExpirySnapshot.Capture(draft);
        async Task ExpireAsync() => Assert.True(await producer.Services.GetRequiredService<CommentExpiry>().ExpireAsync(witness, Ct));
        if (during)
            _api.DuringSend = async _ =>
            {
                _api.DuringSend = null;
                await ExpireAsync();
            };
        else await ExpireAsync();
        await HostAsync(true);
        await UntilAsync(async () => (await RootAsync(id)).Stage == NotificationDeliveryStage.Completed);
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        await UntilAsync(async () => await context.Notifications.AnyAsync(row => row.EditParentNotificationId == notice.Id, Ct));
        var edit = await context.Notifications.SingleAsync(row => row.EditParentNotificationId == notice.Id, Ct);
        Assert.NotNull(edit.DeliveryRequestId);
        await UntilAsync(async () => (await RootAsync(edit.DeliveryRequestId.Value)).Stage == NotificationDeliveryStage.Completed);
        var card = await StoredAsync(id);
        Assert.Equal(card.TelegramMessageId, edit.EditKeyboardMessageId);
        var receipt = await context.NotificationDeliveryAttempts.SingleAsync(row => row.RequestId == id && row.EffectIndex == 0, Ct);
        Assert.Equal(DeliveryAttemptStatus.Confirmed, receipt.Status);
        Assert.Equal(card.TelegramMessageId, receipt.TelegramMessageId);
        var retired = await RootAsync(edit.DeliveryRequestId.Value);
        Assert.Equal(NotificationDeliveryInputs.Input(await RootAsync(id)).Target, NotificationDeliveryInputs.Input(retired).Target);
        var editReceipt = await context.NotificationDeliveryAttempts.SingleAsync(row => row.RequestId == retired.Id, Ct);
        Assert.Equal(DeliveryAttemptStatus.Confirmed, editReceipt.Status);
        var effect = JsonSerializer.Deserialize(editReceipt.EffectJson, NotificationDeliveryJsonContext.Default.NotificationEffect)!;
        Assert.Equal(NotificationEffectKind.EditKeyboard, effect.Kind);
        Assert.Equal(card.TelegramMessageId, effect.MessageId);
        Assert.Single(_api.Calls, call => call.Method == "editMessageReplyMarkup");
        Assert.Equal(2, (await context.CommentDrafts.SingleAsync(row => row.Id == draft.Id, Ct)).Revision);
        Assert.Empty(await context.CommentDeliveryAttempts.ToListAsync(Ct));
    }

    [Fact]
    public async Task Expiry_after_confirmed_delivery_binds_the_retirement_to_that_exact_card_receipt()
    {
        var host = await HostAsync(true);
        var draft = await ExpiringDraftAsync();
        var notice = await NoticeAsync();
        notice.Kind = NotificationKind.CommentDraft;
        notice.EntityId = draft.Id;
        var id = await host.Services.GetRequiredService<NotificationDeliveryRequests>().PublishAsync(notice, Ct);
        await UntilAsync(async () => (await RootAsync(id)).Stage == NotificationDeliveryStage.Completed);
        Assert.True(await host.Services.GetRequiredService<CommentExpiry>().ExpireAsync(CommentExpirySnapshot.Capture(draft), Ct));
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        var edit = await context.Notifications.SingleAsync(row => row.EditParentNotificationId == notice.Id, Ct);
        await UntilAsync(async () => (await RootAsync(edit.DeliveryRequestId!.Value)).Stage == NotificationDeliveryStage.Completed);
        Assert.Equal((await StoredAsync(id)).TelegramMessageId, edit.EditKeyboardMessageId);
        var primary = await context.NotificationDeliveryAttempts.SingleAsync(row => row.RequestId == id, Ct);
        Assert.Equal(DeliveryAttemptStatus.Confirmed, primary.Status);
        Assert.Single(_api.Calls, call => call.Method == "editMessageReplyMarkup");
    }

    [Fact]
    public async Task A_legacy_card_message_id_alone_cannot_authorize_an_expiry_keyboard_effect()
    {
        var producer = await HostAsync(false);
        var draft = await ExpiringDraftAsync();
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        var card = new Notification
        {
            Kind = NotificationKind.CommentDraft, Text = "legacy synthetic card", Status = NotificationStatus.Sent,
            EntityId = draft.Id, CreatedAt = DateTimeOffset.UtcNow, TelegramMessageId = 777,
        };
        context.Notifications.Add(card);
        await context.SaveChangesAsync(Ct);
        Assert.True(await producer.Services.GetRequiredService<CommentExpiry>().ExpireAsync(CommentExpirySnapshot.Capture(draft), Ct));
        var edit = await context.Notifications.SingleAsync(row => row.EditParentNotificationId == card.Id, Ct);
        await HostAsync(true);
        await UntilAsync(async () => (await RootAsync(edit.DeliveryRequestId!.Value)).Stage == NotificationDeliveryStage.Failed);
        Assert.DoesNotContain(_api.Calls, call => call.Method == "editMessageReplyMarkup");
        Assert.Empty(await context.NotificationDeliveryAttempts.Where(row => row.RequestId == edit.DeliveryRequestId).ToListAsync(Ct));
        Assert.Equal(NotificationStatus.Sent, (await context.Notifications.AsNoTracking().SingleAsync(row => row.Id == card.Id, Ct)).Status);
    }
}
