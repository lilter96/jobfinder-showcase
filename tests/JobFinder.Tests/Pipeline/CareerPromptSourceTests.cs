using JobFinder.Core.Domain;
using JobFinder.Bot;
using JobFinder.Data;
using JobFinder.Llm.Tasks;
using JobFinder.Pipeline.Career;
using JobFinder.Pipeline.Modules.Preparation;
using JobFinder.Pipeline.Notifications;
using JobFinder.Pipeline.Settings;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Telegram.Bot;
using Telegram.Bot.Types;

namespace JobFinder.Tests.Pipeline;

public sealed partial class InterviewSourceIntegrationTests
{
    [Theory]
    [InlineData(MockInterviews.Mode)]
    [InlineData(StoryBank.Mode)]
    [InlineData(OfferAdvisor.Mode)]
    public async Task Career_prompt_starts_preserve_every_other_active_source_and_raw_answers(string mode)
    {
        var source = await InterviewAsync();
        await SourcesAsync();
        var store = _host!.Services.GetRequiredService<ConversationStore>();
        var previous = new ConversationState(mode, 17, 2, "Original question", ["Original answer"], "Original draft") { MockRequestId = Guid.NewGuid() };
        await store.SetAsync(previous, Ct);
        var before = ConversationStore.Capture((await store.GetAsync(Ct))!);
        var chat = _host.Services.GetRequiredService<CareerChat>();
        Assert.Contains("активный диалог", (await chat.AskTimeAsync(source.Id, Ct)).Text);
        Assert.Contains("активный диалог", (await chat.AskDebriefAsync(source.Id, Ct)).Text);
        Assert.Contains("активный диалог", (await chat.AskOfferAsync(Ct)).Text);
        Assert.Equal(before, ConversationStore.Capture((await store.GetAsync(Ct))!));
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        Assert.Empty(await context.Notifications.ToListAsync(Ct));
        Assert.Empty(await context.LlmCalls.ToListAsync(Ct));
    }

    [Fact]
    public async Task Concurrent_career_prompts_publish_one_captured_dialogue_and_native_notice()
    {
        var source = await InterviewAsync();
        await SourcesAsync();
        var chat = _host!.Services.GetRequiredService<CareerChat>();
        var replies = await Task.WhenAll(chat.AskTimeAsync(source.Id, Ct), chat.AskDebriefAsync(source.Id, Ct), chat.AskOfferAsync(Ct));
        Assert.Single(replies, reply => reply.Text.Length == 0);
        var state = (await _host.Services.GetRequiredService<ConversationStore>().GetAsync(Ct))!;
        Assert.NotEqual(Guid.Empty, state.Id);
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        var notice = Assert.Single(await context.Notifications.ToListAsync(Ct));
        Assert.Equal(state.EntityId > 0 ? state.EntityId : (long?)null, notice.EntityId);
        await NotificationAdmissionAssertions.JoinedAsync(context, notice, Ct);
    }

    [Fact]
    public async Task Career_prompt_notice_failure_rolls_back_the_slot_and_retries_the_same_displayed_source()
    {
        var source = await InterviewAsync();
        var fault = new PromptNoticeFault();
        await SourcesAsync(fault);
        fault.Enabled = true;
        var chat = _host!.Services.GetRequiredService<CareerChat>();
        var version = CareerPromptSources.ControlVersion(source);
        await Assert.ThrowsAsync<IOException>(() => chat.AskTimeAsync(source.Id, Ct, version));
        Assert.False(await _host.Services.GetRequiredService<ConversationStore>().HasStateAsync(Ct));
        await using (var context = await pg.Db.CreateDbContextAsync(Ct))
        {
            Assert.Empty(await context.Notifications.ToListAsync(Ct));
            Assert.Empty(await context.NotificationDeliveryRequests.ToListAsync(Ct));
        }
        fault.Enabled = false;
        Assert.Empty((await chat.AskTimeAsync(source.Id, Ct, version)).Text);
        Assert.True((await _host.Services.GetRequiredService<ConversationStore>().GetAsync(Ct))!.Interview!.Matches(await ReadAsync(source.Id)));
    }

