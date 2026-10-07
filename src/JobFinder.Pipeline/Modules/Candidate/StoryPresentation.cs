using System.Text;
using System.Text.Json;
using JobFinder.Core.Resume;
using JobFinder.Core.Text;
using JobFinder.Llm.Tasks;
using JobFinder.Pipeline.Career;
using JobFinder.Pipeline.Notifications;

namespace JobFinder.Pipeline.Modules.Candidate;

public static class StoryPresentation
{
    public const string Version = "v1";
    public static ConversationState Ready(StoryPreparationInput input, StoryPreparationResult result) => input.Waiting with
    {
        Draft = JsonSerializer.Serialize(result.Draft, CareerJsonContext.Default.StoryDraft), StoryPending = null,
    };

    public static BotReply Draft(StoryPreparationInput input, StoryPreparationResult result)
    {
        var draft = result.Draft;
        var ready = Ready(input, result);
        var source = ResumeFaithfulness.SourceNumbers($"{input.Waiting.Subject}\n{input.AnswersText()}\n{input.Candidate.ResumeText}");
        var suspicious = ResumeFaithfulness.UnverifiedNumbers($"{draft.Situation} {draft.Task} {draft.Action} {draft.Result} {draft.Reflection}", source);
        var text = new StringBuilder("📖 <b>").Append(TextUtil.EscapeHtml(draft.Title)).Append("</b>\n\n")
            .Append("<b>S</b> ").Append(TextUtil.EscapeHtml(draft.Situation)).Append('\n')
            .Append("<b>T</b> ").Append(TextUtil.EscapeHtml(draft.Task)).Append('\n')
            .Append("<b>A</b> ").Append(TextUtil.EscapeHtml(draft.Action)).Append('\n')
            .Append("<b>R</b> ").Append(TextUtil.EscapeHtml(draft.Result)).Append('\n')
            .Append("<b>Вывод</b> ").Append(TextUtil.EscapeHtml(draft.Reflection)).Append('\n');
        if (draft.Skills.Count > 0) text.Append("🏷 ").Append(TextUtil.EscapeHtml(string.Join(", ", draft.Skills))).Append('\n');
        if (suspicious.Count > 0) text.Append("\n⚠️ Цифры ").Append(string.Join(", ", suspicious)).Append(" не из ваших ответов — проверьте перед сохранением.\n");
        return new(CareerCards.Truncate(text.ToString()),
        [
            [Callback.Button("✅ Сохранить", CareerCallback.StorySave, StoryBank.ControlId(ready), StoryBank.Version(ready)), Callback.Button("🔄 Переписать", CareerCallback.StoryRedo, StoryBank.ControlId(ready), StoryBank.Version(ready))],
            [Callback.Button("⏭ Другое достижение", CareerCallback.StorySkip, StoryBank.ControlId(ready), StoryBank.Version(ready)), Callback.Button("⏹ Хватит", CareerCallback.StoryStop, StoryBank.ControlId(ready), StoryBank.Version(ready))],
        ]);
    }

    public static BotReply Ask(ConversationState state, int? pending)
    {
        var text = new StringBuilder();
        if (state.Step == 0)
            text.Append("📚 <b>Банк историй</b>").Append(pending is { } count ? $" · осталось достижений: {count}" : "")
                .Append("\n\nДостижение из резюме:\n<i>").Append(TextUtil.EscapeHtml(state.Subject ?? "")).Append("</i>\n\n");
        var questions = state.StoryCandidate?.Questions ?? StoryBank.Questions.ToList();
        text.Append("<b>Вопрос ").Append(state.Step + 1).Append('/').Append(questions.Count).Append(".</b> ")
            .Append(TextUtil.EscapeHtml(questions[state.Step])).Append("\n\nОтветьте текстом или голосовым. /stop — прервать.");
        return new(text.ToString(), StopButtons(state));
    }

    public static List<List<string>> StopButtons(ConversationState state) =>
        [[Callback.Button("⏭ Другое достижение", CareerCallback.StorySkip, StoryBank.ControlId(state), StoryBank.Version(state)), Callback.Button("⏹ Хватит", CareerCallback.StoryStop, StoryBank.ControlId(state), StoryBank.Version(state))]];
}
