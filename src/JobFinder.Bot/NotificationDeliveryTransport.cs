using JobFinder.Core.Domain;
using JobFinder.Pipeline.Notifications;
using Telegram.Bot;
using Telegram.Bot.Exceptions;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.ReplyMarkups;

namespace JobFinder.Bot;

public sealed class NotificationDeliveryTransport(BotOptions options, TelegramBotClientHolder holder) : INotificationDeliveryTransport
{
    public NotificationTarget? CaptureTarget()
    {
        return options.Enabled && holder.DeliveryClient?.BotId is long botId && botId > 0 && options.OwnerUserId > 0
            ? new() { ChatId = options.OwnerUserId, BotId = botId } : null;
    }

    public async Task<NotificationEffectReceipt> SendAsync(NotificationTarget target, NotificationEffect effect, byte[]? attachment,
        Func<CancellationToken, Task<bool>> maySend, CancellationToken ct)
    {
        if (CaptureTarget() != target || !await maySend(ct))
            return new() { Status = DeliveryAttemptStatus.KnownNotSent, Error = "notification ownership, source or account permission refused" };
        var bot = holder.DeliveryClient!;
        var mode = effect.Html ? ParseMode.Html : ParseMode.None;
        var markup = effect.Buttons.Count > 0 ? NotificationDispatcher.Keyboard(effect.Buttons) : null;
        try
        {
            if (effect.Kind == NotificationEffectKind.EditKeyboard)
            {
                try
                {
                    var edited = await bot.EditMessageReplyMarkup(target.ChatId, effect.MessageId!.Value, NotificationDispatcher.Keyboard(effect.Buttons), cancellationToken: ct);
                    if (edited.Chat.Id != target.ChatId || edited.MessageId != effect.MessageId)
                        return new() { Status = DeliveryAttemptStatus.OutcomeUnknown, Error = "Telegram edit receipt does not identify the requested message" };
                }
                catch (ApiRequestException error) when (error.ErrorCode == 400 && error.Message.Contains("not modified", StringComparison.OrdinalIgnoreCase)) { }
                return new() { Status = DeliveryAttemptStatus.Confirmed, MessageId = effect.MessageId };
            }
            Message message;
            if (effect.Kind == NotificationEffectKind.Document)
            {
                if (attachment is null) return new() { Status = DeliveryAttemptStatus.KnownNotSent, Error = "notification attachment buffer is missing" };
                await using var file = new MemoryStream(attachment, writable: false);
                message = await bot.SendDocument(target.ChatId, InputFile.FromStream(file, effect.AttachmentName!), effect.Text.Length == 0 ? null : effect.Text,
                    mode, replyMarkup: markup, cancellationToken: ct);
            }
            else if (effect.Kind == NotificationEffectKind.EditText)
                message = await bot.EditMessageText(target.ChatId, effect.MessageId!.Value, effect.Text, mode,
                    linkPreviewOptions: new LinkPreviewOptions { IsDisabled = true }, replyMarkup: markup, cancellationToken: ct);
            else if (effect.Kind == NotificationEffectKind.Text)
                message = await bot.SendMessage(target.ChatId, effect.Text, mode, replyMarkup: markup,
                    linkPreviewOptions: new LinkPreviewOptions { IsDisabled = true }, cancellationToken: ct);
            else if (effect.Kind == NotificationEffectKind.EditPrompt)
                message = await bot.SendMessage(target.ChatId, effect.Text, ParseMode.None,
                    replyMarkup: new ForceReplyMarkup { InputFieldPlaceholder = "Текст…" },
                    linkPreviewOptions: new LinkPreviewOptions { IsDisabled = true }, cancellationToken: ct);
            else return new() { Status = DeliveryAttemptStatus.KnownNotSent, Error = "unsupported notification effect" };
            return message.MessageId > 0 && message.Chat.Id == target.ChatId ? new() { Status = DeliveryAttemptStatus.Confirmed, MessageId = message.MessageId }
                : new() { Status = DeliveryAttemptStatus.OutcomeUnknown, Error = "Telegram accepted a request without a message identity" };
        }
        catch (ApiRequestException error) when (error.ErrorCode == 429)
        {
            return new() { Status = DeliveryAttemptStatus.KnownNotSent, Error = "Telegram explicitly rate limited this effect", RetrySeconds = Math.Clamp(error.Parameters?.RetryAfter ?? 5, 1, 3600) };
        }
        catch (ApiRequestException error) when (error.ErrorCode is >= 400 and < 500 && error.ErrorCode != 408)
        {
            return new() { Status = DeliveryAttemptStatus.KnownNotSent, Error = error.Message,
                ParseRejected = error.ErrorCode == 400 && effect.Html && effect.Kind != NotificationEffectKind.EditKeyboard && error.Message.Contains("parse", StringComparison.OrdinalIgnoreCase) };
        }
        catch (Exception)
        {
            return new() { Status = DeliveryAttemptStatus.OutcomeUnknown, Error = "Telegram request has no reliable effect receipt" };
        }
    }
}
