using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;
using JobFinder.Bot;
using JobFinder.Core.Domain;
using JobFinder.Core.Text;
using JobFinder.Data;
using JobFinder.Pipeline.Career;
using JobFinder.Pipeline.Modules.Hiring;
using JobFinder.Pipeline.Notifications;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Telegram.Bot;
using Telegram.Bot.Types;
using ContactsPage = JobFinder.App.Components.Pages.Contacts;

namespace JobFinder.Tests.Pipeline;

public sealed partial class OrdinaryNotificationAdmissionTests
{
    private async Task<(Outreach Source, CompanyContact Contact)> OutreachDecisionAsync(ContactKind kind = ContactKind.LinkedIn)
    {
        var (vacancy, contact) = await OutreachSourceAsync(kind: kind);
        var source = new Outreach
        {
            VacancyId = vacancy.Id, ContactId = contact.Id, Text = "Original note", Status = OutreachStatus.Draft,
            Revision = 2, CreatedAt = DateTimeOffset.UtcNow,
        };
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        context.Outreaches.Add(source);
        await context.SaveChangesAsync(Ct);
        return (source, contact);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task Captured_outreach_decision_and_notice_commit_once_or_roll_back(bool manualSent, bool fail)
    {
        var (source, contact) = await OutreachDecisionAsync();
        var snapshot = OutreachEditSnapshot.Capture(source, contact);
        var service = _host!.Services.GetRequiredService<OutreachService>();
        _fault.Active = fail;
        Task<JobFinder.Pipeline.Actions.ActionResult> DecideAsync() => manualSent
            ? service.SendAsync(source.Id, Ct, "User-written note", snapshot)
            : service.DismissAsync(source.Id, Ct, snapshot);
        if (fail) await Assert.ThrowsAsync<InvalidOperationException>(DecideAsync);
        else
        {
            var decisions = await Task.WhenAll(DecideAsync(), DecideAsync());
            Assert.Single(decisions, result => result.Ok);
        }
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        var saved = await context.Outreaches.SingleAsync(row => row.Id == source.Id, Ct);
        Assert.Equal(fail ? OutreachStatus.Draft : manualSent ? OutreachStatus.Sent : OutreachStatus.Dismissed, saved.Status);
        Assert.Equal(source.Revision + (fail ? 0 : 1), saved.Revision);
        Assert.Equal(!fail && manualSent ? "User-written note" : source.Text, saved.Text);
        if (fail) await NoAdmissionAsync(context);
        else await NotificationAdmissionAssertions.JoinedAsync(context, await context.Notifications.SingleAsync(Ct), Ct);
        Assert.Empty(await context.DeliveryAttempts.ToListAsync(Ct));
        Assert.Equal(0, _provider.Calls);
    }

    [Theory]
    [InlineData("revision")]
    [InlineData("text")]
    [InlineData("contact")]
    [InlineData("fresh")]
    [InlineData("sending")]
    [InlineData("unknown")]
    public async Task Outreach_card_and_displayed_snapshot_cannot_rebase_after_a_change(string changed)
    {
        var (source, contact) = await OutreachDecisionAsync();
        var snapshot = OutreachEditSnapshot.Capture(source, contact);
        var owner = _host!.Services.GetRequiredService<OutreachSources>();
        Assert.NotNull(await owner.CaptureEditAsync(source.Id, snapshot.Version, Ct));
        Assert.Null(await owner.CaptureEditAsync(source.Id, 0, Ct));
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        var current = await context.Outreaches.SingleAsync(row => row.Id == source.Id, Ct);
        switch (changed)
        {
            case "revision": current.Revision++; break;
            case "text": current.Text = "Concurrent note"; break;
            case "contact":
                var currentContact = await context.CompanyContacts.SingleAsync(row => row.Id == contact.Id, Ct);
                currentContact.Value += "-changed";
                break;
            case "fresh": current.Fresh = true; break;
            case "sending": current.Status = OutreachStatus.Sending; break;
            case "unknown": current.Status = OutreachStatus.OutcomeUnknown; break;
        }
        await context.SaveChangesAsync(Ct);
        Assert.Null(await owner.CaptureEditAsync(source.Id, snapshot.Version, Ct));
        var service = _host!.Services.GetRequiredService<OutreachService>();
        Assert.False((await service.SendAsync(source.Id, Ct, "Stale user note", snapshot)).Ok);
        Assert.False((await service.DismissAsync(source.Id, Ct, snapshot)).Ok);
        await NoAdmissionAsync(context);
        Assert.Equal(current.Text, (await context.Outreaches.AsNoTracking().SingleAsync(row => row.Id == source.Id, Ct)).Text);
        Assert.Equal(0, _provider.Calls);
    }

    [Theory]
    [InlineData(EmployerMessageStatus.Pending, false)]
    [InlineData(EmployerMessageStatus.Ready, false)]
    [InlineData(EmployerMessageStatus.Pending, true)]
    public async Task Cancelling_a_queued_outreach_commits_source_request_and_notice_together(EmployerMessageStatus status, bool fail)
    {
        var (source, contact) = await OutreachDecisionAsync(ContactKind.Email);
        var snapshot = OutreachEditSnapshot.Capture(source, contact);
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        var message = new EmployerMessage
        {
            Id = Guid.NewGuid(), VacancyId = source.VacancyId, BusinessKey = Guid.NewGuid().ToString("N"), Status = status,
            CreatedAt = DateTimeOffset.UtcNow,
            InputJson = JsonSerializer.Serialize(new EmployerMessageInput
            {
                VacancyId = source.VacancyId, OutreachId = source.Id, ContactId = contact.Id, Kind = EmployerMessageKind.Outreach,
                Channel = ApplicationChannel.Email, Target = contact.Value, Text = source.Text, SourceRevision = source.Revision,
            }, EmployerMessageJsonContext.Default.EmployerMessageInput),
        };
        context.EmployerMessages.Add(message);
        await context.SaveChangesAsync(Ct);
        _fault.Active = fail;
        var owner = _host!.Services.GetRequiredService<OutreachSources>();
        if (fail) await Assert.ThrowsAsync<InvalidOperationException>(() => owner.DecideAsync(snapshot, false, null, Ct));
        else Assert.True((await owner.DecideAsync(snapshot, false, null, Ct)).Ok);
        Assert.Equal(fail ? status : EmployerMessageStatus.Cancelled,
            (await context.EmployerMessages.AsNoTracking().SingleAsync(row => row.Id == message.Id, Ct)).Status);
        Assert.Equal(fail ? OutreachStatus.Draft : OutreachStatus.Dismissed,
            (await context.Outreaches.AsNoTracking().SingleAsync(row => row.Id == source.Id, Ct)).Status);
        if (fail) await NoAdmissionAsync(context);
        else await NotificationAdmissionAssertions.JoinedAsync(context, await context.Notifications.SingleAsync(Ct), Ct);
    }

    [Theory]
    [InlineData(EmployerMessageStatus.Sending)]
    [InlineData(EmployerMessageStatus.OutcomeUnknown)]
    public async Task An_unlinked_uncertain_message_fences_manual_outreach_decisions(EmployerMessageStatus status)
    {
        var (source, contact) = await OutreachDecisionAsync();
        var snapshot = OutreachEditSnapshot.Capture(source, contact);
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        context.EmployerMessages.Add(new EmployerMessage
        {
            Id = Guid.NewGuid(), VacancyId = source.VacancyId, BusinessKey = Guid.NewGuid().ToString("N"),
            Status = status, InputJson = "{}", CreatedAt = DateTimeOffset.UtcNow,
        });
        await context.SaveChangesAsync(Ct);
        var owner = _host!.Services.GetRequiredService<OutreachSources>();
        Assert.False((await owner.DecideAsync(snapshot, true, "User note", Ct)).Ok);
        Assert.False((await owner.DecideAsync(snapshot, false, null, Ct)).Ok);
        await NoAdmissionAsync(context);
    }

    [Fact]
    public async Task Employer_admission_refuses_an_unlinked_initial_unknown_root_before_preparation_or_publication()
    {
        var source = await ReplySourceAsync();
        var host = await HostAsync();
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        context.ApplicationDeliveryRequests.Add(new ApplicationDeliveryRequest
        {
            Id = Guid.NewGuid(), ApplicationId = source.ApplicationId, ApplicationRevision = 999,
            Stage = ApplicationDeliveryStage.OutcomeUnknown, InputJson = "{}", InputHash = CanonicalJson.Hash("{}"), CreatedAt = DateTimeOffset.UtcNow,
        });
        await context.SaveChangesAsync(Ct);
        Assert.False((await host.Services.GetRequiredService<EmployerMessages>().ReplyAsync(source.Id, null, false, 0, Ct)).Ok);
        Assert.Empty(await context.EmployerMessages.ToListAsync(Ct));
        await NoAdmissionAsync(context);
        Assert.Equal(0, _provider.Calls);
    }

    [Theory]
    [InlineData(DeliveryAttemptStatus.Submitting)]
    [InlineData(DeliveryAttemptStatus.OutcomeUnknown)]
    public async Task Employer_admission_refuses_a_rootless_initial_unresolved_attempt(DeliveryAttemptStatus status)
    {
        var source = await ReplySourceAsync();
        var host = await HostAsync();
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        context.DeliveryAttempts.Add(new DeliveryAttempt
        {
            Id = Guid.NewGuid(), ApplicationId = source.ApplicationId, ApplicationRevision = 999, OperationId = Guid.NewGuid(),
            Text = "Unresolved effect", TextHash = new string('a', 64), Status = status, CreatedAt = DateTimeOffset.UtcNow,
        });
        await context.SaveChangesAsync(Ct);
        Assert.False((await host.Services.GetRequiredService<EmployerMessages>().ReplyAsync(source.Id, null, false, 0, Ct)).Ok);
        Assert.Empty(await context.EmployerMessages.ToListAsync(Ct));
        await NoAdmissionAsync(context);
        Assert.Equal(0, _provider.Calls);
    }

    [Fact]
    public async Task Actual_contacts_page_retains_typed_text_and_displayed_snapshot_until_explicit_reload()
    {
        var (source, _) = await OutreachDecisionAsync();
        var capture = new OutreachPageCapture();
        using var services = new ServiceCollection().AddLogging().AddSingleton<IComponentActivator>(capture)
            .AddSingleton(pg.Db).AddSingleton(_host!.Services.GetRequiredService<OutreachService>())
            .AddSingleton(_host.Services.GetRequiredService<ContactRegistry>()).BuildServiceProvider();
        await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());
        await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var rendered = await renderer.RenderComponentAsync<ContactsPage>(ParameterView.Empty);
            var texts = (Dictionary<long, string>)OutreachPageField(capture.Page, "_texts");
            var snapshots = (Dictionary<long, OutreachEditSnapshot>)OutreachPageField(capture.Page, "_snapshots");
            var displayed = snapshots[source.Id];
            texts[source.Id] = "Unsaved user note";
            await using var context = await pg.Db.CreateDbContextAsync(Ct);
            await context.Outreaches.Where(row => row.Id == source.Id).ExecuteUpdateAsync(update => update
                .SetProperty(row => row.Text, "Concurrent note").SetProperty(row => row.Revision, row => row.Revision + 1), Ct);
            await OutreachPageCall(capture.Page, "Send", source.Id);
            typeof(ComponentBase).GetMethod("StateHasChanged", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(capture.Page, null);
            var html = WebUtility.HtmlDecode(rendered.ToHtmlString());
            Assert.Contains("Unsaved user note", html);
            Assert.DoesNotContain("Concurrent note", html);
            Assert.Equal(displayed, ((Dictionary<long, OutreachEditSnapshot>)OutreachPageField(capture.Page, "_snapshots"))[source.Id]);
            await NoAdmissionAsync(context);
            await OutreachPageCall(capture.Page, "ReloadAsync");
            Assert.Equal("Concurrent note", ((Dictionary<long, string>)OutreachPageField(capture.Page, "_texts"))[source.Id]);
            await OutreachPageCall(capture.Page, "Send", source.Id);
            Assert.Equal(OutreachStatus.Sent, (await context.Outreaches.SingleAsync(row => row.Id == source.Id, Ct)).Status);
            await NotificationAdmissionAssertions.JoinedAsync(context, await context.Notifications.SingleAsync(Ct), Ct);
        });
    }

    [Fact]
    public async Task Actual_career_bot_rejects_stale_and_legacy_card_tokens_before_applying_manual_sent()
    {
        var (source, contact) = await OutreachDecisionAsync();
        var snapshot = OutreachEditSnapshot.Capture(source, contact);
        var service = OutreachCareerBot();
        using var api = new OutreachBotApi();
        using var http = new HttpClient(api);
        var bot = new TelegramBotClient(new TelegramBotClientOptions("123456:synthetic-test-token"), http);
        var callback = new CallbackQuery { Id = "synthetic-outreach", From = new User { Id = 123456, FirstName = "Synthetic" } };
        Assert.True(await service.TryCallbackAsync(bot, callback, CareerCallback.OutreachSend, source.Id, 0, Ct));
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        var notices = await context.Notifications.ToListAsync(Ct);
        Assert.Single(notices);
        foreach (var notice in notices)
        {
            Assert.Contains("карточка устарела", notice.Text);
            await NotificationAdmissionAssertions.JoinedAsync(context, notice, Ct, new NotificationTarget { ChatId = 123456, BotId = 123456 });
        }
        Assert.Empty(await context.EmployerMessages.ToListAsync(Ct));
        await context.Outreaches.Where(row => row.Id == source.Id).ExecuteUpdateAsync(update => update.SetProperty(row => row.Revision, row => row.Revision + 1), Ct);
        Assert.True(await service.TryCallbackAsync(bot, callback, CareerCallback.OutreachSend, source.Id, snapshot.Version, Ct));
        notices = await context.Notifications.ToListAsync(Ct);
        Assert.Equal(2, notices.Count);
        foreach (var notice in notices)
        {
            Assert.Contains("карточка устарела", notice.Text);
            await NotificationAdmissionAssertions.JoinedAsync(context, notice, Ct, new NotificationTarget { ChatId = 123456, BotId = 123456 });
        }
        Assert.Empty(await context.EmployerMessages.ToListAsync(Ct));
        var current = await context.Outreaches.AsNoTracking().SingleAsync(row => row.Id == source.Id, Ct);
        var fresh = OutreachEditSnapshot.Capture(current, contact);
        Assert.True(await service.TryCallbackAsync(bot, callback, CareerCallback.OutreachSend, source.Id, fresh.Version, Ct));
        Assert.Equal(OutreachStatus.Sent, (await context.Outreaches.AsNoTracking().SingleAsync(row => row.Id == source.Id, Ct)).Status);
        await NotificationAdmissionAssertions.JoinedAsync(context, await context.Notifications.SingleAsync(row => !row.Text.Contains("карточка устарела"), Ct), Ct);
        Assert.Empty(await context.DeliveryAttempts.ToListAsync(Ct));
    }

    [Fact]
    public async Task Actual_career_bot_edit_keeps_the_prompt_snapshot_and_does_not_remove_it_on_conflict()
    {
        var (source, contact) = await OutreachDecisionAsync();
        var snapshot = OutreachEditSnapshot.Capture(source, contact);
        var service = OutreachCareerBot();
        using var api = new OutreachBotApi();
        using var http = new HttpClient(api);
        var bot = new TelegramBotClient(new TelegramBotClientOptions("123456:synthetic-test-token"), http);
        var callback = new CallbackQuery { Id = "synthetic-edit", From = new User { Id = 123456, FirstName = "Synthetic" } };
        Assert.True(await service.TryCallbackAsync(bot, callback, CareerCallback.OutreachEdit, source.Id, snapshot.Version, Ct));
        Assert.Equal(0, api.Sends);
        await DeliverStoredQuestionAsync(api);
        var promptId = api.LastMessageId;
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        await context.Outreaches.Where(row => row.Id == source.Id).ExecuteUpdateAsync(update => update
            .SetProperty(row => row.Text, "Concurrent note").SetProperty(row => row.Revision, row => row.Revision + 1), Ct);
        service = OutreachCareerBot();
        var message = new Message { Id = 900, Chat = new Chat { Id = 123456 }, From = new User { Id = 123456 }, Text = "User note",
            ReplyToMessage = new Message { Id = promptId, Chat = new Chat { Id = 123456 } } };
        Assert.True(await service.TryEditAsync(bot, message, Ct));
        Assert.True(await service.TryEditAsync(bot, message, Ct));
        Assert.Equal("Concurrent note", (await context.Outreaches.SingleAsync(row => row.Id == source.Id, Ct)).Text);
        await NoSourceAdmissionBeyondQuestionAsync(context);
        var savedPrompt = Assert.IsType<CapturedBotEdit>(await _host!.Services.GetRequiredService<BotEdits>().FindAsync(123456, 123456, promptId, null, null, Ct));
        Assert.False(savedPrompt.Value.Completed);
        Assert.Equal("User note", savedPrompt.Value.Draft);
        Assert.Equal(snapshot, savedPrompt.Value.Outreach);
        Assert.Equal(0, _provider.Calls);
    }

    private CareerBot OutreachCareerBot()
    {
        var constructor = typeof(CareerBot).GetConstructors().Single();
        var arguments = constructor.GetParameters().Select(parameter => parameter.ParameterType == typeof(BotOptions)
            ? (object)new BotOptions { OwnerUserId = 123456 } : _host!.Services.GetService(parameter.ParameterType)).ToArray();
        return (CareerBot)constructor.Invoke(arguments);
    }

    private static object OutreachPageField(object page, string field) => page.GetType().GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(page)!;
    private static Task OutreachPageCall(object page, string method, params object[] arguments) =>
        (Task)page.GetType().GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(page, arguments)!;

    private sealed class OutreachPageCapture : IComponentActivator
    {
        public ContactsPage Page { get; } = new();
        public IComponent CreateInstance(Type type) => type == typeof(ContactsPage) ? Page : (IComponent)Activator.CreateInstance(type)!;
    }

    private sealed class OutreachBotApi : HttpMessageHandler
    {
        public int LastMessageId { get; private set; } = 100;
        public bool AcceptThenFailOnce { get; set; }
        public string LastPromptText { get; private set; } = "";
        public int Sends { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Assert.Equal("api.telegram.org", request.RequestUri!.Host);
            var method = request.RequestUri.AbsolutePath.Split('/').Last().ToLowerInvariant();
            Assert.Contains(method, new[] { "sendmessage", "answercallbackquery", "editmessagereplymarkup" });
            if (method is "sendmessage" or "editmessagereplymarkup")
            {
                using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
                Assert.Equal(123456, body.RootElement.GetProperty("chat_id").GetInt64());
                if (method == "sendmessage")
                {
                    LastPromptText = body.RootElement.GetProperty("text").GetString()!;
                    Sends++;
                    LastMessageId++;
                    if (AcceptThenFailOnce)
                    {
                        AcceptThenFailOnce = false;
                        throw new HttpRequestException("synthetic owner chat accepted the prompt but the response was lost");
                    }
                }
            }
            var result = request.RequestUri!.AbsolutePath.EndsWith("sendMessage", StringComparison.OrdinalIgnoreCase)
                ? JsonSerializer.Serialize(new { ok = true, result = new { message_id = LastMessageId, date = 1, chat = new { id = 123456, type = "private" }, text = "Synthetic owner-chat response" } })
                : "{\"ok\":true,\"result\":true}";
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(result, Encoding.UTF8, "application/json") };
        }
    }
}