    [Theory]
    [InlineData("legacy")]
    [InlineData("source")]
    [InlineData("header")]
    public async Task Interview_prompt_buttons_refuse_legacy_and_changed_displayed_witnesses(string change)
    {
        var source = await InterviewAsync();
        await SourcesAsync();
        var version = CareerPromptSources.ControlVersion(source);
        await using (var context = await pg.Db.CreateDbContextAsync(Ct))
        {
            if (change == "source") await context.Interviews.ExecuteUpdateAsync(update => update.SetProperty(row => row.PreparationRevision, 4), Ct);
            if (change == "header") await context.Vacancies.ExecuteUpdateAsync(update => update.SetProperty(row => row.CompanyName, "Different employer"), Ct);
        }
        var chat = _host!.Services.GetRequiredService<CareerChat>();
        Assert.Contains("устарела", (await chat.AskTimeAsync(source.Id, Ct, change == "legacy" ? 0 : version)).Text);
        Assert.Contains("устарела", (await chat.AskDebriefAsync(source.Id, Ct, change == "legacy" ? 0 : version)).Text);
        Assert.False(await _host.Services.GetRequiredService<ConversationStore>().HasStateAsync(Ct));
        await using var final = await pg.Db.CreateDbContextAsync(Ct);
        Assert.Empty(await final.Notifications.ToListAsync(Ct));
    }

    [Fact]
    public async Task Closed_interview_refuses_time_but_preserves_late_debrief_with_a_current_witness()
    {
        var source = await InterviewAsync();
        await SourcesAsync();
        await using (var context = await pg.Db.CreateDbContextAsync(Ct))
            await context.Interviews.ExecuteUpdateAsync(update => update.SetProperty(row => row.Status, InterviewStatus.Done), Ct);
        source.Status = InterviewStatus.Done;
        var chat = _host!.Services.GetRequiredService<CareerChat>();
        Assert.Contains("закрыто", (await chat.AskTimeAsync(source.Id, Ct, CareerPromptSources.ControlVersion(source))).Text);
        Assert.False(await _host.Services.GetRequiredService<ConversationStore>().HasStateAsync(Ct));
        Assert.Empty((await chat.AskDebriefAsync(source.Id, Ct, CareerPromptSources.ControlVersion(source))).Text);
        Assert.Equal(CareerChat.DebriefMode, (await _host.Services.GetRequiredService<ConversationStore>().GetAsync(Ct))!.Mode);
    }

    [Theory]
    [InlineData("{broken")]
    [InlineData("null")]
    [InlineData("{\"mode\":\"future-dialogue\",\"entityId\":1,\"answers\":[]}")]
    public async Task Unknown_or_damaged_dialogue_is_never_cleared_or_replaced_by_commands(string raw)
    {
        await SourcesAsync();
        await using (var context = await pg.Db.CreateDbContextAsync(Ct))
        {
            context.Settings.Add(new() { Key = ConversationStore.Key, Value = raw, UpdatedAt = DateTimeOffset.UtcNow });
            await context.SaveChangesAsync(Ct);
        }
        var chat = _host!.Services.GetRequiredService<CareerChat>();
        Assert.NotNull(await chat.HandleTextAsync("Keep original evidence", Ct));
        await chat.StopAsync(Ct);
        Assert.Contains("активный диалог", (await chat.AskOfferAsync(Ct)).Text);
        await using var final = await pg.Db.CreateDbContextAsync(Ct);
        Assert.Equal(raw, (await final.Settings.SingleAsync(row => row.Key == ConversationStore.Key, Ct)).Value);
        Assert.Empty(await final.Notifications.ToListAsync(Ct));
        var settings = _host.Services.GetRequiredService<RuntimeSettings>();
        await Assert.ThrowsAsync<InvalidOperationException>(() => settings.SetAsync(ConversationStore.Key, "replacement", Ct));
        await Assert.ThrowsAsync<InvalidOperationException>(() => settings.RemoveAsync(ConversationStore.Key, Ct));
    }

    [Fact]
    public async Task Stale_prompt_answer_and_stop_cannot_clear_a_later_identical_prompt()
    {
        var source = await InterviewAsync();
        await SourcesAsync();
        var chat = _host!.Services.GetRequiredService<CareerChat>();
        await chat.AskTimeAsync(source.Id, Ct);
        var store = _host.Services.GetRequiredService<ConversationStore>();
        var captured = (await store.GetAsync(Ct))!;
        var newer = captured with { Id = Guid.NewGuid() };
        await store.SetAsync(newer, Ct);
        var owner = _host.Services.GetRequiredService<CareerPromptSources>();
        Assert.Null(await owner.SaveAnswerAsync(captured, "завтра 15:00", Ct));
        Assert.Contains("изменился", (await owner.StopAsync(captured, Ct)).Text);
        Assert.Equal(newer.Id, (await store.GetAsync(Ct))!.Id);
        Assert.Empty((await owner.StopAsync(newer, Ct)).Text);
        Assert.False(await store.HasStateAsync(Ct));
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        var stop = Assert.Single(await context.Notifications.Where(row => row.Text == "Остановлено.").ToListAsync(Ct));
        await NotificationAdmissionAssertions.JoinedAsync(context, stop, Ct);
    }

