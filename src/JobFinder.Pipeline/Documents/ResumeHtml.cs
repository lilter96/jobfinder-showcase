using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using JobFinder.Core.Profile;
using JobFinder.Core.Resume;

namespace JobFinder.Pipeline.Documents;

/// <summary>
/// The tailored resume as HTML for the PDF printer. ATS rules: one column, real text in reading order, standard section titles, contacts
/// as plain text in the body (not in a header/footer, links clickable but printed in full), no tables, icons or images, 10 pt text; one
/// restrained accent colour for people. Page rules: a job's heading, its project line and its first two points never part (the block
/// moves to the next page whole), a point is never cut between pages, a section title does not stay alone at the bottom of a page; what
/// the printer still gets wrong, <see cref="ResumePdfCheck"/> finds in the PDF and <paramref name="breakBefore"/> fixes with a page break.
/// </summary>
public static partial class ResumeHtml
{
    public const string Accent = "#1f4e8c";

    /// <summary>Points of a job kept on the page of its heading.</summary>
    public const int LeadBullets = 2;

    /// <param name="breakBefore">Blocks that start a new page ("job:1", "section:education"): the fix for a split the PDF check found.</param>
    /// <param name="wholeJobs">Every job on one page (moved whole to the next one when it does not fit); off when that takes too many pages.</param>
    public static string Render(ResumeDocument doc, CandidateProfile profile, string fontCss, IReadOnlySet<string>? breakBefore = null, bool wholeJobs = false)
    {
        var en = doc.Language == "en";
        var sb = new StringBuilder();
        sb.Append("<!doctype html><html lang=\"").Append(en ? "en" : "ru").Append("\"><head><meta charset=\"utf-8\"><title>")
            .Append(E(DisplayName(profile, en))).Append("</title><style>").Append(fontCss).Append(Css).Append("</style></head><body")
            .Append(wholeJobs ? " class=\"whole-jobs\"" : "").Append('>');

        sb.Append("<h1>").Append(E(DisplayName(profile, en))).Append("</h1>");
        if (!string.IsNullOrWhiteSpace(doc.Headline)) sb.Append("<div class=\"headline\">").Append(E(doc.Headline)).Append("</div>");
        // A contact never breaks inside (a URL cut at its hyphen is also cut in the extracted text); the line wraps between contacts.
        sb.Append("<div class=\"contacts\">").Append(string.Join("<span class=\"sep\"> · </span>", ContactLinks(profile).Select(c => $"<span class=\"nb\">{c}</span>")))
            .Append("</div>");
        // The writer's line from the confirmed facts when it names the candidate's city; the profile's otherwise.
        var place = !string.IsNullOrWhiteSpace(doc.Location) && (string.IsNullOrWhiteSpace(profile.Location)
                        || doc.Location.Contains(en ? Transliterate(profile.Location) : profile.Location, StringComparison.OrdinalIgnoreCase))
            ? doc.Location.Trim()
            : Place(profile, en);
        sb.Append("<div class=\"contacts\">").Append(E(place)).Append("</div>");

        string Open(string key) => breakBefore?.Contains(key) == true ? " class=\"force-break\"" : "";

        if (!string.IsNullOrWhiteSpace(doc.Summary))
            sb.Append("<section").Append(Open("section:summary")).Append("><h2>").Append(en ? "Summary" : "О себе").Append("</h2><p class=\"summary\">")
                .Append(E(doc.Summary)).Append("</p></section>");

        if (doc.Skills.Count > 0 || doc.Familiar.Count > 0)
        {
            sb.Append("<section").Append(Open("section:skills")).Append("><h2>").Append(en ? "Skills" : "Навыки").Append("</h2><div class=\"skills\">");
            foreach (var g in doc.Skills.Where(g => g.Items.Count > 0))
                sb.Append("<p><b>").Append(E(g.Group)).Append(":</b> ").Append(E(string.Join(", ", g.Items))).Append("</p>");
            if (doc.Familiar.Count > 0)
                sb.Append("<p><b>").Append(en ? "Also familiar with" : "Также знаком").Append(":</b> ").Append(E(string.Join(", ", doc.Familiar)))
                    .Append("</p>");
            sb.Append("</div></section>");
        }

        if (doc.Experience.Count > 0)
        {
            sb.Append("<section").Append(Open("section:experience")).Append("><h2>").Append(en ? "Experience" : "Опыт работы").Append("</h2>");
            for (var i = 0; i < doc.Experience.Count; i++)
            {
                var job = doc.Experience[i];
                sb.Append("<div class=\"job").Append(breakBefore?.Contains($"job:{i}") == true ? " force-break" : "").Append("\"><div class=\"keep\">")
                    .Append("<div class=\"job-head\"><div><span class=\"role\">").Append(E(job.Role)).Append("</span>")
                    .Append("<span class=\"at\"> — </span><span class=\"company\">").Append(E(job.Company)).Append("</span>");
                if (!string.IsNullOrWhiteSpace(job.Location)) sb.Append("<span class=\"loc\">, ").Append(E(job.Location)).Append("</span>");
                sb.Append("</div><div class=\"period\">").Append(E(job.Period)).Append("</div></div>");
                if (!string.IsNullOrWhiteSpace(job.Context)) sb.Append("<div class=\"context\">").Append(E(job.Context)).Append("</div>");
                AppendList(sb, job.Bullets.Take(LeadBullets));
                sb.Append("</div>");
                AppendList(sb, job.Bullets.Skip(LeadBullets), "more");
                if (job.Stack.Count > 0)
                    sb.Append("<div class=\"stack\"><b>").Append(en ? "Stack" : "Стек").Append(":</b> ").Append(E(string.Join(", ", job.Stack))).Append("</div>");
                sb.Append("</div>");
            }

            sb.Append("</section>");
        }

        if (doc.Education.Count > 0)
        {
            sb.Append("<section").Append(Open("section:education")).Append("><h2>").Append(en ? "Education" : "Образование").Append("</h2>");
            foreach (var ed in doc.Education)
                sb.Append("<div class=\"edu keep\"><div><span class=\"role\">").Append(E(ed.Institution)).Append("</span>")
                    .Append(string.IsNullOrWhiteSpace(ed.Degree) ? "" : ", " + E(ed.Degree)).Append("</div><div class=\"period\">")
                    .Append(E(ed.Period)).Append("</div></div>");
            sb.Append("</section>");
        }

        if (doc.Languages.Count > 0)
            sb.Append("<section").Append(Open("section:languages")).Append("><h2>").Append(en ? "Languages" : "Языки").Append("</h2><p>")
                .Append(E(string.Join("; ", doc.Languages))).Append("</p></section>");
        if (doc.Extras.Count > 0)
        {
            sb.Append("<section").Append(Open("section:extras")).Append("><h2>").Append(en ? "Additional" : "Дополнительно").Append("</h2>");
            AppendList(sb, doc.Extras);
            sb.Append("</section>");
        }

        sb.Append("</body></html>");
        return sb.ToString();
    }

