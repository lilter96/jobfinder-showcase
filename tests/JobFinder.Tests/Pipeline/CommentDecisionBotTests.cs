using System.Reflection;
using JobFinder.Bot;
using JobFinder.Core.Domain;
using JobFinder.Pipeline.Actions;
using JobFinder.Pipeline.Modules.Engagement;
using JobFinder.Pipeline.Notifications;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Telegram.Bot;
using Telegram.Bot.Types;

namespace JobFinder.Tests.Pipeline;

public sealed partial class DurableApplicationDeliveryIntegrationTests
{
    [Fact]
    public async Task Actual_bot_cancel_callback_requires_the_queued_comment_revision_and_never_sends()
    {
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        var draft = new CommentDraft
        {
            PostId = "bot-cancel", PostUrl = "https://www.threads.com/@author/post/bot-cancel", Author = "author", PostText = "Синтетический пост",
            ChosenText = "Я бы заранее договаривался о сроке ответа.", Status = CommentStatus.Approved, Revision = 3, CreatedAt = DateTimeOffset.UtcNow,
        };
        context.CommentDrafts.Add(draft);
        await context.SaveChangesAsync(Ct);
        var recipient = new Recipient();
        var producer = await HostAsync(false, new Gate(), recipient);
        var id = (await producer.Services.GetRequiredService<CommentDeliveryRequests>().RequestAsync(draft.Id, Ct))!.Value;
        var constructor = typeof(BotService).GetConstructors().Single();
        var arguments = constructor.GetParameters().Select(parameter => parameter.ParameterType == typeof(BotOptions)
            ? (object)new BotOptions { OwnerUserId = 123456 } : producer.Services.GetService(parameter.ParameterType)).ToArray();
        using var service = (BotService)constructor.Invoke(arguments);
        using var handler = new SyntheticBotApi();
        using var http = new HttpClient(handler);
        var bot = new TelegramBotClient(new TelegramBotClientOptions("123456:synthetic-test-token"), http);
        var callback = new CallbackQuery { Id = "synthetic-cancel", From = new User { Id = 123456, FirstName = "Synthetic" }, Data = "c:cancel:" + draft.Id + ":0:2" };
        await InvokeBotAsync(service, "OnCallbackAsync", bot, callback, Ct);
        Assert.Equal(CommentDeliveryStage.Preparing, (await context.CommentDeliveryRequests.AsNoTracking().SingleAsync(row => row.Id == id, Ct)).Stage);
        callback.Data = "c:cancel:" + draft.Id + ":0:3";
        await InvokeBotAsync(service, "OnCallbackAsync", bot, callback, Ct);
        Assert.Equal(CommentDeliveryStage.Cancelled, (await context.CommentDeliveryRequests.AsNoTracking().SingleAsync(row => row.Id == id, Ct)).Stage);
        Assert.Equal(CommentStatus.Rejected, (await context.CommentDrafts.AsNoTracking().SingleAsync(Ct)).Status);
        Assert.Contains(handler.CallbackAnswers, text => text.Contains("черновик изменился", StringComparison.Ordinal));
        Assert.Empty(await context.CommentDeliveryAttempts.ToListAsync(Ct));
        Assert.Empty(recipient.Deliveries);
    }

    [Fact]
    public async Task Actual_bot_comment_prompt_and_old_card_keep_the_captured_revision_after_a_concurrent_decision()
    {
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        await context.CommentDrafts.ExecuteDeleteAsync(Ct);
        var draft = new CommentDraft
        {
            PostId = "synthetic-bot-post", PostUrl = "https://threads.invalid/post/synthetic-bot-post", Author = "synthetic-author",
            PostText = "Синтетический пост о сроках ответа", Status = CommentStatus.Pending, Audience = AudienceKind.Recruiter,
            CreatedAt = DateTimeOffset.UtcNow, Variants = ["А какой срок ответа вы обычно называете кандидату?"], Revision = 3,
        };
        context.CommentDrafts.Add(draft);
        await context.SaveChangesAsync(Ct);
        var recipient = new Recipient();
        var producer = await HostAsync(false, new Gate(), recipient);
        var approvals = producer.Services.GetRequiredService<ApprovalService>();
        var constructor = typeof(BotService).GetConstructors().Single();
        var arguments = constructor.GetParameters().Select(parameter => parameter.ParameterType == typeof(BotOptions)
            ? (object)new BotOptions { OwnerUserId = 123456 }
            : parameter.ParameterType == typeof(OwnerBotMessages)
                ? new OwnerBotMessages(new BotOptions { OwnerUserId = 123456 }, producer.Services.GetRequiredService<NotificationDeliveryRequests>(), TimeProvider.System)
            : producer.Services.GetService(parameter.ParameterType)).ToArray();
        using var service = (BotService)constructor.Invoke(arguments);
        using var handler = new SyntheticBotApi();
        using var http = new HttpClient(handler);
        var bot = new TelegramBotClient(new TelegramBotClientOptions("123456:synthetic-test-token"), http);
        var card = Cards.CommentDraft(draft);
        var editButton = card.Buttons.SelectMany(row => row).Single(button => button.Contains("|c:edit:", StringComparison.Ordinal));
        var callback = new CallbackQuery
        {
            Id = "synthetic-callback", From = new User { Id = 123456, FirstName = "Synthetic" }, Data = editButton.Split('|')[1],
        };
        await InvokeBotAsync(service, "OnCallbackAsync", bot, callback, Ct);
        var edit = Assert.IsType<JobFinder.Pipeline.Career.CapturedBotEdit>(await producer.Services.GetRequiredService<JobFinder.Pipeline.Career.BotEdits>()
            .FindAsync(123456, 123456, 50, (await context.Notifications.AsNoTracking().SingleAsync(row => row.Kind == NotificationKind.EditPrompt, Ct)).Text, 123456, Ct));
        Assert.True((await approvals.RejectCommentAsync(CommentDecisions.Snapshot(draft), Ct)).Ok);
        await InvokeBotAsync(service, "OnEditReplyAsync", bot,
            new Message { Id = 901, Text = "Я бы заранее договаривался о дате следующего разговора.", Chat = new Chat { Id = 123456 } }, edit, Ct);
        callback.Data = card.Buttons[0][0].Split('|')[1];
        await InvokeBotAsync(service, "OnCallbackAsync", bot, callback, Ct);
        var stored = await context.CommentDrafts.AsNoTracking().SingleAsync(Ct);
        Assert.Equal(CommentStatus.Rejected, stored.Status);
        Assert.Equal(4, stored.Revision);
        Assert.Null(stored.ChosenText);
        Assert.Contains(await context.Notifications.AsNoTracking().ToListAsync(Ct), row => row.Text.Contains("черновик изменился", StringComparison.Ordinal));
        Assert.Empty(handler.Texts);
        Assert.Contains(handler.CallbackAnswers, text => text.Contains("черновик изменился", StringComparison.Ordinal));
        Assert.Empty(recipient.Deliveries);
    }
}
