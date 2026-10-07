using System.Text.Json;
using System.Reflection;
using JobFinder.Bot;
using JobFinder.Core.Domain;
using JobFinder.Core.Storage;
using JobFinder.Pipeline.Career;
using JobFinder.Pipeline.Actions;
using JobFinder.Pipeline.Modules.Engagement;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Telegram.Bot;
using Telegram.Bot.Types;

namespace JobFinder.Tests.Pipeline;

public sealed partial class OrdinaryNotificationAdmissionTests
{
    private async Task<(BotEdits Store, CapturedBotEdit Prompt)> BotPromptAsync(bool bind = true)
    {
        var (source, snapshot) = await ReplyDecisionAsync();
        var store = _host!.Services.GetRequiredService<BotEdits>();
        var prompt = await store.BeginAsync(123456, 123456, new BotEditPrompt { Kind = "reply", SourceId = source.Id, Reply = snapshot }, Ct);
        if (bind) prompt = await store.BindAsync(123456, 123456, prompt.Value.Id, 101, Ct);
        return (store, prompt);
    }

    [Fact]
    public async Task Persisted_bot_prompt_survives_a_new_store_without_rebasing_its_snapshot_or_user_text()
    {
        var (store, prompt) = await BotPromptAsync();
        const string text = "  Unsaved user answer\nwith another paragraph  ";
        var submitted = Assert.IsType<CapturedBotEdit>(await store.RecordReplyAsync(prompt, 900, text, Ct));
        var restarted = new BotEdits(pg.Db, TimeProvider.System);
        var read = Assert.IsType<CapturedBotEdit>(await restarted.FindAsync(123456, 123456, 101, null, null, Ct));
        Assert.Equal(prompt.Value.Reply, read.Value.Reply);
        Assert.Equal(text, read.Value.Draft);
        Assert.Equal(submitted.Revision, read.Revision);
        Assert.False(read.Value.Completed);
        Assert.Null(await restarted.FindAsync(654321, 123456, 101, null, null, Ct));
        Assert.Null(await restarted.FindAsync(123456, 654321, 101, null, null, Ct));
        Assert.Null(await restarted.FindAsync(123456, 123456, 101, null, 654321, Ct));
    }

    [Fact]
    public async Task A_trusted_reply_recovers_the_prompt_after_sdk_acceptance_before_binding_without_resending()
    {
        var (store, prompt) = await BotPromptAsync(bind: false);
        var text = BotEdits.Text("Synthetic prompt", prompt);
        Assert.Null(await store.FindAsync(123456, 123456, 101, text, 654321, Ct));
        var restarted = new BotEdits(pg.Db, TimeProvider.System);
        var recovered = Assert.IsType<CapturedBotEdit>(await restarted.FindAsync(123456, 123456, 101, text, 123456, Ct));
        Assert.Equal(prompt.Value.Id, recovered.Value.Id);
        Assert.Equal(prompt.Value.Reply, recovered.Value.Reply);
        Assert.Equal(101, recovered.Value.MessageId);
        var repeated = await store.BindAsync(123456, 123456, prompt.Value.Id, 101, Ct);
        Assert.Equal(recovered.Revision, repeated.Revision);
        await Assert.ThrowsAsync<BotEditPromptException>(() => store.BindAsync(123456, 123456, prompt.Value.Id, 102, Ct));
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        Assert.Equal(2, await context.Documents.CountAsync(row => row.Key.StartsWith("bot.edit."), Ct));
        Assert.Equal(0, _provider.Calls);
    }

    [Fact]
    public async Task A_new_prompt_cannot_take_an_existing_bot_chat_message_binding()
    {
        var (store, prompt) = await BotPromptAsync();
        var another = await store.BeginAsync(123456, 123456, prompt.Value with { }, Ct);
        await Assert.ThrowsAsync<BotEditPromptException>(() => store.BindAsync(123456, 123456, another.Value.Id, 101, Ct));
        var read = Assert.IsType<CapturedBotEdit>(await store.FindAsync(123456, 123456, 101, null, null, Ct));
        Assert.Equal(prompt.Value.Id, read.Value.Id);
        Assert.Null(await store.FindAsync(123456, 123456, 102, null, null, Ct));
    }

