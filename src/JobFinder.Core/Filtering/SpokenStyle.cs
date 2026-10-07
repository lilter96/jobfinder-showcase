using System.Text.RegularExpressions;
using JobFinder.Core.Text;

namespace JobFinder.Core.Filtering;

/// <summary>
/// The code-level gate for interview speech: what the candidate says aloud reads as a person talking, not as documentation, notes to
/// self or a stack list, fits its time, and does not sell the experience short. A part that fails is rewritten once with the reasons.
/// </summary>
public static partial class SpokenStyle
{
    /// <summary>A spoken part with its time budget (about 150 words a minute, Russian ≈ 7 characters a word).</summary>
    public sealed record Part(string Label, int MaxChars, int MaxSentences, int MaxTech);

    public static readonly Part Start = new("начало", 340, 3, 3);
    public static readonly Part Core = new("30 сек", 950, 7, 5);
    public static readonly Part Extended = new("60 сек", 1600, 11, 7);
    public static readonly Part FollowUpStart = new("начало уточнения", 340, 3, 3);
    public static readonly Part FollowUpCore = new("уточнение", 800, 6, 5);

    /// <param name="gapAllowed">The topic is outside the experience (confirmed gap): an honest "не работал" is allowed there.</param>
    public static List<string> Problems(string? text, Part part, bool gapAllowed = false)
    {
        var problems = new List<string>();
        var t = (text ?? "").Trim();
        if (t.Length == 0) return [$"{part.Label}: пусто"];
        if (t.Length > part.MaxChars) problems.Add($"{part.Label}: длиннее {part.MaxChars} символов, не укладывается во время");
        if (CommentStyle.Sentences(t) > part.MaxSentences) problems.Add($"{part.Label}: больше {part.MaxSentences} предложений");
        if (ChatStyle.FindPlaceholder(t) is { } hole) problems.Add($"{part.Label}: заглушка «{hole}»");
        if (ChatStyle.FindMeta(t) is { } meta) problems.Add($"{part.Label}: пометка вместо речи «{meta}»");
        if (!gapAllowed && ChatStyle.FindHedge(t) is { } hedge) problems.Add($"{part.Label}: самоуничижение «{hedge}»");
        if (ChatStyle.HasBullets(t)) problems.Add($"{part.Label}: список вместо речи");
        var normalized = TextUtil.Normalize(t);
        if (Note().Match(normalized) is { Success: true } note) problems.Add($"{part.Label}: заметка для себя «{note.Value.Trim(' ', '.', '!', '?')}»");
        if (Definition().IsMatch(normalized)) problems.Add($"{part.Label}: начинается с определения, а не с ответа");
        if (Bureaucratic().Match(normalized) is { Success: true } b) problems.Add($"{part.Label}: канцелярит «{b.Value}»");
        if (Disclaimer().Match(normalized) is { Success: true } d) problems.Add($"{part.Label}: оговорка про свои заявления «{d.Value}»");
        if (ChatStyle.TechTerms(t) > part.MaxTech) problems.Add($"{part.Label}: перечисление технологий");
        if (!HumanText.IsClean(t)) problems.Add($"{part.Label}: длинные тире или «…»");
        return problems;
    }

    /// <summary>An imperative infinitive opening a sentence: "Уточнить модель данных", "Проверить индексы".</summary>
    [GeneratedRegex(@"(^|[.!?]\s+)(уточнить|проверить|рассказать|упомянуть|объяснить|подчеркнуть|привести|отметить|рассмотреть|описать|показать|сделать акцент|акцентировать)\b[^.!?]{0,40}")]
    private static partial Regex Note();

    /// <summary>"Идемпотентность - это …" as the first words.</summary>
    [GeneratedRegex(@"^[\p{L}\p{N}#./+ ]{2,40}\s-\s?это\s")]
    private static partial Regex Definition();

    /// <summary>
    /// What the candidate "will not claim", "confirmed experience", "as in my resume": the preparation's checks spoken aloud. The
    /// interviewer hears what the candidate did, never the bookkeeping behind it.
    /// </summary>
    public static string? FindDisclaimer(string text) => Disclaimer().Match(TextUtil.Normalize(text)) is { Success: true } m ? m.Value : null;

    [GeneratedRegex(@"не (буду|стану|хочу) (выдавать|заявлять|представлять|приравнивать|называть|завышать|преувеличивать)|не заявляю|подтвержд\p{L}* (опыт|кейс|production|продакшн|игровой|результат)|(в|по) (моему |своему )?резюме|в анкете|уточню (на созвоне|точно|позже)|не хочу (завышать|приукрашивать)|лучше обозначить сразу|не прохожу")]
    private static partial Regex Disclaimer();

    [GeneratedRegex(@"(?<!\p{L})(осуществлял\p{L}*|является|являются|в рамках|данн(ый|ого|ая|ой|ую|ом|ому)\s|хочу отметить|важно понимать|следует отметить|было бы корректно|вышеупомянут\p{L}*)")]
    private static partial Regex Bureaucratic();
}