    /// <summary>The section titles as printed, in order, with their keys (what <see cref="ResumePdfCheck"/> looks for).</summary>
    public static IEnumerable<(string Key, string Title)> Sections(ResumeDocument doc)
    {
        var en = doc.Language == "en";
        if (!string.IsNullOrWhiteSpace(doc.Summary)) yield return ("section:summary", en ? "Summary" : "О себе");
        if (doc.Skills.Count > 0 || doc.Familiar.Count > 0) yield return ("section:skills", en ? "Skills" : "Навыки");
        if (doc.Experience.Count > 0) yield return ("section:experience", en ? "Experience" : "Опыт работы");
        if (doc.Education.Count > 0) yield return ("section:education", en ? "Education" : "Образование");
        if (doc.Languages.Count > 0) yield return ("section:languages", en ? "Languages" : "Языки");
        if (doc.Extras.Count > 0) yield return ("section:extras", en ? "Additional" : "Дополнительно");
    }

    private static void AppendList(StringBuilder sb, IEnumerable<string> items, string? css = null)
    {
        var list = items.ToList();
        if (list.Count == 0) return;
        sb.Append(css is null ? "<ul>" : $"<ul class=\"{css}\">");
        foreach (var item in list) sb.Append("<li>").Append(E(item)).Append("</li>");
        sb.Append("</ul>");
    }