    [Theory]
    [InlineData("✏️ Пришлите текст письма для вакансии #1 ответом на это сообщение.")]
    [InlineData("✏️ Пришлите свой текст ответом на это сообщение — он уйдёт получателю как есть.")]
    [InlineData("Synthetic prompt\n\nКод редактирования: invalid")]
    public async Task Unowned_legacy_or_damaged_edit_prompts_never_fall_through_to_an_unrelated_conversation(string text)
    {
        var (store, _) = await BotPromptAsync();
        await Assert.ThrowsAsync<BotEditPromptException>(() => store.FindAsync(123456, 123456, 102, text, 123456, Ct));
        Assert.Null(await store.FindAsync(123456, 123456, 102, text, 654321, Ct));
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        await NoAdmissionAsync(context);
        Assert.Empty(await context.EmployerMessages.ToListAsync(Ct));
        Assert.Equal(0, _provider.Calls);
    }

    [Fact]
    public async Task Bot_reply_compare_exchange_preserves_newer_text_and_completion_cannot_erase_it()
    {
        var (store, prompt) = await BotPromptAsync();
        var winners = await Task.WhenAll(store.RecordReplyAsync(prompt, 900, "First text", Ct), store.RecordReplyAsync(prompt, 901, "Other text", Ct));
        var first = Assert.Single(winners, result => result is not null)!;
        var newer = Assert.IsType<CapturedBotEdit>(await store.RecordReplyAsync(first, 902, "Newer text", Ct));
        Assert.Null(await store.RecordReplyAsync(first, 903, "Stale writer", Ct));
        Assert.Null(await store.RecordReplyAsync(newer, 899, "Old update replay", Ct));
        Assert.False(await store.CompleteAsync(first, Ct));
        var read = Assert.IsType<CapturedBotEdit>(await store.FindAsync(123456, 123456, 101, null, null, Ct));
        Assert.Equal("Newer text", read.Value.Draft);
        Assert.False(read.Value.Completed);
        Assert.True(await store.CompleteAsync(newer, Ct));
        var terminal = Assert.IsType<CapturedBotEdit>(await store.FindAsync(123456, 123456, 101, null, null, Ct));
        Assert.True(terminal.Value.Completed);
        Assert.Null(await store.RecordReplyAsync(terminal, 904, "Terminal edit", Ct));
        Assert.False(await store.CompleteAsync(terminal, Ct));
    }

    [Theory]
    [InlineData("version")]
    [InlineData("kind")]
    [InlineData("source")]
    [InlineData("multiple")]
    [InlineData("witness")]
    [InlineData("shape")]
    [InlineData("binding")]
    [InlineData("missing")]
    public async Task Damaged_or_unsupported_owned_prompt_state_fails_closed_without_retiring_its_evidence(string damage)
    {
        var (store, prompt) = await BotPromptAsync();
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        var intent = await context.Documents.SingleAsync(row => row.Key.StartsWith("bot.edit.intent."), Ct);
        var binding = await context.Documents.SingleAsync(row => row.Key.StartsWith("bot.edit.message."), Ct);
        var changed = prompt.Value with { };
        switch (damage)
        {
            case "version": changed.Version = 999; break;
            case "kind": changed.Kind = "unknown"; break;
            case "source": changed.SourceId++; break;
            case "multiple": changed.Comment = new() { Id = changed.SourceId }; break;
            case "witness": changed.Reply = changed.Reply! with { SourceHash = "changed witness" }; break;
            case "shape": intent.Content = "{\"version\":[]}"; break;
            case "binding": binding.Content = "{}"; break;
            case "missing": context.Documents.Remove(intent); break;
        }
        if (damage is "version" or "kind" or "source" or "multiple" or "witness")
            intent.Content = JsonSerializer.Serialize(changed, BotEditJsonContext.Default.BotEditPrompt);
        await context.SaveChangesAsync(Ct);
        var before = await context.Documents.AsNoTracking().Where(row => row.Key.StartsWith("bot.edit.")).OrderBy(row => row.Key).Select(row => row.Content).ToListAsync(Ct);
        await Assert.ThrowsAsync<BotEditPromptException>(() => store.FindAsync(123456, 123456, 101, null, null, Ct));
        var after = await context.Documents.AsNoTracking().Where(row => row.Key.StartsWith("bot.edit.")).OrderBy(row => row.Key).Select(row => row.Content).ToListAsync(Ct);
        Assert.Equal(before, after);
        await NoAdmissionAsync(context);
        Assert.Empty(await context.EmployerMessages.ToListAsync(Ct));
    }

