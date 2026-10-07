using JobFinder.Core.Domain;
using JobFinder.Pipeline.Notifications;
using Telegram.Bot;

namespace JobFinder.Bot;

public sealed class OwnerBotMessages(BotOptions options, NotificationDeliveryRequests notifications, TimeProvider time)
{
    public Task<Guid?> EditKeyboardAsync(ITelegramBotClient bot, long chatId, int messageId, List<List<string>> buttons, CancellationToken ct,
        string? replaceCallback = null)
    {
        if (bot.BotId <= 0 || chatId <= 0 || chatId != options.OwnerUserId)
            throw new InvalidOperationException("bot keyboard requires its captured private owner destination");
        return notifications.PublishKeyboardAsync(new NotificationTarget { BotId = bot.BotId, ChatId = chatId }, messageId, buttons, ct, replaceCallback);
    }

    public Task<Guid> PublishAsync(ITelegramBotClient bot, long chatId, string text, CancellationToken ct,
        List<List<string>>? buttons = null, string? attachmentPath = null, string? attachmentName = null, long? entityId = null)
    {
        if (bot.BotId <= 0 || chatId <= 0 || chatId != options.OwnerUserId)
            throw new InvalidOperationException("bot output requires its captured private owner destination");
        return notifications.PublishCapturedAsync(new Notification
        {
            Kind = NotificationKind.Info, Text = text, Buttons = buttons?.Select(row => row.ToList()).ToList() ?? [], AttachmentPath = attachmentPath,
            AttachmentName = attachmentName, Status = NotificationStatus.Pending, CreatedAt = time.GetUtcNow(),
            EntityId = entityId,
        }, new NotificationTarget { BotId = bot.BotId, ChatId = chatId }, ct);
    }
}
