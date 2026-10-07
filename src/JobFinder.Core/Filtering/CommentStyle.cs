using System.Text.RegularExpressions;
using JobFinder.Core.Text;

namespace JobFinder.Core.Filtering;

/// <summary>
/// The code-level gate for Threads comment drafts (the prompt asks, this checks): short, conversational, no lecturing, no stack forced into
/// a non-technical thread, and one variant that asks the author something — the replies are what lift a comment in Threads.
/// Also which posts are worth a model call at all: Russian, and the post itself rather than a reply deep in someone's thread.
/// </summary>
public static partial class CommentStyle
{
    public const int MaxLength = 220;
    public const int MaxSentences = 3;

    /// <summary>The user comments in Russian (B1 English): a post mostly in another script or language is not worth a model call.</summary>
    public static bool IsRussian(string text)
    {
        var letters = text.Where(char.IsLetter).ToList();
        if (letters.Count < 20) return false;
        return letters.Count(c => c is >= 'а' and <= 'я' or >= 'А' and <= 'Я' or 'ё' or 'Ё') >= letters.Count * 0.5;
    }

    /// <summary>A reply inside someone else's thread ("В ответ @…"): a comment there is seen by almost nobody.</summary>
    public static bool IsReply(string text) => ReplyMarker().IsMatch(text.TrimStart());

    /// <summary>What is wrong with one variant (empty = fine).</summary>
    public static List<string> Problems(string variant, bool technicalThread)
    {
        var problems = new List<string>();
        var v = variant.Trim();
        if (v.Length > MaxLength) problems.Add($"длиннее {MaxLength} символов");
        if (Sentences(v) > MaxSentences) problems.Add($"больше {MaxSentences} предложений");
        if (Lecture().Match(TextUtil.Normalize(v)) is { Success: true } lecture) problems.Add($"менторский тон / канцелярит: «{lecture.Value}»");
        if (!technicalThread && Jargon().Match(TextUtil.Normalize(v)) is { Success: true } jargon)
            problems.Add($"профессия ввёрнута в нетехнический пост: «{jargon.Value}»");
        if (v.Contains('#') && HashTag().IsMatch(v)) problems.Add("хэштег");
        if (v.Contains("http", StringComparison.OrdinalIgnoreCase) || v.Contains("www.", StringComparison.OrdinalIgnoreCase)) problems.Add("ссылка");
        return problems;
    }

    /// <summary>What is wrong with the set: one variant must ask the author something, emoji in at most one variant.</summary>
    public static List<string> SetProblems(IReadOnlyList<string> variants)
    {
        var problems = new List<string>();
        if (!variants.Any(v => v.Contains('?'))) problems.Add("нет варианта с вопросом автору");
        if (variants.Count(HasEmoji) > 1) problems.Add("эмодзи больше чем в одном варианте");
        return problems;
    }

    /// <summary>Runs of <paramref name="n"/> normalized words: a variant sharing one with a prompt example copied the example.</summary>
    public static HashSet<string> Shingles(string text, int n = 5)
    {
        var words = WordSplit().Split(TextUtil.Normalize(text)).Where(w => w.Length > 0).ToList();
        var set = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i + n <= words.Count; i++) set.Add(string.Join(' ', words.Skip(i).Take(n)));
        return set;
    }

    public static bool CopiesExample(string variant, IReadOnlySet<string> exampleShingles) =>
        exampleShingles.Count > 0 && Shingles(variant).Overlaps(exampleShingles);

    public static int Sentences(string text) =>
        SentenceEnd().Split(text.Trim()).Count(s => s.Any(char.IsLetterOrDigit));

    private static bool HasEmoji(string text) => text.EnumerateRunes().Any(r => r.Value >= 0x1F300 || r.Value is >= 0x2600 and <= 0x27BF);

    [GeneratedRegex(@"[^\p{L}\p{N}]+")]
    private static partial Regex WordSplit();

    [GeneratedRegex(@"^(в ответ|ответ|replying to)\s+@", RegexOptions.IgnoreCase)]
    private static partial Regex ReplyMarker();

    [GeneratedRegex(@"[.!?…]+(?=\s|$)")]
    private static partial Regex SentenceEnd();

    [GeneratedRegex(@"(?<![\w&])#[\p{L}\d_]+")]
    private static partial Regex HashTag();

    // The lecture: teaching the author how hiring should work, and the article-summary clichés.
    [GeneratedRegex(@"это показывает|хорошо показыва|показывают, как важн|как важн[аоы] |важно отметить|следует помнить|стоит помнить|важно понимать|важен не только|важно не только|важна не только|не только .{1,60}, но и|^согласен[:,]|полностью согласен|отличный пост|классный пост|процесс\w* принятия решени|прозрачн\w+ (процесс|коммуникаци)|аккуратн\w+ фиксаци|единая коммуникаци|с точки зрения кандидата|уважительн|в конечном итоге|таким образом")]
    private static partial Regex Lecture();

    // Stack words forced into a thread about hiring or people (the profile tells the stack; the comment should not).
    [GeneratedRegex(@"идемпотент|распредел[её]нн\w* систем|kubernetes|кубер|микросервис|транзакци|\.net\b|c#|dotnet|бэкенд-разработчик|backend-разработчик|в работе с (распредел|систем|сервис|код)|как разработчик|как бэкенд|архитектур\w* (систем|решени)|kafka|rabbitmq|postgres|sql\b|api\b|eventual consistency|race condition")]
    private static partial Regex Jargon();
}