    [Fact]
    public async Task Generic_document_writes_cannot_replace_or_delete_owned_bot_prompt_state()
    {
        await BotPromptAsync();
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        var row = await context.Documents.AsNoTracking().SingleAsync(document => document.Key.StartsWith("bot.edit.intent."), Ct);
        var generic = _host!.Services.GetRequiredService<IDocumentStore>();
        await Assert.ThrowsAsync<InvalidOperationException>(() => generic.SaveAsync(row.Key, "{}", Ct));
        await Assert.ThrowsAsync<InvalidOperationException>(() => generic.AddAsync(row.Key, "{}", Ct));
        await Assert.ThrowsAsync<InvalidOperationException>(() => generic.DeleteAsync(row.Key, Ct));
        Assert.Equal(row.Content, (await context.Documents.AsNoTracking().SingleAsync(document => document.Key == row.Key, Ct)).Content);
    }

    [Fact]
    public async Task A_mutated_in_memory_capture_cannot_rewrite_the_persisted_prompt_witness()
    {
        var (store, prompt) = await BotPromptAsync();
        prompt.Value.Reply!.SourceHash = "different witness";
        await Assert.ThrowsAsync<BotEditPromptException>(() => store.RecordReplyAsync(prompt, 900, "User text", Ct));
        var saved = Assert.IsType<CapturedBotEdit>(await store.FindAsync(123456, 123456, 101, null, null, Ct));
        Assert.NotEqual(prompt.Value.Reply, saved.Value.Reply);
        Assert.Null(saved.Value.Draft);
    }

    [Fact]
    public async Task Actual_restarted_career_bot_recovers_an_accepted_but_unbound_edit_prompt_and_completes_it_once()
    {
        var (source, contact) = await OutreachDecisionAsync();
        var snapshot = JobFinder.Pipeline.Modules.Hiring.OutreachEditSnapshot.Capture(source, contact);
        var service = OutreachCareerBot();
        using var api = new OutreachBotApi { AcceptThenFailOnce = true };
        using var http = new HttpClient(api);
        var bot = new TelegramBotClient(new TelegramBotClientOptions("123456:synthetic-test-token") { RetryCount = 1, RetryThreshold = 0 }, http);
        var callback = new CallbackQuery { Id = "synthetic-prompt", From = new User { Id = 123456, FirstName = "Synthetic" } };
        Assert.True(await service.TryCallbackAsync(bot, callback, JobFinder.Pipeline.Notifications.CareerCallback.OutreachEdit, source.Id, snapshot.Version, Ct));
        Assert.Equal(0, api.Sends);
        await DeliverStoredQuestionAsync(api);
        Assert.Equal(1, api.Sends);
        var acceptedText = api.LastPromptText;
        var acceptedId = api.LastMessageId;
        service = OutreachCareerBot();
        var message = new Message
        {
            Id = 900, From = new User { Id = 123456 }, Chat = new Chat { Id = 123456 }, Text = "User-written LinkedIn note",
            ReplyToMessage = new Message { Id = acceptedId, From = new User { Id = 123456, IsBot = true }, Chat = new Chat { Id = 123456 }, Text = acceptedText },
        };
        Assert.True(await service.TryEditAsync(bot, message, Ct));
        Assert.True(await OutreachCareerBot().TryEditAsync(bot, message, Ct));
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        var saved = await context.Outreaches.SingleAsync(row => row.Id == source.Id, Ct);
        Assert.Equal(OutreachStatus.Sent, saved.Status);
        Assert.Equal(source.Revision + 1, saved.Revision);
        Assert.Equal("User-written LinkedIn note", saved.Text);
        var prompt = Assert.IsType<CapturedBotEdit>(await _host!.Services.GetRequiredService<BotEdits>().FindAsync(123456, 123456, acceptedId, null, null, Ct));
        Assert.True(prompt.Value.Completed);
        Assert.Equal(snapshot, prompt.Value.Outreach);
        await NotificationAdmissionAssertions.JoinedAsync(context, await context.Notifications.SingleAsync(row => row.Kind != NotificationKind.EditPrompt && row.EntityId != null, Ct), Ct);
        Assert.Empty(await context.DeliveryAttempts.ToListAsync(Ct));
        Assert.Equal(0, _provider.Calls);
    }

