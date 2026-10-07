using System.Text.RegularExpressions;

namespace JobFinder.Core.Text;

/// <summary>
/// Words to their stems (Snowball: the Russian and English stemmers of snowballstem.org), so a phrase matches in any of its forms:
/// «высоконагруженных систем» in a vacancy, «высоконагруженные системы» in a resume. This is how an ATS and a recruiter's search read a
/// resume; a substring test would call the resume wrong.
/// </summary>
public static partial class Stems
{
    // The stemmers keep their working buffer in the instance: one per thread.
    private static readonly ThreadLocal<Snowball.RussianStemmer> Russian = new(() => new Snowball.RussianStemmer());
    private static readonly ThreadLocal<Snowball.EnglishStemmer> English = new(() => new Snowball.EnglishStemmer());

    /// <summary>Words that carry no meaning of their own in a requirement («опыт работы с», «знание»).</summary>
    private static readonly HashSet<string> Stop = new(StringComparer.Ordinal)
    {
        "и", "в", "во", "на", "с", "со", "по", "для", "к", "о", "об", "от", "из", "или", "а", "же", "не", "как", "при", "через", "под", "над",
        "the", "and", "or", "of", "in", "on", "for", "with", "to", "a", "an", "at", "by",
    };

    public static string Stem(string word)
    {
        var w = word.ToLowerInvariant().Replace('ё', 'е');
        if (w.Length < 3) return w;
        if (Cyrillic().IsMatch(w)) return Russian.Value!.Stem(w);
        return LatinWord().IsMatch(w) ? English.Value!.Stem(w) : w;
    }

    /// <summary>The meaningful words of a text as stems, in order («опыт разработки микросервисов» → опыт, разработк, микросервис).</summary>
    public static List<string> Of(string? text) =>
        Word().Matches(text ?? "").Select(m => m.Value).Where(w => !Stop.Contains(w.ToLowerInvariant())).Select(Stem).ToList();

    /// <summary>Every meaningful word of <paramref name="phrase"/> is among <paramref name="stems"/> (order-free, as a keyword search is).</summary>
    public static bool Covers(IReadOnlySet<string> stems, string phrase)
    {
        var words = Of(phrase);
        return words.Count > 0 && words.All(w => stems.Contains(w) || stems.Any(s => Same(s, w)));
    }

    /// <summary>
    /// Two stems of one word. Snowball cuts some forms deeper than others — «систем» (genitive plural) → «сист», «системы» → «систем» — so
    /// a stem that is the other's beginning, short of it by at most three letters and at least four long, is the same word.
    /// </summary>
    public static bool Same(string a, string b)
    {
        if (a == b) return true;
        var (shorter, longer) = a.Length <= b.Length ? (a, b) : (b, a);
        return shorter.Length >= 4 && longer.Length - shorter.Length <= 3 && longer.StartsWith(shorter, StringComparison.Ordinal);
    }

    [GeneratedRegex(@"[\p{L}\p{N}]+(?:[-'][\p{L}\p{N}]+)*")]
    private static partial Regex Word();

    [GeneratedRegex(@"\p{IsCyrillic}")]
    private static partial Regex Cyrillic();

    [GeneratedRegex(@"^[a-z]+$")]
    private static partial Regex LatinWord();
}
