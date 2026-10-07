using JobFinder.Bot;
using JobFinder.Core.Domain;
using JobFinder.Pipeline.Career;
using JobFinder.Pipeline.Notifications;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Telegram.Bot;
using Telegram.Bot.Types;

namespace JobFinder.Tests.Pipeline;

public sealed partial class OrdinaryNotificationAdmissionTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Career_bot_output_admits_complete_text_and_document_atomically_without_an_inline_SDK_send(bool fail)
    {
        await HostAsync();
        using var api = new OutreachBotApi();
        using var http = new HttpClient(api);
        var bot = new TelegramBotClient(new TelegramBotClientOptions("123456:synthetic-test-token"), http);
        var text = "<b>" + new string('я', 1500) + "</b>";
        var reply = new BotReply(text, [["Synthetic|noop:0:0"]], "data/synthetic-missing-report.pdf", "report.pdf");
        _fault.Active = fail;
        if (fail) await Assert.ThrowsAsync<InvalidOperationException>(() => OutreachCareerBot().SendAsync(bot, reply, Ct));
        else await OutreachCareerBot().SendAsync(bot, reply, Ct);
        Assert.Equal(0, api.Sends);
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        if (fail) await NoAdmissionAsync(context);
        else
        {
            var notice = await context.Notifications.SingleAsync(Ct);
            Assert.Equal(text, notice.Text);
            Assert.Equal(reply.AttachmentPath, notice.AttachmentPath);
            Assert.Equal(reply.AttachmentName, notice.AttachmentName);
            Assert.Equal(reply.Buttons!.Single(), notice.Buttons.Single());
            await NotificationAdmissionAssertions.JoinedAsync(context, notice, Ct, new NotificationTarget { BotId = 123456, ChatId = 123456 });
            var input = NotificationDeliveryInputs.Input(await context.NotificationDeliveryRequests.SingleAsync(Ct));
            Assert.Equal(123456, input.Target!.BotId);
            Assert.Equal(123456, input.Target.ChatId);
            Assert.Equal(text, input.Text);
        }
        Assert.Equal(0, _provider.Calls);
    }

    [Theory]
    [InlineData(123456, 654321)]
    [InlineData(654321, 123456)]
    public async Task Main_bot_does_not_publish_content_for_an_unowned_author_or_non_private_chat(long author, long chatId)
    {
        await HostAsync();
        using var api = new OutreachBotApi();
        using var http = new HttpClient(api);
        var bot = new TelegramBotClient(new TelegramBotClientOptions("123456:synthetic-test-token"), http);
        using var service = PersistentMainBot();
        await OutreachPageCall(service, "OnMessageAsync", bot,
            new Message { Id = 1, Chat = new Chat { Id = chatId }, From = new User { Id = author }, Text = "/start" }, Ct);
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        await NoAdmissionAsync(context);
        Assert.Equal(0, api.Sends);
        Assert.Equal(0, _provider.Calls);
    }

    [Fact]
    public async Task Main_bot_help_response_is_published_without_an_inline_SDK_send()
    {
        await HostAsync();
        using var api = new OutreachBotApi();
        using var http = new HttpClient(api);
        var bot = new TelegramBotClient(new TelegramBotClientOptions("123456:synthetic-test-token"), http);
        using var service = PersistentMainBot();
        await OutreachPageCall(service, "OnMessageAsync", bot,
            new Message { Id = 1, Chat = new Chat { Id = 123456 }, From = new User { Id = 123456 }, Text = "/help" }, Ct);
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        var notice = await context.Notifications.SingleAsync(Ct);
        Assert.Contains("/status", notice.Text);
        await NotificationAdmissionAssertions.JoinedAsync(context, notice, Ct, new NotificationTarget { BotId = 123456, ChatId = 123456 });
        var vacancy = await VacancyAsync();
        await OutreachPageCall(service, "SendVacancyCardAsync", bot, vacancy.Id, Ct, Array.Empty<string>());
        var card = await context.Notifications.SingleAsync(row => row.EntityId == vacancy.Id, Ct);
        Assert.NotEmpty(card.Buttons);
        await NotificationAdmissionAssertions.JoinedAsync(context, card, Ct, new NotificationTarget { BotId = 123456, ChatId = 123456 });
        Assert.Equal(0, api.Sends);
        Assert.Equal(0, _provider.Calls);
    }
}
