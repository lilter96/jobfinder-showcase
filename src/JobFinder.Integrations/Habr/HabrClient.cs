using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using JobFinder.Core.Domain;
using JobFinder.Core.Text;
using JobFinder.Integrations.HeadHunter;
using JobFinder.Integrations.Http;

namespace JobFinder.Integrations.Habr;

public sealed class HabrOptions
{
    public const string Section = "Habr";

    public bool Enabled { get; set; } = true;

    public List<string> Queries { get; set; } = [".NET", "C#"];

    /// <summary>Extra query-string appended to every search (e.g. <c>remote=true</c>); empty = all formats.</summary>
    public List<string> Filters { get; set; } = [""];

    public int MaxPages { get; set; } = 2;

    public int IntervalMinutes { get; set; } = 30;

    public int JitterMinutes { get; set; } = 10;

    /// <summary>Cities mapped to Belarus (Habr gives only a city name).</summary>
    public List<string> BelarusCities { get; set; } = ["Минск", "Гомель", "Брест", "Гродно", "Витебск", "Могилёв", "Могилев"];
}

public sealed record HabrVacancyBrief(
    long Id,
    string Title,
    string? Company,
    string? CompanyAlias,
    bool Remote,
    List<string> Locations,
    decimal? SalaryFrom,
    decimal? SalaryTo,
    string? Currency,
    string? Qualification,
    List<string> Skills,
    DateTimeOffset? PublishedAt,
    string Url,
    string RawJson);

/// <summary>career.habr.com: the frontend JSON API for search, the JSON-LD JobPosting of the vacancy page for the description.</summary>
public sealed class HabrClient(PoliteHttp http)
{
    public const string Host = "career.habr.com";

    public async Task<(List<HabrVacancyBrief> Items, int TotalPages)> SearchAsync(string query, string filter, int page, CancellationToken ct)
    {
        var url = $"https://{Host}/api/frontend/vacancies?q={Uri.EscapeDataString(query)}&type=all&sort=date&page={page}";
        if (!string.IsNullOrWhiteSpace(filter)) url += "&" + filter.TrimStart('&');
        var json = await http.GetStringAsync(new Uri(url), ct, "application/json");
        return ParseSearch(json);
    }

    public async Task<string> GetDescriptionAsync(long id, CancellationToken ct)
    {
        var html = await http.GetStringAsync(new Uri($"https://{Host}/vacancies/{id}"), ct);
        return ParseDescription(html);
    }

    internal static (List<HabrVacancyBrief> Items, int TotalPages) ParseSearch(string json)
    {
        var root = JsonNode.Parse(json) ?? throw new FormatException("empty Habr response");
        var items = new List<HabrVacancyBrief>();
        foreach (var v in root["list"]?.AsArray() ?? [])
        {
            if (v is null || HhStateParser.Long(v["id"]) is not { } id) continue;
            var salary = v["salary"];
            items.Add(new HabrVacancyBrief(
                id,
                HhStateParser.Str(v["title"])?.Trim() ?? "",
                HhStateParser.Str(v["company"]?["title"]),
                HhStateParser.Str(v["company"]?["alias_name"]),
                HhStateParser.Bool(v["remoteWork"]),
                (v["locations"]?.AsArray() ?? []).Select(l => HhStateParser.Str(l?["title"])).OfType<string>().ToList(),
                HhStateParser.Dec(salary?["from"]),
                HhStateParser.Dec(salary?["to"]),
                Currency.Normalize(HhStateParser.Str(salary?["currency"])),
                HhStateParser.Str(v["qualification"]),
                (v["skills"]?.AsArray() ?? []).Select(s => HhStateParser.Str(s?["title"])).OfType<string>().ToList(),
                HhStateParser.Str(v["publishedDate"]?["date"]) is { } d
                && DateTimeOffset.TryParse(d, CultureInfo.InvariantCulture, DateTimeStyles.None, out var date) ? date : null,
                $"https://{Host}/vacancies/{id}",
                v.ToJsonString()));
        }

        return (items, (int)(HhStateParser.Long(root["meta"]?["totalPages"]) ?? 1));
    }

    internal static string ParseDescription(string html)
    {
        const string open = "<script type=\"application/ld+json\">";
        var at = html.IndexOf(open, StringComparison.Ordinal);
        if (at < 0) return "";
        var start = at + open.Length;
        var end = html.IndexOf("</script>", start, StringComparison.Ordinal);
        if (end < 0) return "";
        try
        {
            var node = JsonNode.Parse(html[start..end]);
            return TextUtil.HtmlToText(HhStateParser.Str(node?["description"]));
        }
        catch (JsonException)
        {
            return "";
        }
    }

    public static WorkFormat Formats(HabrVacancyBrief v) =>
        (v.Remote ? WorkFormat.Remote : WorkFormat.None) | (v.Locations.Count > 0 ? WorkFormat.Office : WorkFormat.None);
}
