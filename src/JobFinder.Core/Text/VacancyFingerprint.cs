using System.Text.RegularExpressions;

namespace JobFinder.Core.Text;

/// <summary>
/// The key that merges one opening seen in several sources ("Senior .NET разработчик" at "ООО Рога и Копыта" on hh and
/// "Senior .NET Developer" at "Рога и копыта" in a channel). Company is mandatory: without it two different employers'
/// "Senior .NET Developer" would collapse into one row, so a post without a company gets a unique, source-bound key instead.
/// </summary>
public static partial class VacancyFingerprint
{
    private static readonly Dictionary<string, string> Synonyms = new(StringComparer.Ordinal)
    {
        ["developer"] = "dev", ["разработчик"] = "dev", ["программист"] = "dev", ["engineer"] = "dev", ["инженер"] = "dev",
        ["software"] = "", ["программного"] = "", ["обеспечения"] = "", ["по"] = "",
        ["бэкенд"] = "backend", ["бекенд"] = "backend", ["back-end"] = "backend", ["back"] = "backend", ["end"] = "",
        ["старший"] = "senior", ["сеньор"] = "senior", ["синьор"] = "senior", ["sr"] = "senior",
        ["ведущий"] = "lead", ["лид"] = "lead", ["тимлид"] = "teamlead", ["team"] = "", ["teamlead"] = "teamlead",
        ["мидл"] = "middle", ["mid"] = "middle",
        ["c#"] = "csharp", [".net"] = "dotnet", ["net"] = "dotnet", ["asp.net"] = "dotnet", ["core"] = "",
        ["удаленно"] = "", ["remote"] = "", ["удаленка"] = "",
    };

    private static readonly Regex LegalForms = new(
        @"(?<![\p{L}\p{N}])(ооо|оао|зао|пао|ао|ип|одо|чуп|уп|llc|ltd|inc|gmbh|group|группа компаний|гк)(?![\p{L}\p{N}])",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    public static string Compute(string? company, string title, string uniqueFallback)
    {
        var c = NormalizeCompany(company);
        if (c.Length == 0) return $"~{uniqueFallback}";
        return $"{c}|{NormalizeTitle(title)}";
    }

    public static string NormalizeCompany(string? company)
    {
        var s = TextUtil.Normalize(company);
        if (s.Length == 0) return "";
        s = Quotes().Replace(s, " ");
        s = LegalForms.Replace(s, " ");
        s = NonWord().Replace(s, "");
        return s;
    }

    public static string NormalizeTitle(string title)
    {
        var s = TextUtil.Normalize(title);
        s = Parenthesized().Replace(s, " ");
        var tokens = TitleToken().Matches(s)
            .Select(m => Synonyms.TryGetValue(m.Value, out var mapped) ? mapped : m.Value)
            .Where(t => t.Length > 0)
            .Distinct()
            .Order(StringComparer.Ordinal);
        return string.Join(' ', tokens);
    }

    [GeneratedRegex("[\"'«»“”„`]")]
    private static partial Regex Quotes();

    [GeneratedRegex(@"[^\p{L}\p{N}]+")]
    private static partial Regex NonWord();

    // A parenthesized tail is usually the location or the format ("(Минск)", "(удалённо)"): not part of the role.
    [GeneratedRegex(@"\([^)]*\)")]
    private static partial Regex Parenthesized();

    [GeneratedRegex(@"asp\.net|\.net|c#|[\p{L}\p{N}]+(?:-[\p{L}\p{N}]+)*")]
    private static partial Regex TitleToken();
}