    [Theory]
    [InlineData("letter", false)]
    [InlineData("letter", true)]
    [InlineData("comment", false)]
    [InlineData("comment", true)]
    public async Task Actual_main_bot_uses_a_persisted_prompt_after_recreation_and_retains_conflicts_or_completes_once(string kind, bool stale)
    {
        var vacancy = await VacancyAsync();
        await HostAsync();
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        CommentDecisionSnapshot? comment = null;
        var sourceId = vacancy.Id;
        if (kind == "comment")
        {
            var draft = new CommentDraft
            {
                PostId = "synthetic-edit-" + Guid.NewGuid().ToString("N"), PostUrl = "https://example.invalid/synthetic-post",
                Author = "synthetic", PostText = "Synthetic post", CreatedAt = DateTimeOffset.UtcNow,
                Status = CommentStatus.Pending, Audience = AudienceKind.Recruiter, Revision = 2, Variants = ["Original variant"],
            };
            context.CommentDrafts.Add(draft);
            await context.SaveChangesAsync(Ct);
            sourceId = draft.Id;
            comment = CommentDecisions.Snapshot(draft);
        }
        using var first = PersistentMainBot();
        using var api = new OutreachBotApi();
        using var http = new HttpClient(api);
        var bot = new TelegramBotClient(new TelegramBotClientOptions("123456:synthetic-test-token"), http);
        var callback = new CallbackQuery { Id = "synthetic-main-prompt", From = new User { Id = 123456, FirstName = "Synthetic" } };
        await OutreachPageCall(first, "AskForTextAsync", bot, callback, kind, sourceId, "Synthetic prompt", Ct, comment!);
        Assert.Equal(0, api.Sends);
        await DeliverStoredQuestionAsync(api);
        var promptId = api.LastMessageId;
        var store = _host!.Services.GetRequiredService<BotEdits>();
        var before = Assert.IsType<CapturedBotEdit>(await store.FindAsync(123456, 123456, promptId, null, null, Ct));
        if (stale)
        {
            if (kind == "letter") await context.Vacancies.Where(row => row.Id == sourceId).ExecuteUpdateAsync(update => update
                .SetProperty(row => row.CoverLetter, "Concurrent letter").SetProperty(row => row.LetterRevision, row => row.LetterRevision + 1), Ct);
            else await context.CommentDrafts.Where(row => row.Id == sourceId).ExecuteUpdateAsync(update => update
                .SetProperty(row => row.Revision, row => row.Revision + 1), Ct);
        }
        using var restarted = PersistentMainBot();
        const string text = "Я бы заранее договаривался о дате следующего разговора.";
        var message = new Message
        {
            Id = 900, From = new User { Id = 123456 }, Chat = new Chat { Id = 123456 }, Text = text,
            ReplyToMessage = new Message { Id = promptId, Chat = new Chat { Id = 123456 }, From = new User { Id = 123456, IsBot = true } },
        };
        await OutreachPageCall(restarted, "OnMessageAsync", bot, message, Ct);
        await OutreachPageCall(restarted, "OnMessageAsync", bot, message, Ct);
        var saved = Assert.IsType<CapturedBotEdit>(await store.FindAsync(123456, 123456, promptId, null, null, Ct));
        Assert.Equal(!stale, saved.Value.Completed);
        Assert.Equal(text, saved.Value.Draft);
        Assert.Equal(before.Value.SnapshotHash, saved.Value.SnapshotHash);
        if (kind == "letter")
        {
            var current = await context.Vacancies.AsNoTracking().SingleAsync(row => row.Id == sourceId, Ct);
            Assert.Equal(stale ? "Concurrent letter" : text, current.CoverLetter);
            Assert.Equal(1, current.LetterRevision);
        }
        else
        {
            var current = await context.CommentDrafts.AsNoTracking().SingleAsync(row => row.Id == sourceId, Ct);
            Assert.Equal(stale ? CommentStatus.Pending : CommentStatus.Approved, current.Status);
            Assert.Equal(stale ? null : text, current.ChosenText);
            Assert.Equal(3, current.Revision);
        }
        Assert.Empty(await context.DeliveryAttempts.ToListAsync(Ct));
        Assert.Equal(0, _provider.Calls);
    }