    /// <summary>The contacts as they are printed: the text itself is the address (a parser reads it), the link is for a person.</summary>
    public static IEnumerable<string> ContactLinks(CandidateProfile p)
    {
        if (p.Contacts.Telegram is { Length: > 0 } tg)
            yield return $"Telegram <a href=\"https://t.me/{E(tg.TrimStart('@'))}\">{E(tg.StartsWith('@') ? tg : "@" + tg)}</a>";
        if (p.Contacts.Email is { Length: > 0 } email) yield return $"<a href=\"mailto:{E(email)}\">{E(email)}</a>";
        if (p.Contacts.Phone is { Length: > 0 } phone) yield return $"<a href=\"tel:{E(new string(phone.Where(c => char.IsDigit(c) || c == '+').ToArray()))}\">{E(phone)}</a>";
        if (p.Contacts.GitHub is { Length: > 0 } gh) yield return Link(gh);
        if (p.Contacts.LinkedIn is { Length: > 0 } li) yield return Link(li);
    }

    private static string Link(string address)
    {
        var shown = address.Replace("https://", "", StringComparison.OrdinalIgnoreCase).Replace("http://", "", StringComparison.OrdinalIgnoreCase)
            .Replace("www.", "", StringComparison.OrdinalIgnoreCase).TrimEnd('/');
        return $"<a href=\"https://{E(shown)}\">{E(shown)}</a>";
    }

    /// <summary>Plain-text contacts (the letter card, tests): Telegram, e-mail, phone, then the place and formats.</summary>
    public static IEnumerable<string> Contacts(CandidateProfile p, bool en)
    {
        if (!string.IsNullOrWhiteSpace(p.Contacts.Telegram)) yield return "Telegram " + p.Contacts.Telegram;
        if (!string.IsNullOrWhiteSpace(p.Contacts.Email)) yield return p.Contacts.Email!;
        if (!string.IsNullOrWhiteSpace(p.Contacts.Phone)) yield return p.Contacts.Phone!;
        yield return Place(p, en);
    }

    private static string Place(CandidateProfile p, bool en)
    {
        var formats = p.WorkFormats.Select(f => (f, en) switch
        {
            ("office", false) => "офис",
            ("hybrid", false) => "гибрид",
            ("remote", false) => "удалённо",
            ("office", true) => "office",
            ("hybrid", true) => "hybrid",
            ("remote", true) => "remote",
            _ => f,
        });
        var place = en ? Transliterate(p.Location) + (p.Location.Contains("Минск", StringComparison.OrdinalIgnoreCase) ? ", Belarus" : "") : p.Location;
        var line = string.Join(" / ", formats);
        return line.Length > 0 ? $"{place} · {line}" : place;
    }

    /// <summary>The name in the resume's language: the profile's Latin spelling when given, a transliteration otherwise (no patronymic in English).</summary>
    public static string DisplayName(CandidateProfile p, bool en)
    {
        if (!en) return p.Name;
        if (!string.IsNullOrWhiteSpace(p.NameLatin)) return p.NameLatin!;
        var parts = p.Name.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        // "Фамилия Имя Отчество" → "Imya Familiya".
        return parts.Length >= 2 ? $"{Transliterate(parts[1])} {Transliterate(parts[0])}" : Transliterate(p.Name);
    }

