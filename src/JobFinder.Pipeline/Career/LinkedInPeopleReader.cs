using System.Text;
using JobFinder.Core.Text;
using JobFinder.Integrations.LinkedIn;
using JobFinder.Llm.Tasks;
using JobFinder.Pipeline.Modules.Hiring;

namespace JobFinder.Pipeline.Career;

/// <summary>A person found at a company: the profile link always comes from the page, the reading from the model (or the rules).</summary>
public sealed record CompanyPerson(string Name, string Headline, string ProfileUrl, string? Role, bool IsRecruiter, string ReadBy);

/// <summary>
/// Who works at a company, from a LinkedIn people search. The page gives layout-free blocks (profile link + card text); a cheap model
/// reads them — result or stray link, current employer (never a past one), recruiter or not, on leave — and code keeps only what the
/// block itself supports: the name and the employer must be in the block's text, the link is the block's own. Paid reading requests
/// checkpoint their captured page and operation; a model outage resumes that reading without repeating the browser search.
/// </summary>
public sealed class LinkedInPeopleReader(LinkedInClient linkedIn, LinkedInPeopleRequests requests, LinkedInSearchQuota quota, ILogger<LinkedInPeopleReader> log)
{
    public async Task<List<CompanyPerson>> FindAtCompanyAsync(string company, CancellationToken ct, string? diagnosePath = null)
    {
        if (string.IsNullOrWhiteSpace(company)) throw new ArgumentException("company required", nameof(company));
        if (!quota.Enabled || !await linkedIn.HasSessionAsync(ct)) return [];
        if (await requests.ReusableAsync(company, ct) is { } cached)
        {
            var saved = await requests.WaitAsync(cached, ct);
            return FromSaved(saved.Input, saved.Result).People;
        }
        if (!await quota.TakeAsync(ct)) return [];
        var page = await linkedIn.SearchAsync($"{company} recruiter", ct, diagnosePath);
        return await ReadAsync(company, page, ct);
    }

    public async Task<List<CompanyPerson>> ReadAsync(string company, LinkedInSearchPage page, CancellationToken ct) =>
        (await ReadWithReasonsAsync(company, page, ct)).People;

    /// <returns>The accepted people and the model's raw reading of every block (null when the rules were used).</returns>
    public async Task<(List<CompanyPerson> People, LinkedInPeople? Reading)> ReadWithReasonsAsync(string company, LinkedInSearchPage page,
        CancellationToken ct)
    {
        var id = await requests.RequestAsync(company, page, ct);
        var saved = await requests.WaitAsync(id, ct);
        var result = FromSaved(saved.Input, saved.Result);
        log.Info($"[linkedin] {company}: {result.People.Count} work there (saved preparation {id})");
        return result;
    }

    private static (List<CompanyPerson> People, LinkedInPeople? Reading) FromSaved(LinkedInPeopleInput input, LinkedInPeopleResult result)
    {
        var page = input.Page();
        var company = input.Company;
        if (result.Reading is { } reading)
        {
            var people = Accept(company, page.Cards, reading);
            return (people, reading);
        }
        return (page.Parsed
            .Where(p => OutreachService.WorksAt(company, $"{p.Headline} | {p.Details}"))
            .Select(p => new CompanyPerson(p.Name, p.Headline, p.ProfileUrl, null, IsRecruiterLike(p.Headline), "rules"))
            .ToList(), null);
    }

    public static string Blocks(IReadOnlyList<LinkedInCard> cards)
    {
        var sb = new StringBuilder();
        for (var i = 0; i < cards.Count; i++) sb.Append('[').Append(i + 1).Append("]\n").Append(TextUtil.Truncate(cards[i].Text, 700)).Append("\n\n");
        return sb.ToString();
    }

    /// <summary>The model's reading, kept only where the block supports it.</summary>
    public static List<CompanyPerson> Accept(string company, IReadOnlyList<LinkedInCard> cards, LinkedInPeople reading)
    {
        var result = new List<CompanyPerson>();
        foreach (var r in reading.People)
        {
            if (!r.IsResult || !r.WorksAtTarget || r.OnLeave || r.Index < 1 || r.Index > cards.Count) continue;
            var card = cards[r.Index - 1];
            var text = TextUtil.Normalize(card.Text);
            // Grounding: a name or an employer that is not in the block is the model's invention.
            if (string.IsNullOrWhiteSpace(r.Name) || !text.Contains(TextUtil.Normalize(r.Name), StringComparison.Ordinal)) continue;
            if (string.IsNullOrWhiteSpace(r.CurrentCompany) || !text.Contains(TextUtil.Normalize(r.CurrentCompany), StringComparison.Ordinal)) continue;
            // "Same company" is the model's claim; code agrees only on the same distinctive words or the same name in another alphabet.
            if (!SameCompany(company, r.CurrentCompany)) continue;
            if (result.Any(p => p.ProfileUrl == card.ProfileUrl)) continue;
            var headline = !string.IsNullOrWhiteSpace(r.Headline) && text.Contains(TextUtil.Normalize(r.Headline), StringComparison.Ordinal)
                ? r.Headline.Trim()
                : r.CurrentRole ?? "";
            result.Add(new CompanyPerson(r.Name.Trim(), headline, card.ProfileUrl, r.CurrentRole, r.IsRecruiter, "llm"));
        }

        return result.OrderByDescending(p => p.IsRecruiter).ToList();
    }

    /// <summary>
    /// The target and the employer the model read are one company: the target's distinctive words are in the employer's name, or the
    /// first distinctive words are the same after transliteration ("Яндекс" ≈ "Yandex", edit distance ≤ 2). "Леста Игры" is not "Wargaming".
    /// </summary>
    public static bool SameCompany(string target, string employer)
    {
        if (OutreachService.WorksAt(target, employer)) return true;
        var a = OutreachService.CompanyWords(target).FirstOrDefault();
        var b = OutreachService.CompanyWords(employer).FirstOrDefault();
        if (a is null || b is null) return false;
        // Fuzzy only across alphabets (a transliteration is inexact); in one alphabet a one-letter difference is another company
        // ("iTechArt" is not "TechArt").
        if (IsCyrillic(a) == IsCyrillic(b)) return false;
        var la = Documents.ResumeHtml.Transliterate(a).ToLowerInvariant();
        var lb = Documents.ResumeHtml.Transliterate(b).ToLowerInvariant();
        return Math.Min(la.Length, lb.Length) >= 4 && Distance(la, lb) <= 2;
    }

    private static bool IsCyrillic(string word) => word.Any(c => c is >= 'а' and <= 'я' or 'ё');

    private static int Distance(string a, string b)
    {
        var d = new int[a.Length + 1, b.Length + 1];
        for (var i = 0; i <= a.Length; i++) d[i, 0] = i;
        for (var j = 0; j <= b.Length; j++) d[0, j] = j;
        for (var i = 1; i <= a.Length; i++)
        for (var j = 1; j <= b.Length; j++)
            d[i, j] = Math.Min(Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1), d[i - 1, j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1));
        return d[a.Length, b.Length];
    }

    private static readonly string[] RecruiterWords = ["recruit", "рекрут", "talent", "hr ", "hr-", "hrbp", "hiring", "sourc", "подбор", "найм"];

    private static bool IsRecruiterLike(string headline)
    {
        var h = TextUtil.Normalize(headline) + " ";
        return RecruiterWords.Any(h.Contains);
    }
}
