using System.Text.RegularExpressions;
using JobFinder.Core.Domain;

namespace JobFinder.Core.Text;

/// <summary>
/// How a message to an employer is laid out. The first line names the vacancy — built by code from the vacancy itself, so a recruiter
/// with twenty open roles knows at once which one it is about (title, company, the link to the post or the page). The body the model
/// writes comes in short paragraphs with empty lines between them, and the closing stands on its own line: a message is read on a phone.
/// </summary>
public static partial class LetterLayout
{
    public const int LinkedInLimit = 300;

    public static string LinkedInReference(Vacancy vacancy)
    {
        var title = TextUtil.Truncate(Tidy(vacancy.Title).Trim('«', '»', '"'), 45);
        var company = Tidy(vacancy.CompanyName);
        var known = company.Length > 0 && !Unknown().IsMatch(company) && !title.Contains(company, StringComparison.OrdinalIgnoreCase);
        var line = $"Вакансия «{title}»" + (known ? $" ({TextUtil.Truncate(company, 25)})" : "");
        return Uri.TryCreate(vacancy.Url, UriKind.Absolute, out var url) && url.Scheme is "http" or "https"
            ? $"{line}: {url.AbsoluteUri}" : line + ".";
    }

    /// <param name="greeting">"Здравствуйте, Валентина!"; empty in a running conversation.</param>
    /// <param name="reference">The vacancy line (<see cref="VacancyReference"/>); null when the reader already knows (hh form, a reply).</param>
    public static string Compose(string? greeting, string? reference, IEnumerable<string> paragraphs, string? closing)
    {
        var blocks = new List<string>();
        var head = string.Join(" ", new[] { greeting, reference }.Select(Tidy).Where(s => s.Length > 0));
        if (head.Length > 0) blocks.Add(head);
        blocks.AddRange(paragraphs.Select(Tidy).Where(p => p.Length > 0));
        if (Tidy(closing) is { Length: > 0 } end) blocks.Add(end);
        return string.Join("\n\n", blocks);
    }

    /// <summary>
    /// "Пишу по вакансии «Senior C#/.NET Developer» (KA Career): https://t.me/DotNetRuJobsFeed/5885." The company is left out when unknown
    /// or already in the title; the link is the vacancy's own address (the post in the channel, the page on hh).
    /// </summary>
    public static string VacancyReference(Vacancy v)
    {
        var title = Tidy(v.Title).Trim('«', '»', '"');
        var company = Tidy(v.CompanyName);
        var known = company.Length > 0 && !Unknown().IsMatch(company) && !title.Contains(company, StringComparison.OrdinalIgnoreCase);
        var line = $"Пишу по вакансии «{title}»" + (known ? $" ({company})" : "");
        return Uri.TryCreate(v.Url, UriKind.Absolute, out var url) && url.Scheme is "http" or "https" ? $"{line}: {url.AbsoluteUri}" : line + ".";
    }

    /// <summary>The body the model wrote as one line per paragraph: breaks inside a paragraph and repeated spaces go.</summary>
    public static string Tidy(string? text) => Spaces().Replace(text ?? "", " ").Trim();

    [GeneratedRegex(@"^(компания )?не указан\p{L}*$|^unknown$|^n/?a$|^—?$", RegexOptions.IgnoreCase)]
    private static partial Regex Unknown();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Spaces();
}