    [Fact]
    public async Task Time_source_and_dialogue_consumption_roll_back_together_without_losing_the_answer()
    {
        var source = await InterviewAsync();
        var fault = new Fault { Enabled = false };
        var sources = await SourcesAsync(fault);
        await _host!.Services.GetRequiredService<CareerChat>().AskTimeAsync(source.Id, Ct);
        var store = _host.Services.GetRequiredService<ConversationStore>();
        var prompt = (await store.GetAsync(Ct))!;
        var saved = (await _host.Services.GetRequiredService<CareerPromptSources>().SaveAnswerAsync(prompt, "завтра 15:00", Ct))!;
        fault.Enabled = true;
        var next = source.ScheduledAt!.Value.AddDays(1);
        await Assert.ThrowsAsync<InvalidOperationException>(() => sources.SetTimeAsync(saved.Interview!, next, Ct, saved));
        Assert.Equal(ConversationStore.Capture(saved), ConversationStore.Capture((await store.GetAsync(Ct))!));
        Assert.True(saved.Interview!.Matches(await ReadAsync(source.Id)));
        Assert.Equal(0, await CommandsAsync());
        fault.Enabled = false;
        Assert.True((await sources.SetTimeAsync(saved.Interview, next, Ct, saved)).Ok);
        Assert.False(await store.HasStateAsync(Ct));
        Assert.Equal(1, await CommandsAsync());
    }