    [Fact]
    public async Task A_bot_edit_keeps_its_text_and_original_witness_when_native_source_admission_rolls_back()
    {
        var (source, contact) = await OutreachDecisionAsync();
        var snapshot = JobFinder.Pipeline.Modules.Hiring.OutreachEditSnapshot.Capture(source, contact);
        using var api = new OutreachBotApi();
        using var http = new HttpClient(api);
        var bot = new TelegramBotClient(new TelegramBotClientOptions("123456:synthetic-test-token"), http);
        var service = OutreachCareerBot();
        var callback = new CallbackQuery { Id = "synthetic-admission", From = new User { Id = 123456, FirstName = "Synthetic" } };
        await service.TryCallbackAsync(bot, callback, JobFinder.Pipeline.Notifications.CareerCallback.OutreachEdit, source.Id, snapshot.Version, Ct);
        await DeliverStoredQuestionAsync(api);
        var promptId = api.LastMessageId;
        var message = new Message
        {
            Id = 900, From = new User { Id = 123456 }, Chat = new Chat { Id = 123456 }, Text = "User note",
            ReplyToMessage = new Message { Id = promptId, Chat = new Chat { Id = 123456 } },
        };
        _fault.Active = true;
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.TryEditAsync(bot, message, Ct));
        var store = _host!.Services.GetRequiredService<BotEdits>();
        var saved = Assert.IsType<CapturedBotEdit>(await store.FindAsync(123456, 123456, promptId, null, null, Ct));
        Assert.False(saved.Value.Completed);
        Assert.Equal("User note", saved.Value.Draft);
        Assert.Equal(snapshot, saved.Value.Outreach);
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        Assert.Equal(OutreachStatus.Draft, (await context.Outreaches.AsNoTracking().SingleAsync(row => row.Id == source.Id, Ct)).Status);
        await NoSourceAdmissionBeyondQuestionAsync(context);
        _fault.Active = false;
        Assert.True(await OutreachCareerBot().TryEditAsync(bot, message, Ct));
        Assert.True(Assert.IsType<CapturedBotEdit>(await store.FindAsync(123456, 123456, promptId, null, null, Ct)).Value.Completed);
        Assert.Equal(OutreachStatus.Sent, (await context.Outreaches.AsNoTracking().SingleAsync(row => row.Id == source.Id, Ct)).Status);
        await NotificationAdmissionAssertions.JoinedAsync(context, await context.Notifications.SingleAsync(row => row.Kind != NotificationKind.EditPrompt && row.EntityId != null, Ct), Ct);
        Assert.Empty(await context.DeliveryAttempts.ToListAsync(Ct));
    }

    private BotService PersistentMainBot()
    {
        var constructor = typeof(BotService).GetConstructors().Single();
        var arguments = constructor.GetParameters().Select(parameter => parameter.ParameterType == typeof(BotOptions)
            ? (object)new BotOptions { OwnerUserId = 123456 }
            : parameter.ParameterType == typeof(CareerBot) ? OutreachCareerBot() : _host!.Services.GetService(parameter.ParameterType)).ToArray();
        return (BotService)constructor.Invoke(arguments);
    }
}
