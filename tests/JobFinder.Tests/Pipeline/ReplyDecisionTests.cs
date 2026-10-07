using System.Net;
using System.Reflection;
using System.Text.Json;
using JobFinder.Bot;
using JobFinder.Core.Domain;
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
using PipelinePage = JobFinder.App.Components.Pages.Pipeline;

namespace JobFinder.Tests.Pipeline;

public sealed partial class OrdinaryNotificationAdmissionTests
{
    private async Task<(ApplicationEvent Source, ReplyEditSnapshot Snapshot)> ReplyDecisionAsync()
    {
        var source = await ReplySourceAsync();
        var host = await HostAsync();
        var snapshot = Assert.IsType<ReplyEditSnapshot>(await host.Services.GetRequiredService<ReplyDraftSources>().CaptureEditAsync(source.Id, null, Ct));
        return (source, snapshot);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Captured_reply_dismissal_commits_source_and_native_notice_once_or_rolls_back(bool fail)
    {
        var (source, snapshot) = await ReplyDecisionAsync();
        var service = _host!.Services.GetRequiredService<ReplyService>();
        _fault.Active = fail;
        if (fail) await Assert.ThrowsAsync<InvalidOperationException>(() => service.DismissDraftAsync(source.Id, Ct, snapshot));
        else
        {
            var results = await Task.WhenAll(service.DismissDraftAsync(source.Id, Ct, snapshot), service.DismissDraftAsync(source.Id, Ct, snapshot));
            Assert.Single(results, result => result.Ok);
        }
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        var saved = await context.ApplicationEvents.SingleAsync(row => row.Id == source.Id, Ct);
        Assert.Equal(fail ? DraftStatus.Pending : DraftStatus.Dismissed, saved.DraftStatus);
        Assert.Equal(source.Revision + (fail ? 0 : 1), saved.Revision);
        Assert.Equal(source.Draft, saved.Draft);
        if (fail) await NoAdmissionAsync(context);
        else await NotificationAdmissionAssertions.JoinedAsync(context, await context.Notifications.SingleAsync(Ct), Ct);
        Assert.Empty(await context.DeliveryAttempts.ToListAsync(Ct));
        Assert.Equal(0, _provider.Calls);
    }

    [Theory]
    [InlineData("revision")]
    [InlineData("draft")]
    [InlineData("status")]
    [InlineData("author")]
    [InlineData("text")]
    [InlineData("channel")]
    [InlineData("external")]
    [InlineData("classification")]
    [InlineData("kind")]
    [InlineData("time")]
    [InlineData("application-revision")]
    [InlineData("application-status")]
    [InlineData("application-channel")]
    [InlineData("target")]
    [InlineData("peer")]
    [InlineData("email")]
    [InlineData("stage")]
    public async Task Reply_controls_do_not_rebase_stale_text_or_a_changed_recipient(string changed)
    {
        var (source, snapshot) = await ReplyDecisionAsync();
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        var current = await context.ApplicationEvents.SingleAsync(row => row.Id == source.Id, Ct);
        var application = await context.Applications.SingleAsync(row => row.Id == source.ApplicationId, Ct);
        switch (changed)
        {
            case "revision": current.Revision++; break;
            case "draft": current.Draft = "Concurrent text"; break;
            case "status": current.DraftStatus = DraftStatus.Sent; break;
            case "author": current.Author = "other@example.invalid"; break;
            case "text": current.Text = "Different question"; break;
            case "channel": current.Channel = EventChannel.Telegram; break;
            case "external": current.ExternalId = "Different inbound ID"; break;
            case "classification": current.Classification = ReplyClass.Rejection; break;
            case "kind": current.Kind = EventKind.MessageOut; break;
            case "time": current.At = current.At.AddMinutes(1); break;
            case "application-revision": application.Revision++; break;
            case "application-status": application.Status = ApplicationStatus.OutcomeUnknown; break;
            case "application-channel": application.Channel = ApplicationChannel.TelegramDm; break;
            case "target": application.Target = "other@example.invalid"; break;
            case "peer": application.TargetPeerId = 99; break;
            case "email": application.EmailMessageId = "Different reply-to ID"; break;
            case "stage": application.Stage = ApplicationStage.Rejected; break;
        }
        await context.SaveChangesAsync(Ct);
        var owner = _host!.Services.GetRequiredService<ReplyDraftSources>();
        Assert.Null(await owner.CaptureEditAsync(source.Id, snapshot.Version, Ct));
        Assert.Null(await owner.CaptureEditAsync(source.Id, 0, Ct));
        var service = _host.Services.GetRequiredService<ReplyService>();
        Assert.False((await service.SendReplyAsync(source.Id, "Unsaved user answer", Ct, expected: snapshot)).Ok);
        Assert.False((await service.DismissDraftAsync(source.Id, Ct, snapshot)).Ok);
        var saved = await context.ApplicationEvents.AsNoTracking().SingleAsync(row => row.Id == source.Id, Ct);
        Assert.Equal(current.Revision, saved.Revision);
        Assert.Equal(current.Draft, saved.Draft);
        Assert.Equal(current.DraftStatus, saved.DraftStatus);
        Assert.Empty(await context.EmployerMessages.ToListAsync(Ct));
        await NoAdmissionAsync(context);
        Assert.Equal(0, _provider.Calls);
    }

    [Theory]
    [InlineData(EmployerMessageStatus.Pending, false)]
    [InlineData(EmployerMessageStatus.Ready, false)]
    [InlineData(EmployerMessageStatus.Pending, true)]
    [InlineData(EmployerMessageStatus.Ready, true)]
    public async Task Reply_dismissal_cancels_matching_native_requests_atomically_with_its_notice(EmployerMessageStatus status, bool fail)
    {
        var (source, snapshot) = await ReplyDecisionAsync();
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        var message = new EmployerMessage
        {
            Id = Guid.NewGuid(), VacancyId = snapshot.VacancyId, ApplicationId = snapshot.ApplicationId,
            BusinessKey = Guid.NewGuid().ToString("N"), Status = status, CreatedAt = DateTimeOffset.UtcNow,
            InputJson = JsonSerializer.Serialize(new EmployerMessageInput
            {
                VacancyId = snapshot.VacancyId, EventId = source.Id, ApplicationId = source.ApplicationId, Kind = EmployerMessageKind.Reply,
                Channel = ApplicationChannel.Email, Target = "synthetic@example.invalid", Text = source.Draft!, SourceRevision = source.Revision,
            }, EmployerMessageJsonContext.Default.EmployerMessageInput),
        };
        context.EmployerMessages.Add(message);
        await context.SaveChangesAsync(Ct);
        _fault.Active = fail;
        var owner = _host!.Services.GetRequiredService<ReplyDraftSources>();
        if (fail) await Assert.ThrowsAsync<InvalidOperationException>(() => owner.DismissAsync(snapshot, Ct));
        else Assert.True((await owner.DismissAsync(snapshot, Ct)).Ok);
        Assert.Equal(fail ? status : EmployerMessageStatus.Cancelled,
            (await context.EmployerMessages.AsNoTracking().SingleAsync(row => row.Id == message.Id, Ct)).Status);
        Assert.Equal(fail ? DraftStatus.Pending : DraftStatus.Dismissed,
            (await context.ApplicationEvents.AsNoTracking().SingleAsync(row => row.Id == source.Id, Ct)).DraftStatus);
        if (fail) await NoAdmissionAsync(context);
        else await NotificationAdmissionAssertions.JoinedAsync(context, await context.Notifications.SingleAsync(Ct), Ct);
    }

    [Theory]
    [InlineData(EmployerMessageStatus.Sending)]
    [InlineData(EmployerMessageStatus.OutcomeUnknown)]
    public async Task Unlinked_uncertain_delivery_refuses_a_captured_reply_dismissal(EmployerMessageStatus status)
    {
        var (source, snapshot) = await ReplyDecisionAsync();
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        context.EmployerMessages.Add(new EmployerMessage
        {
            Id = Guid.NewGuid(), VacancyId = snapshot.VacancyId, BusinessKey = Guid.NewGuid().ToString("N"),
            Status = status, InputJson = "{}", CreatedAt = DateTimeOffset.UtcNow,
        });
        await context.SaveChangesAsync(Ct);
        Assert.False((await _host!.Services.GetRequiredService<ReplyService>().DismissDraftAsync(source.Id, Ct, snapshot)).Ok);
        Assert.Equal(DraftStatus.Pending, (await context.ApplicationEvents.SingleAsync(row => row.Id == source.Id, Ct)).DraftStatus);
        await NoAdmissionAsync(context);
    }

    [Fact]
    public async Task A_stale_redraft_control_is_refused_before_paid_analysis()
    {
        var (source, snapshot) = await ReplyDecisionAsync();
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        await context.ApplicationEvents.Where(row => row.Id == source.Id).ExecuteUpdateAsync(update => update
            .SetProperty(row => row.Revision, row => row.Revision + 1), Ct);
        Assert.False((await _host!.Services.GetRequiredService<ReplyService>().RedraftAsync(source.Id, Ct, expected: snapshot)).Ok);
        Assert.Equal(0, _provider.Calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Reply_dismissal_cannot_reinterpret_a_damaged_or_unsupported_pending_operation(bool unsupported)
    {
        var (source, snapshot) = await ReplyDecisionAsync();
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        var message = new EmployerMessage
        {
            Id = Guid.NewGuid(), VacancyId = snapshot.VacancyId, BusinessKey = Guid.NewGuid().ToString("N"),
            Status = EmployerMessageStatus.Pending, CreatedAt = DateTimeOffset.UtcNow,
            InputJson = unsupported ? JsonSerializer.Serialize(new EmployerMessageInput
            {
                Version = 999, VacancyId = snapshot.VacancyId, EventId = source.Id, Kind = EmployerMessageKind.Reply,
                Target = "synthetic@example.invalid", Text = source.Draft!,
            }, EmployerMessageJsonContext.Default.EmployerMessageInput) : "{\"vacancyId\":[]}",
        };
        context.EmployerMessages.Add(message);
        await context.SaveChangesAsync(Ct);
        Assert.False((await _host!.Services.GetRequiredService<ReplyDraftSources>().DismissAsync(snapshot, Ct)).Ok);
        Assert.Equal(DraftStatus.Pending, (await context.ApplicationEvents.SingleAsync(row => row.Id == source.Id, Ct)).DraftStatus);
        Assert.Equal(EmployerMessageStatus.Pending, (await context.EmployerMessages.AsNoTracking().SingleAsync(Ct)).Status);
        await NoAdmissionAsync(context);
    }

    [Fact]
    public async Task Reply_and_follow_up_cards_carry_the_full_source_and_recipient_version_on_every_control()
    {
        var (source, snapshot) = await ReplyDecisionAsync();
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        var application = await context.Applications.Include(row => row.Vacancy).SingleAsync(row => row.Id == source.ApplicationId, Ct);
        var cards = new[] { CareerCards.Reply(application, source, ReplyAnalysis(), null), CareerCards.FollowUp(application, source, 3) };
        foreach (var card in cards)
        foreach (var button in card.Buttons.SelectMany(row => row).Where(button => button.Contains("|r:", StringComparison.Ordinal)))
            Assert.EndsWith($":{source.Id}:{snapshot.Version}", button);
        Assert.All(cards, card => Assert.Contains(card.Buttons.SelectMany(row => row), button => button.Contains(CareerCallback.ReplySkip, StringComparison.Ordinal)));
    }

    [Fact]
    public async Task Actual_pipeline_editor_retains_text_and_snapshot_on_conflict_until_explicit_reload()
    {
        var (source, _) = await ReplyDecisionAsync();
        var capture = new ReplyPageCapture();
        using var services = new ServiceCollection().AddLogging().AddSingleton<IComponentActivator>(capture)
            .AddSingleton(pg.Db).AddSingleton(_host!.Services.GetRequiredService<ReplyService>())
            .AddSingleton(_host.Services.GetRequiredService<FunnelService>()).BuildServiceProvider();
        await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());
        await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var rendered = await renderer.RenderComponentAsync<PipelinePage>(ParameterView.Empty);
            var texts = (Dictionary<long, string>)OutreachPageField(capture.Page, "_draftText");
            var displayed = ((Dictionary<long, ReplyEditSnapshot>)OutreachPageField(capture.Page, "_snapshots"))[source.Id];
            texts[source.Id] = "Unsaved user answer";
            await using var context = await pg.Db.CreateDbContextAsync(Ct);
            await context.ApplicationEvents.Where(row => row.Id == source.Id).ExecuteUpdateAsync(update => update
                .SetProperty(row => row.Draft, "Concurrent answer").SetProperty(row => row.Revision, row => row.Revision + 1), Ct);
            await OutreachPageCall(capture.Page, "SendDraft", source.Id);
            typeof(ComponentBase).GetMethod("StateHasChanged", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(capture.Page, null);
            var html = WebUtility.HtmlDecode(rendered.ToHtmlString());
            Assert.Contains("Unsaved user answer", html);
            Assert.DoesNotContain("Concurrent answer", html);
            Assert.Equal(displayed, ((Dictionary<long, ReplyEditSnapshot>)OutreachPageField(capture.Page, "_snapshots"))[source.Id]);
            await OutreachPageCall(capture.Page, "Dismiss", source.Id);
            Assert.Equal("Unsaved user answer", ((Dictionary<long, string>)OutreachPageField(capture.Page, "_draftText"))[source.Id]);
            await NoAdmissionAsync(context);
            await OutreachPageCall(capture.Page, "ReloadAsync");
            Assert.Equal("Concurrent answer", ((Dictionary<long, string>)OutreachPageField(capture.Page, "_draftText"))[source.Id]);
            await OutreachPageCall(capture.Page, "Dismiss", source.Id);
            Assert.Equal(DraftStatus.Dismissed, (await context.ApplicationEvents.SingleAsync(row => row.Id == source.Id, Ct)).DraftStatus);
            await NotificationAdmissionAssertions.JoinedAsync(context, await context.Notifications.SingleAsync(Ct), Ct);
        });
    }

    [Theory]
    [InlineData(CareerCallback.ReplySend)]
    [InlineData(CareerCallback.ReplySkip)]
    [InlineData(CareerCallback.ReplyEdit)]
    [InlineData(CareerCallback.ReplyRedraft)]
    public async Task Actual_bot_refuses_legacy_and_stale_reply_controls(string action)
    {
        var (source, snapshot) = await ReplyDecisionAsync();
        var service = OutreachCareerBot();
        using var api = new OutreachBotApi();
        using var http = new HttpClient(api);
        var bot = new TelegramBotClient(new TelegramBotClientOptions("123456:synthetic-test-token"), http);
        var callback = new CallbackQuery { Id = "synthetic-reply", From = new User { Id = 123456, FirstName = "Synthetic" } };
        Assert.True(await service.TryCallbackAsync(bot, callback, action, source.Id, 0, Ct));
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        await context.ApplicationEvents.Where(row => row.Id == source.Id).ExecuteUpdateAsync(update => update
            .SetProperty(row => row.Revision, row => row.Revision + 1), Ct);
        Assert.True(await service.TryCallbackAsync(bot, callback, action, source.Id, snapshot.Version, Ct));
        Assert.Equal(DraftStatus.Pending, (await context.ApplicationEvents.SingleAsync(row => row.Id == source.Id, Ct)).DraftStatus);
        Assert.Empty(await context.EmployerMessages.ToListAsync(Ct));
        if (action is CareerCallback.ReplySend or CareerCallback.ReplyRedraft)
        {
            var notices = await context.Notifications.ToListAsync(Ct);
            Assert.Equal(2, notices.Count);
            foreach (var notice in notices)
            {
                Assert.Contains("карточка устарела", notice.Text);
                await NotificationAdmissionAssertions.JoinedAsync(context, notice, Ct, new NotificationTarget { ChatId = 123456, BotId = 123456 });
            }
            Assert.Empty(await context.DeliveryAttempts.ToListAsync(Ct));
        }
        else await NoAdmissionAsync(context);
        Assert.Equal(0, _provider.Calls);
    }

    [Fact]
    public async Task Actual_bot_reply_edit_preserves_the_original_prompt_witness_after_a_recipient_change()
    {
        var (source, snapshot) = await ReplyDecisionAsync();
        var service = OutreachCareerBot();
        using var api = new OutreachBotApi();
        using var http = new HttpClient(api);
        var bot = new TelegramBotClient(new TelegramBotClientOptions("123456:synthetic-test-token"), http);
        var callback = new CallbackQuery { Id = "synthetic-reply-edit", From = new User { Id = 123456, FirstName = "Synthetic" } };
        Assert.True(await service.TryCallbackAsync(bot, callback, CareerCallback.ReplyEdit, source.Id, snapshot.Version, Ct));
        Assert.Equal(0, api.Sends);
        await DeliverStoredQuestionAsync(api);
        var promptId = api.LastMessageId;
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        await context.Applications.Where(row => row.Id == source.ApplicationId).ExecuteUpdateAsync(update => update
            .SetProperty(row => row.Target, "new@example.invalid"), Ct);
        service = OutreachCareerBot();
        var message = new Message
        {
            Id = 901, Chat = new Chat { Id = 123456 }, From = new User { Id = 123456 }, Text = "User reply",
            ReplyToMessage = new Message { Id = promptId, Chat = new Chat { Id = 123456 } },
        };
        Assert.True(await service.TryEditAsync(bot, message, Ct));
        Assert.True(await service.TryEditAsync(bot, message, Ct));
        var savedPrompt = Assert.IsType<CapturedBotEdit>(await _host!.Services.GetRequiredService<BotEdits>().FindAsync(123456, 123456, promptId, null, null, Ct));
        Assert.False(savedPrompt.Value.Completed);
        Assert.Equal("User reply", savedPrompt.Value.Draft);
        Assert.Equal(snapshot, savedPrompt.Value.Reply);
        Assert.Equal(source.Draft, (await context.ApplicationEvents.SingleAsync(row => row.Id == source.Id, Ct)).Draft);
        Assert.Empty(await context.EmployerMessages.ToListAsync(Ct));
        await NoSourceAdmissionBeyondQuestionAsync(context);
        Assert.Equal(0, _provider.Calls);
    }

    private sealed class ReplyPageCapture : IComponentActivator
    {
        public PipelinePage Page { get; } = new();
        public IComponent CreateInstance(Type type) => type == typeof(PipelinePage) ? Page : (IComponent)Activator.CreateInstance(type)!;
    }
}
