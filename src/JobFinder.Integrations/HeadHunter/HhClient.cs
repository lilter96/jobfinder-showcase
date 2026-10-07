using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using JobFinder.Integrations.Http;

namespace JobFinder.Integrations.HeadHunter;

/// <summary>
/// Public (logged-out) pages of hh.ru / rabota.by: search, vacancy, employer. The account is never touched here. Every read goes through
/// the site's shared budget (<see cref="PoliteHttp"/>): <paramref name="priority"/> says who needs the page, <paramref name="maxWait"/> how
/// long the caller may wait for a slot.
/// </summary>
public sealed partial class HhClient(PoliteHttp http)
{
    public const int PageSize = 100;

    public async Task<(List<HhVacancyBrief> Items, int Total)> SearchAsync(HhSearch search, int page, CancellationToken ct,
        ScrapePriority priority = ScrapePriority.Normal, TimeSpan? maxWait = null)
    {
        var html = await http.GetStringAsync(SearchUrl(search, page), ct, priority: priority, maxWait: maxWait);
        return HhStateParser.ParseSearch(HhStateParser.ExtractState(html), search.Host);
    }

    public async Task<HhVacancyDetail> GetVacancyAsync(string host, long id, CancellationToken ct, ScrapePriority priority = ScrapePriority.Normal,
        TimeSpan? maxWait = null)
    {
        var html = await http.GetStringAsync(new Uri(HhStateParser.VacancyUrl(host, id)), ct, priority: priority, maxWait: maxWait);
        return HhStateParser.ParseVacancy(HhStateParser.ExtractState(html), host);
    }

    public async Task<HhEmployer> GetEmployerAsync(string host, long id, CancellationToken ct, ScrapePriority priority = ScrapePriority.Normal,
        TimeSpan? maxWait = null)
    {
        var html = await http.GetStringAsync(new Uri($"https://{host}/employer/{id}"), ct, priority: priority, maxWait: maxWait);
        return HhStateParser.ParseEmployer(HhStateParser.ExtractState(html));
    }

    public static Uri SearchUrl(HhSearch s, int page)
    {
        var q = new StringBuilder();
        Add("text", s.Text);
        if (!string.IsNullOrWhiteSpace(s.SearchField)) Add("search_field", s.SearchField);
        foreach (var area in s.Areas) Add("area", area.ToString(CultureInfo.InvariantCulture));
        foreach (var format in s.WorkFormats) Add("work_format", format);
        Add("order_by", "publication_time");
        Add("search_period", s.PeriodDays.ToString(CultureInfo.InvariantCulture));
        Add("items_on_page", PageSize.ToString(CultureInfo.InvariantCulture));
        Add("page", page.ToString(CultureInfo.InvariantCulture));
        return new Uri($"https://{s.Host}/search/vacancy?{q}");

        void Add(string key, string value)
        {
            if (q.Length > 0) q.Append('&');
            q.Append(key).Append('=').Append(Uri.EscapeDataString(value));
        }
    }

    /// <summary>A vacancy link in a post: hh.ru, rabota.by, any regional *.hh.ru subdomain, with or without www.</summary>
    public static bool TryParseVacancyUrl(string? text, out string host, out long id)
    {
        host = "";
        id = 0;
        if (string.IsNullOrEmpty(text)) return false;
        var m = VacancyLink().Match(text);
        if (!m.Success) return false;
        host = m.Groups["host"].Value.EndsWith("rabota.by", StringComparison.OrdinalIgnoreCase) ? "rabota.by" : "hh.ru";
        return long.TryParse(m.Groups["id"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out id);
    }

    /// <summary>Every vacancy id linked in the text (a shared message can carry several), in order, without repeats.</summary>
    public static IReadOnlyList<long> ParseVacancyIds(string? text) =>
        string.IsNullOrEmpty(text)
            ? []
            : VacancyLink().Matches(text).Select(m => long.TryParse(m.Groups["id"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id) ? id : 0)
                .Where(id => id > 0).Distinct().ToList();

    [GeneratedRegex(@"(?<host>(?:[a-z0-9-]+\.)?(?:hh\.ru|rabota\.by))/vacancy/(?<id>\d{5,12})", RegexOptions.IgnoreCase)]
    private static partial Regex VacancyLink();
}
