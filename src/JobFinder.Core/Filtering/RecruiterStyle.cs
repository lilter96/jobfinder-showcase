using System.Text.RegularExpressions;
using JobFinder.Core.Text;

namespace JobFinder.Core.Filtering;

/// <summary>
/// The code-level gate for a message to a recruiter: a developer writing to a person, not a cover letter and not a resume in a message.
/// It catches what a first contact dies of: a stack recital, a row of KPIs, "also worked with…", template openings and closings, a
/// separate sentence about the attachment.
/// </summary>
public static partial class RecruiterStyle
{
    public const int MinFirstLength = 250;

    /// <summary>What the model writes of a first message (greeting, body, closing); the vacancy line the code adds comes on top.</summary>
    public const int MaxFirstLength = 850;

    public const int MaxFollowUpLength = 500;

    /// <summary>The whole first message with the vacancy line: it fits a Telegram caption, so the text and the resume arrive as one message.</summary>
    public const int MaxComposedLength = 1000;

    /// <summary>A paragraph read on a phone screen without scrolling back: about four lines.</summary>
    public const int MaxParagraphLength = 420;

    public const int MaxParagraphs = 3;

    public const int MaxClosingLength = 180;

    /// <summary>Technologies only where the vacancy asks for them: more reads as keywords for an ATS.</summary>
    public const int MaxTechTerms = 3;

    /// <summary>One strong number is a fact, three in a row are a presentation.</summary>
    public const int MaxMetrics = 2;

    /// <param name="first">The first message to this person (a response, a nudge); otherwise a turn in a conversation.</param>
    /// <param name="maxMetrics">A response letter takes one number at most: a cover letter is not a compressed resume.</param>
    public static List<string> Problems(string text, bool first, int maxMetrics = MaxMetrics, int minFirstLength = MinFirstLength)
    {
        var t = text.Trim();
        if (t.Length == 0) return ["пустое сообщение"];
        var problems = new List<string>();
        if (first && t.Length < minFirstLength) problems.Add($"короче {minFirstLength} символов: нет ни зацепки, ни факта");
        var max = first ? MaxFirstLength : MaxFollowUpLength;
        if (t.Length > max) problems.Add($"длиннее {max} символов");
        if (ChatStyle.FindPlaceholder(t) is { } hole) problems.Add($"заглушка: «{hole}»");
        if (ChatStyle.FindMeta(t) is { } meta) problems.Add($"пометка вместо текста: «{meta}»");
        if (ChatStyle.FindHedge(t) is { } hedge) problems.Add($"оговорка, принижающая опыт: «{hedge}»");
        if (ChatStyle.FindCliche(t) is { } cliche) problems.Add($"шаблонная фраза: «{cliche}»");
        if (Template().Match(TextUtil.Normalize(t)) is { Success: true } template) problems.Add($"шаблон отклика: «{template.Value}»");
        if (Fitting().Match(TextUtil.Normalize(t)) is { Success: true } fit) problems.Add($"абстрактная подгонка под вакансию без опоры: «{fit.Value}»");
        if (Checkup().Match(TextUtil.Normalize(t)) is { Success: true } check) problems.Add($"напоминание как проверка: «{check.Value}»");
        if (Condition().Match(TextUtil.Normalize(t)) is { Success: true } condition)
            problems.Add($"условие вместо утверждения: «{condition.Value}» (скажи, что подходит: формат, варианты оформления)");
        if (StackTail().Match(TextUtil.Normalize(t)) is { Success: true } tail) problems.Add($"хвост со стеком: «{tail.Value}…»");
        if (ChatStyle.HasBullets(t)) problems.Add("список вместо сообщения");
        if (ChatStyle.HasSignature(t)) problems.Add("подпись");
        if (!HumanText.IsClean(t)) problems.Add("длинные тире или «…»");
        var tech = ChatStyle.TechTerms(t);
        if (tech > MaxTechTerms) problems.Add($"слишком много технологий ({tech}): оставь 2-3 из требований вакансии");
        var metrics = Metric().Matches(t).Count;
        if (metrics > maxMetrics) problems.Add($"слишком много цифр ({metrics}): оставь {(maxMetrics == 1 ? "одну, если она правда нужна" : "одну-две самые сильные")}");
        if (AiPhrase().Match(TextUtil.Normalize(t)) is { Success: true } ai) problems.Add($"типичная AI-фраза: «{ai.Value}»");
        return problems;
    }

    /// <summary>
    /// The shape of what the model wrote: a greeting line, one to three short paragraphs, a closing of its own. A first message that is one
    /// wall of text is not read; the vacancy is named by the code's line, so the body does not name it again.
    /// </summary>
    public static List<string> LayoutProblems(string greeting, IReadOnlyList<string> paragraphs, string closing, bool first)
    {
        var problems = new List<string>();
        var body = paragraphs.Where(p => !string.IsNullOrWhiteSpace(p)).ToList();
        if (body.Count == 0) problems.Add("нет текста");
        if (body.Count > MaxParagraphs) problems.Add($"абзацев {body.Count}: не больше {MaxParagraphs}, каждый о своём");
        foreach (var p in body.Where(p => p.Length > MaxParagraphLength))
            problems.Add($"абзац длиннее {MaxParagraphLength} символов (читают с телефона): «{TextUtil.Truncate(p, 60)}»");
        if (first)
        {
            if (!Greeting().IsMatch(greeting.Trim())) problems.Add("приветствие: «Здравствуйте!» или «Здравствуйте, <имя>!», отдельно от текста");
            if (string.IsNullOrWhiteSpace(closing)) problems.Add("нет завершения: одна фраза о следующем шаге");
            if (body.Count == 1 && body[0].Length > 300) problems.Add("один сплошной абзац: зацепка и пример из опыта — разными абзацами");
        }

        if (closing.Length > MaxClosingLength) problems.Add($"завершение длиннее {MaxClosingLength} символов: одна короткая фраза");
        var said = TextUtil.Normalize(string.Join(" ", body.Append(closing)));
        if (Reference().Match(said) is { Success: true } again) problems.Add($"вакансию уже называет первая строка, в тексте не повторяй: «{again.Value}»");
        if (Closing().Match(TextUtil.Normalize(closing)) is { Success: true } worn) problems.Add($"заезженное завершение: «{worn.Value}»");
        return problems;
    }

