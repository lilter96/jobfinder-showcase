using System.Text;
using JobFinder.Core.Domain;
using JobFinder.Core.Text;
using JobFinder.Core.Time;
using JobFinder.Pipeline.Notifications;
using JobFinder.Pipeline.Modules.Preparation;

namespace JobFinder.Pipeline.Career;

/// <summary>
/// The bot's dialogue router: free text (or a transcribed voice message) goes to the active conversation — story interview, practice
/// interview, debrief, offer, interview time. Kept here, not in the bot, so the dialogues are testable without Telegram.
/// </summary>
public sealed class CareerChat(ConversationStore conversations, StoryBank stories, MockInterviews mocks, InterviewPrep prep, OfferAdvisor offers,
    LocalClock clock, CareerPromptSources prompts)
{
    public const string DebriefMode = "debrief";
    public const string TimeMode = "itime";

    /// <returns>null when no dialogue is active (the bot then shows its help).</returns>
    public async Task<BotReply?> HandleTextAsync(string text, CancellationToken ct)
    {
        var state = await conversations.GetAsync(ct);
        if (state is null) return await conversations.HasStateAsync(ct)
            ? new BotReply("Сохранённый диалог повреждён: состояние не изменено, требуется проверка.") : null;
        switch (state.Mode)
        {
            case StoryBank.Mode:
                if (state.StoryPending == true) return new BotReply("Подготовка истории уже сохранена; дождитесь черновика или /stop. Ответы не перезаписаны.");
                return state.Draft is null ? await stories.AnswerAsync(state, text, ct) : new BotReply("Сохраните, перепишите или пропустите историю кнопками выше.");
            case MockInterviews.Mode:
                return await mocks.AnswerAsync(state, text, ct);
            case OfferAdvisor.Mode:
                return state.OfferRequestId is not null ? new BotReply("Разбор уже сохранён; дождитесь результата или /stop. Новый текст не перезаписал исходный.")
                    : await offers.AnalyzeAsync(text, ct, state);
            case DebriefMode:
                if (state.Interview is null || state.InterviewHeaderHash is null) return new BotReply("Старый запрос дебрифа: откройте новый.");
                var debrief = await prompts.SaveAnswerAsync(state, text, ct);
                if (debrief is null) return new BotReply("Диалог изменился: ответьте на новый запрос.");
                try
                {
                    return await DebriefAsync(state.Interview, state.InterviewHeaderHash, text, debrief, ct);
                }
                catch (InterviewEditConflictException error)
                {
                    return new BotReply("⚠️ " + TextUtil.EscapeHtml(error.Message) + "\nТекст сохранён, старый snapshot не обновлён.");
                }
            case TimeMode:
                if (!LocalDateParser.TryParse(text, clock.Now, clock.Zone, out var at))
                    return new BotReply("Не понял время. Примеры: «завтра 15:00», «пт 11:30», «28.09 14:00».");
                if (state.Interview is null) return new BotReply("Старый запрос времени: откройте новый.");
                var appointment = await prompts.SaveAnswerAsync(state, text, ct);
                if (appointment is null) return new BotReply("Диалог изменился: ответьте на новый запрос.");
                try
                {
                    var result = await prep.SetTimeAsync(state.Interview, at, ct, appointment);
                    return new BotReply((result.Ok ? "🗓 " : "⚠️ ") + TextUtil.EscapeHtml(result.Message));
                }
                catch (InterviewEditConflictException error)
                {
                    return new BotReply("⚠️ " + TextUtil.EscapeHtml(error.Message));
                }
            default:
                return new BotReply("Неизвестный диалог не изменён: требуется проверка сохранённого состояния.");
        }
    }

    public async Task<BotReply> StopAsync(CancellationToken ct)
    {
        var state = await conversations.GetAsync(ct);
        if (state?.Mode == MockInterviews.Mode) return await mocks.FinishAsync(state, ct);
        if (state?.Mode == OfferAdvisor.Mode) return await offers.StopAsync(state, ct);
        if (state?.Mode == StoryBank.Mode) return await stories.StopAsync(StoryBank.ControlId(state), StoryBank.Version(state), ct);
        return state is null ? new BotReply(await conversations.HasStateAsync(ct)
            ? "Сохранённый диалог повреждён: состояние не изменено, требуется проверка." : "Нет активного диалога.") : await prompts.StopAsync(state, ct);
    }

    public Task<BotReply> AskTimeAsync(long interviewId, CancellationToken ct, int? version = null) =>
        prompts.BeginInterviewAsync(interviewId, TimeMode, ct, version);

    public Task<BotReply> AskDebriefAsync(long interviewId, CancellationToken ct, int? version = null) =>
        prompts.BeginInterviewAsync(interviewId, DebriefMode, ct, version);

    public Task<BotReply> AskOfferAsync(CancellationToken ct) => prompts.BeginOfferAsync(ct);

    private async Task<BotReply> DebriefAsync(InterviewEditSnapshot snapshot, string headerHash, string text, ConversationState prompt, CancellationToken ct)
    {
        var (result, applicationId) = await prep.DebriefAsync(snapshot, headerHash, text, ct, prompt);
        var sb = new StringBuilder("📝 <b>Дебриф сохранён</b>\n").Append(TextUtil.EscapeHtml(result.Summary)).Append('\n');
        if (result.WentWell.Count > 0) sb.Append("\n✅ ").Append(TextUtil.EscapeHtml(string.Join("; ", result.WentWell))).Append('\n');
        if (result.ToImprove.Count > 0) sb.Append("🔧 ").Append(TextUtil.EscapeHtml(string.Join("; ", result.ToImprove))).Append('\n');
        if (!string.IsNullOrWhiteSpace(result.NextSteps)) sb.Append("\n➡️ ").Append(TextUtil.EscapeHtml(result.NextSteps)).Append('\n');
        if (!string.IsNullOrWhiteSpace(result.ThankYouNote))
            sb.Append("\n<b>Благодарность (скопируйте):</b>\n<code>").Append(TextUtil.EscapeHtml(HumanText.Clean(result.ThankYouNote))).Append("</code>");
        return new BotReply(CareerCards.Truncate(sb.ToString()), [CareerCards.StageRow(applicationId)]);
    }
}