    [Fact]
    public async Task Confirming_unchanged_time_consumes_the_prompt_without_rebuilding_preparation()
    {
        var source = await InterviewAsync(pack: true);
        var sources = await SourcesAsync();
        var before = await ReadAsync(source.Id);
        await _host!.Services.GetRequiredService<CareerChat>().AskTimeAsync(source.Id, Ct);
        var store = _host.Services.GetRequiredService<ConversationStore>();
        var saved = (await _host.Services.GetRequiredService<CareerPromptSources>().SaveAnswerAsync((await store.GetAsync(Ct))!, "Same time", Ct))!;
        Assert.True((await sources.SetTimeAsync(saved.Interview!, source.ScheduledAt, Ct, saved)).Ok);
        Assert.False(await store.HasStateAsync(Ct));
        Assert.True(saved.Interview!.Matches(await ReadAsync(source.Id)));
        Assert.Equal(before.PackJson, (await ReadAsync(source.Id)).PackJson);
        Assert.Equal(0, await CommandsAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Changed_prompt_refuses_time_or_paid_debrief_publication_without_overwriting_the_source(bool debrief)
    {
        var source = await InterviewAsync();
        var sources = await SourcesAsync();
        var chat = _host!.Services.GetRequiredService<CareerChat>();
        if (debrief) await chat.AskDebriefAsync(source.Id, Ct);
        else await chat.AskTimeAsync(source.Id, Ct);
        var store = _host.Services.GetRequiredService<ConversationStore>();
        var saved = (await _host.Services.GetRequiredService<CareerPromptSources>().SaveAnswerAsync((await store.GetAsync(Ct))!, "Original input", Ct))!;
        var newer = saved with { Id = Guid.NewGuid(), Draft = null, Step = 0 };
        await store.SetAsync(newer, Ct);
        if (debrief)
        {
            await Assert.ThrowsAsync<InterviewEditConflictException>(() => _host.Services.GetRequiredService<InterviewTransitions>()
                .CompleteDebriefAsync(saved.Interview!, new DebriefResult { Summary = "Paid result", Stage = "final" }, Ct, saved.InterviewHeaderHash, saved));
            await Assert.ThrowsAsync<InterviewEditConflictException>(() => _host.Services.GetRequiredService<InterviewPrep>()
                .DebriefAsync(saved.Interview!, saved.InterviewHeaderHash!, saved.Draft!, Ct, saved));
        }
        else await Assert.ThrowsAsync<InterviewEditConflictException>(() => sources.SetTimeAsync(saved.Interview!, source.ScheduledAt!.Value.AddDays(1), Ct, saved));
        Assert.True(saved.Interview!.Matches(await ReadAsync(source.Id)));
        Assert.Equal(newer.Id, (await store.GetAsync(Ct))!.Id);
        Assert.Equal(0, await CommandsAsync());
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        Assert.Empty(await context.ApplicationEvents.ToListAsync(Ct));
        Assert.Empty(await context.LlmCalls.ToListAsync(Ct));
    }

    [Fact]
    public async Task Debrief_completion_notice_and_consumption_roll_back_together_and_reuse_the_paid_result()
    {
        var source = await InterviewAsync();
        var fault = new PromptNoticeFault();
        await SourcesAsync(fault);
        await _host!.Services.GetRequiredService<CareerChat>().AskDebriefAsync(source.Id, Ct);
        var store = _host.Services.GetRequiredService<ConversationStore>();
        var saved = (await _host.Services.GetRequiredService<CareerPromptSources>().SaveAnswerAsync((await store.GetAsync(Ct))!, "Raw transcript", Ct))!;
        var result = new DebriefResult { Summary = "Already paid summary", Stage = "none" };
        var transitions = _host.Services.GetRequiredService<InterviewTransitions>();
        fault.Enabled = true;
        await Assert.ThrowsAsync<IOException>(() => transitions.CompleteDebriefAsync(saved.Interview!, result, Ct, saved.InterviewHeaderHash, saved));
        Assert.True(saved.Interview!.Matches(await ReadAsync(source.Id)));
        Assert.Equal(ConversationStore.Capture(saved), ConversationStore.Capture((await store.GetAsync(Ct))!));
        fault.Enabled = false;
        Assert.Equal(source.ApplicationId, await transitions.CompleteDebriefAsync(saved.Interview, result, Ct, saved.InterviewHeaderHash, saved));
        Assert.False(await store.HasStateAsync(Ct));
        Assert.Equal(InterviewStatus.Done, (await ReadAsync(source.Id)).Status);
        await using var context = await pg.Db.CreateDbContextAsync(Ct);
        Assert.Single(await context.ApplicationEvents.ToListAsync(Ct));
        var notice = Assert.Single(await context.Notifications.Where(row => row.Text.Contains("сохранён")).ToListAsync(Ct));
        await NotificationAdmissionAssertions.JoinedAsync(context, notice, Ct);
        Assert.Empty(await context.LlmCalls.ToListAsync(Ct));
    }

    [Theory]
    [InlineData(CareerCallback.InterviewTime, CareerChat.TimeMode)]
    [InlineData(CareerCallback.InterviewDebrief, CareerChat.DebriefMode)]
    public async Task Actual_career_bot_versions_the_prompt_and_does_not_publish_an_empty_duplicate(string action, string mode)
    {
        var source = await InterviewAsync();
        await SourcesAsync();
        var options = new BotOptions { OwnerUserId = 123456 };
        var messages = ActivatorUtilities.CreateInstance<OwnerBotMessages>(_host!.Services, options);
        var service = ActivatorUtilities.CreateInstance<CareerBot>(_host.Services, options, messages);
        using var api = new PromptCallbackApi();
        using var http = new HttpClient(api);
        var bot = new TelegramBotClient(new TelegramBotClientOptions("123456:synthetic-test-token"), http);
        var callback = new CallbackQuery { Id = "synthetic-interview", From = new User { Id = 123456, FirstName = "Synthetic" } };
        Assert.True(await service.TryCallbackAsync(bot, callback, action, source.Id, 0, Ct));
        Assert.False(await _host.Services.GetRequiredService<ConversationStore>().HasStateAsync(Ct));
        await using (var context = await pg.Db.CreateDbContextAsync(Ct))
        {
            Assert.Contains("устарела", (await context.Notifications.SingleAsync(Ct)).Text);
            await NotificationAdmissionAssertions.ResetAsync(context, Ct);
        }
        Assert.True(await service.TryCallbackAsync(bot, callback, action, source.Id, CareerPromptSources.ControlVersion(source), Ct));
        Assert.Equal(mode, (await _host.Services.GetRequiredService<ConversationStore>().GetAsync(Ct))!.Mode);
        Assert.Equal(2, api.Acknowledgements);
        await using var final = await pg.Db.CreateDbContextAsync(Ct);
        var notice = Assert.Single(await final.Notifications.ToListAsync(Ct));
        Assert.NotEmpty(notice.Text);
        await NotificationAdmissionAssertions.JoinedAsync(final, notice, Ct);
    }

    private sealed class PromptCallbackApi : HttpMessageHandler
    {
        public int Acknowledgements { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Assert.Equal("api.telegram.org", request.RequestUri!.Host);
            Assert.EndsWith("/answerCallbackQuery", request.RequestUri.AbsolutePath, StringComparison.OrdinalIgnoreCase);
            Acknowledgements++;
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent("{\"ok\":true,\"result\":true}", System.Text.Encoding.UTF8, "application/json"),
            });
        }
    }

    private sealed class PromptNoticeFault : SaveChangesInterceptor
    {
        public bool Enabled { get; set; }
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData data, InterceptionResult<int> result, CancellationToken ct = default)
        {
            if (Enabled && data.Context!.ChangeTracker.Entries<Notification>().Any(entry => entry.State == EntityState.Added))
                throw new IOException("synthetic career prompt notice fault");
            return ValueTask.FromResult(result);
        }
    }
}