    public static string Transliterate(string s)
    {
        var map = new Dictionary<char, string>
        {
            ['а'] = "a", ['б'] = "b", ['в'] = "v", ['г'] = "g", ['д'] = "d", ['е'] = "e", ['ё'] = "e", ['ж'] = "zh", ['з'] = "z", ['и'] = "i",
            ['й'] = "y", ['к'] = "k", ['л'] = "l", ['м'] = "m", ['н'] = "n", ['о'] = "o", ['п'] = "p", ['р'] = "r", ['с'] = "s", ['т'] = "t",
            ['у'] = "u", ['ф'] = "f", ['х'] = "kh", ['ц'] = "ts", ['ч'] = "ch", ['ш'] = "sh", ['щ'] = "shch", ['ъ'] = "", ['ы'] = "y", ['ь'] = "",
            ['э'] = "e", ['ю'] = "yu", ['я'] = "ya",
        };
        var sb = new StringBuilder();
        foreach (var c in s)
        {
            var lower = char.ToLowerInvariant(c);
            if (!map.TryGetValue(lower, out var t))
            {
                sb.Append(c);
                continue;
            }

            sb.Append(char.IsUpper(c) && t.Length > 0 ? char.ToUpperInvariant(t[0]) + t[1..] : t);
        }

        return sb.ToString();
    }

    /// <summary>
    /// Text for the page: encoded, and a number never parted from its digit groups and unit («5 000+ RPS», «200 мс», «60%» stay on one
    /// line: a line break there reads as two numbers, to a person and to a parser).
    /// </summary>
    private static string E(string? s) => WebUtility.HtmlEncode(NumberUnit().Replace(DigitGroup().Replace(s ?? "", "$1 $2"), "$1 $2"));

    [GeneratedRegex(@"(\d)[  ](\d{3}(?!\d))")]
    private static partial Regex DigitGroup();

    [GeneratedRegex(@"(\d\+?|<\s?\d[\d.,]*) (мс|ms|RPS|rps|req/s|сек\p{L}*|мин\p{L}*|час\p{L}*|пользовател\p{L}*|заказ\p{L}*|мест\p{L}*|раз\p{L}*|компонент\p{L}*|%)")]
    private static partial Regex NumberUnit();

    private const string Css = """
        @page { size: A4; margin: 14mm 16mm 14mm 16mm; }
        * { box-sizing: border-box; }
        body { font-family: 'Inter', 'Segoe UI', Arial, sans-serif; font-size: 10pt; line-height: 1.38; color: #1c2230; margin: 0; }
        h1 { font-size: 20pt; font-weight: 700; margin: 0; letter-spacing: -0.3px; color: #111827; }
        .headline { font-size: 12pt; font-weight: 600; color: #1f4e8c; margin: 2pt 0 4pt; }
        .contacts { font-size: 9.2pt; color: #4b5563; }
        .contacts a { color: #1f2937; text-decoration: none; }
        .nb { white-space: nowrap; }
        h2 { font-size: 10pt; font-weight: 700; text-transform: uppercase; letter-spacing: 0.7pt; color: #1f4e8c;
             border-bottom: 1px solid #d6dde8; padding-bottom: 2pt; margin: 11pt 0 5pt; break-after: avoid; page-break-after: avoid; }
        p { margin: 0 0 3pt; orphans: 3; widows: 3; }
        .skills p { margin: 0 0 2.5pt; }
        .skills b, .role { font-weight: 600; color: #111827; }
        .company { font-weight: 600; color: #1f2937; }
        .at, .loc { color: #4b5563; }
        .job { margin: 0 0 8pt; }
        .keep, .whole-jobs .job { break-inside: avoid; page-break-inside: avoid; }
        .job-head, .edu { display: flex; justify-content: space-between; align-items: baseline; gap: 12pt; }
        .period { color: #4b5563; white-space: nowrap; font-size: 9.4pt; }
        .context { color: #4b5563; font-style: italic; margin: 1pt 0 0; }
        ul { margin: 2pt 0 0 13pt; padding: 0; }
        ul.more { margin-top: 0; }
        li { margin: 0 0 2pt; padding-left: 1pt; break-inside: avoid; page-break-inside: avoid; }
        li::marker { color: #1f4e8c; }
        .stack { font-size: 9pt; color: #4b5563; margin: 2pt 0 0; }
        .stack b { font-weight: 600; color: #374151; }
        .force-break { break-before: page; page-break-before: always; }
        """;
}
