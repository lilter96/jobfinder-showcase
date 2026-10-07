using JobFinder.Core.Domain;
using JobFinder.Core.Filtering;
using JobFinder.Core.Profile;
using JobFinder.Llm.Tasks;

namespace JobFinder.Pipeline.Career;

public sealed record AutoReplyDecision(bool Send, string Reason);

/// <summary>
/// Whether a reply to an employer goes out on its own or waits for the user. The model proposes (intent, routine, facts used and
/// missing); this code decides, and every "no" carries the reason shown on the card. Routine only: confirming interest, known facts,
/// proposing call slots, small talk — with every fact confirmed, the style clean, a fresh message, daytime, and never more than two
/// automatic replies in a row without the user.
/// </summary>
public static class DialoguePolicy
{
    public const int MaxAutoInARow = 2;
    public static readonly TimeSpan MaxMessageAge = TimeSpan.FromHours(12);

    private static readonly HashSet<string> RoutineIntents = ["interest_check", "facts", "schedule", "small_talk"];

    public static AutoReplyDecision Decide(ReplyAnalysis a, CandidateFacts facts, bool enabled, EventChannel channel, DateTimeOffset messageAt,
        DateTimeOffset now, DateTimeOffset nowLocal, int autoInARow)
    {
        if (string.IsNullOrWhiteSpace(a.Draft)) return new(false, "ответ не нужен");
        if (!enabled) return new(false, "автоответы выключены");
        if (channel is not (EventChannel.HeadHunter or EventChannel.Telegram or EventChannel.Email)) return new(false, "у этого сообщения нет канала для ответа");
        if (a.Conflicts.Count > 0) return new(false, "расхождение с вашими условиями");
        if (!RoutineIntents.Contains(a.Intent) || !a.Routine) return new(false, $"не рутина: {IntentLabel(a.Intent)}");
        if (a.MissingFacts.Count > 0) return new(false, "не хватает фактов: " + Labels(facts, a.MissingFacts));
        var unconfirmed = a.UsedFacts.Where(k => !facts.IsConfirmed(k)).ToList();
        if (unconfirmed.Count > 0) return new(false, "факты не подтверждены: " + Labels(facts, unconfirmed));
        if (a.Intent == "schedule" && !facts.IsConfirmed("call_slots")) return new(false, "не подтверждены слоты для созвонов");
        var style = ChatStyle.Problems(a.Draft);
        if (style.Count > 0) return new(false, "стиль: " + string.Join(", ", style));
        if (now - messageAt > MaxMessageAge) return new(false, "сообщение старше 12 часов");
        if (nowLocal.Hour is < 9 or >= 21) return new(false, "ночью система сама не пишет");
        if (autoInARow >= MaxAutoInARow) return new(false, "уже два автоответа подряд — дальше решаете вы");
        return new(true, "рутина по подтверждённым фактам");
    }

    /// <summary>A reply that needs the longer style limits: a technical or open question, or a condition to answer first.</summary>
    public static bool IsConsidered(ReplyAnalysis a) => a.Intent is "technical" or "open_question" or "questionnaire" || a.Conflicts.Count > 0;

    public static string IntentLabel(string intent) => intent switch
    {
        "interest_check" => "проверка интереса",
        "facts" => "вопросы по фактам",
        "schedule" => "согласование времени",
        "scheduled" => "назначено время",
        "questionnaire" => "анкета",
        "test_task" => "тестовое",
        "open_question" => "открытый вопрос",
        "technical" => "технический вопрос",
        "offer" => "оффер",
        "rejection" => "отказ",
        "small_talk" => "вежливость",
        _ => "другое",
    };

    private static string Labels(CandidateFacts facts, IEnumerable<string> keys) =>
        string.Join(", ", keys.Select(k => facts.Get(k)?.Question.ToLowerInvariant() ?? k));
}