    [GeneratedRegex(@"^(Здравствуйте|Добрый день)(, [\p{L}][\p{L} .-]{0,40})?!$")]
    private static partial Regex Greeting();

    /// <summary>The body naming the vacancy the code's first line already names.</summary>
    [GeneratedRegex(@"пишу (вам )?(по|насчет|про) (вашей |эт\p{L}+ )?ваканси\p{L}*|откликаюсь на (вашу |эту )?ваканси\p{L}*|по поводу ваканси\p{L}*|увидел\p{L}* (вашу )?ваканси\p{L}*")]
    private static partial Regex Reference();

    /// <summary>The closings every generated letter ends with: a recruiter sees them fifty times a day.</summary>
    [GeneratedRegex(@"(буду рад|готов) обсудить задачи|рассказать подробнее о (проект|опыт)\p{L}*|расскажу подробнее о (проект|опыт)\p{L}*|буду рад обратной связи|жду (вашего )?ответа|надеюсь на (ваш )?(ответ|обратную связь)")]
    private static partial Regex Closing();

    /// <summary>Openings and closings of a template response (normalized text: lower case, ё → е).</summary>
    [GeneratedRegex(@"нашел (вашу )?вакансию|хочу,? чтобы меня рассмотрели|прошу рассмотреть мою кандидатуру|предложите\p{L}*|резюме в pdf|pdf с резюме|резюме (в формате pdf )?(приложил|прикладываю|прилагаю|прикрепил\p{L}*|прикрепляю)|(приложил|прикрепил)\p{L}* резюме|буду рад,? если посмотрите|рассмотреть мой отклик|мой опыт идеально")]
    private static partial Regex Template();

    /// <summary>Claiming a fit in general words instead of a case: "мне близко…", "тематика знакома", "соответствую требованиям".</summary>
    [GeneratedRegex(@"мне (очень )?близк\p{L}*|тематик\p{L}* [\p{L} ]{0,20}знаком\p{L}*|знаком\p{L}* (мне )?тематик\p{L}*|интересен ваш стек|ваш стек мне|соответству\p{L}* (всем |вашим )?требовани\p{L}*|подхожу (под|по) (ваш|всем)\p{L}*|(закрываю|покрываю) (все )?требовани\p{L}*|по всем требованиям")]
    private static partial Regex Fitting();

    /// <summary>
    /// The phrases that make a letter read as generated: "мне интересна роль", "мой опыт хорошо совпадает", "готов созвониться и обсудить,
    /// как мой опыт применим к вашим задачам", "буду рад стать частью команды".
    /// </summary>
    [GeneratedRegex(@"мне интересн\p{L}* (ваша )?(роль|позици\p{L}*|вакансия)|(хорошо|полностью|отлично|идеально) (совпада\p{L}*|подход\p{L}*|соответству\p{L}*)|совпада\p{L}* с (вашими )?(задачами|требованиями)|готов созвониться|как мой опыт (может быть )?(применим|полезен|пригодится)|мой опыт (может быть |будет )?полезен|буду рад стать частью|с интересом (ознакомился|изучил)|ценн\p{L}* вклад|уверен,? что (смогу|мой опыт)|внести (свой )?вклад|(чем|как) (могу|смогу|буду) (быть )?полез\p{L}*")]
    private static partial Regex AiPhrase();

    /// <summary>The candidate's own condition as a doubt: "могу при подходящем варианте оформления", "если это возможно".</summary>
    [GeneratedRegex(@"при (подходящ|соответствующ|определенн|устраивающ)\p{L}* (вариант|услови|формат|оформлени)\p{L}*|если (это )?(возможно|получится|подойдет)|в случае (если|подходящ)\p{L}*")]
    private static partial Regex Condition();

    /// <summary>A reminder that audits the recruiter: "удалось посмотреть?", "вы видели моё сообщение?".</summary>
    [GeneratedRegex(@"удалось (ли )?посмотреть|вы (уже )?видели|получили ли вы|прочитали ли")]
    private static partial Regex Checkup();

    /// <summary>
    /// "Также работал с RabbitMQ, Redis, CQRS…": the rest of the stack appended as a list. One supporting item of the same case ("а также
    /// работал с frontend на React") is not a tail.
    /// </summary>
    [GeneratedRegex(@"(также|еще|кроме того),? (работал\p{L}*|использовал\p{L}*|знаком\p{L}*|имею опыт|есть опыт)( с| со)? [^.,;]{1,40}(, [^.,;]{1,40}){1,}")]
    private static partial Regex StackTail();

    /// <summary>A number with a unit or a percent: "99,99%", "50 мс", "50 тыс.", "5 000 RPS", "в 6 раз".</summary>
    [GeneratedRegex(@"\d[\d\s.,]*\s?(%|мс\b|ms\b|сек(унд\p{L}*)?\b|тыс\p{L}*\.?|млн\p{L}*|k\b|rps\b|раза?\b|x\b|×)", RegexOptions.IgnoreCase)]
    private static partial Regex Metric();
}
