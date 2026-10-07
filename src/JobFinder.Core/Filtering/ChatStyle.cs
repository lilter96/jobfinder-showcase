using System.Text.RegularExpressions;
using JobFinder.Core.Text;

namespace JobFinder.Core.Filtering;

/// <summary>
/// The code-level gate for replies to employers: a person in a messenger writes a few short sentences, answers what was asked and does
/// not recite a stack or a template. A reply that fails is never sent on its own; it goes to the user as a draft.
/// </summary>
public static partial class ChatStyle
{
    public const int MaxLength = 500;
    public const int MaxSentences = 5;

    /// <summary>A technical or open question, or a condition to answer first, takes a few more sentences.</summary>
    public const int MaxConsideredLength = 750;
    public const int MaxConsideredSentences = 7;

    /// <summary>More technologies than this in one message reads as a reference card, not an answer.</summary>
    public const int MaxTechTerms = 4;

    /// <param name="considered">A considered answer (technical, open question, a conflict of conditions): the longer limits apply.</param>
    public static List<string> Problems(string text, bool considered = false)
    {
        var problems = new List<string>();
        var t = text.Trim();
        if (t.Length == 0) return ["пустой ответ"];
        var (maxLength, maxSentences) = considered ? (MaxConsideredLength, MaxConsideredSentences) : (MaxLength, MaxSentences);
        if (t.Length > maxLength) problems.Add($"длиннее {maxLength} символов");
        if (CommentStyle.Sentences(t) > maxSentences) problems.Add($"больше {maxSentences} предложений");
        if (Placeholder().Match(t) is { Success: true } hole) problems.Add($"заглушка вместо ответа: «{hole.Value}»");
        if (Meta().Match(TextUtil.Normalize(t)) is { Success: true } meta) problems.Add($"пометка вместо ответа: «{meta.Value}»");
        if (Hedge().Match(TextUtil.Normalize(t)) is { Success: true } hedge) problems.Add($"оговорка вместо ответа: «{hedge.Value}»");
        if (!HumanText.IsClean(t)) problems.Add("длинные тире или «…»");
        if (Cliche().Match(TextUtil.Normalize(t)) is { Success: true } cliche) problems.Add($"шаблонная фраза: «{cliche.Value}»");
        if (BulletLine().IsMatch(t)) problems.Add("список вместо живого ответа");
        var tech = TechTerm().Matches(t).Select(m => m.Value.ToLowerInvariant().Replace(" ", "", StringComparison.Ordinal)).Distinct().Count();
        if (tech > MaxTechTerms) problems.Add($"перечисление стека ({tech} технологий)");
        if (Signature().IsMatch(t)) problems.Add("подпись в чате");
        return problems;
    }

    [GeneratedRegex(@"буду рад ответить|благодарю за (ваше )?(сообщение|обращение)|готов обсудить детали|отличная возможность|с удовольствием (рассмотрю|отвечу|обсужу)|это показывает|важно отметить|следует отметить|не стесняйтесь|надеюсь на (дальнейшее|плодотворное)|в свою очередь|хотел бы подчеркнуть")]
    private static partial Regex Cliche();

    /// <summary>A gap left for the user ("[срок]", "(указать)"), or null. Shared with the interview speech check.</summary>
    public static string? FindPlaceholder(string text) => Placeholder().Match(text) is { Success: true } m ? m.Value : null;

    /// <summary>Talk about the candidate instead of the candidate talking ("в резюме не указано", "кандидат"), or null.</summary>
    public static string? FindMeta(string text) => Meta().Match(TextUtil.Normalize(text)) is { Success: true } m ? m.Value : null;

    /// <summary>Selling the experience short ("глубоко не погружался", "на базовом уровне"), or null.</summary>
    public static string? FindHedge(string text) => Hedge().Match(TextUtil.Normalize(text)) is { Success: true } m ? m.Value : null;

    public static bool HasBullets(string text) => BulletLine().IsMatch(text);

    /// <summary>A template phrase of a business letter, or null.</summary>
    public static string? FindCliche(string text) => Cliche().Match(TextUtil.Normalize(text)) is { Success: true } m ? m.Value : null;

    public static bool HasSignature(string text) => Signature().IsMatch(text);

    /// <summary>Distinct technology names in the text.</summary>
    public static int TechTerms(string text) =>
        TechTerm().Matches(text).Select(m => m.Value.ToLowerInvariant().Replace(" ", "", StringComparison.Ordinal)).Distinct().Count();

    /// <summary>A gap left for the user: "[срок выхода]", "{{name}}", "XXX", "TODO".</summary>
    [GeneratedRegex(@"\[[^\]]*\]?|\{\{|\bXXX\b|\bTODO\b|\(\s*(указать|уточнить|вставить|дописать)[^)]*\)", RegexOptions.IgnoreCase)]
    private static partial Regex Placeholder();

    /// <summary>Talk about the candidate instead of the candidate talking (normalized text: lower case, ё → е).</summary>
    [GeneratedRegex(@"(в|по|из) (моем |своем )?резюме (не |нет|этого нет|не было)|в резюме (не )?(указан|упомина|отраж)\w*|(?<!\p{L})кандидат\p{L}*|(?<!\p{L})черновик\p{L}*|уточнить (релевантн|практическ|конкретн)\p{L}* пример")]
    private static partial Regex Meta();

    /// <summary>
    /// Selling the experience short where the stack says otherwise: "глубоко не погружался", "на базовом уровне", "в исходники не
    /// углублялся". An honest "с Go не работал" about another stack is not a hedge and passes.
    /// </summary>
    [GeneratedRegex(@"не (углубля|погружа|вника)л(ся|ась)|глубоко не|не (очень|слишком|особо) глубоко|на (базовом|поверхностном) уровне|опыта (мало|немного|небольшой)|не доходил(о|и|а)?(?!\p{L})|на уровне исходник\p{L}* не")]
    private static partial Regex Hedge();

    [GeneratedRegex(@"^\s*([-*•]|\d+[.)])\s+\S", RegexOptions.Multiline)]
    private static partial Regex BulletLine();

    [GeneratedRegex(@"с уважением|best regards|kind regards", RegexOptions.IgnoreCase)]
    private static partial Regex Signature();

    [GeneratedRegex(@"(?<![\w.#])(c#|\.net|asp\.net( core)?|postgres(ql)?|ms ?sql|sql server|redis|rabbitmq|kafka|docker|kubernetes|k8s|rest( api)?|grpc|ef core|entity framework|linq|azure|aws|mongodb|elasticsearch|grafana|prometheus|opentelemetry|signalr|microservices|микросервис\w*|clickhouse|nats|hangfire|masstransit|react|typescript|angular|vue(\.js)?|blazor|cqrs|kafka streams|nosql)(?![\w#])", RegexOptions.IgnoreCase)]
    private static partial Regex TechTerm();
}
