using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using JobFinder.Core.Domain;
using JobFinder.Core.Text;

namespace JobFinder.Integrations.HeadHunter;

/// <summary>A search result row. Search rows carry the flags the detail page lacks (letter required, employer test).</summary>
public sealed record HhVacancyBrief(
    long Id,
    string Host,
    string Name,
    long? CompanyId,
    string? CompanyName,
    string? AreaName,
    string? CountryCode,
    WorkFormat Formats,
    bool LetterRequired,
    bool HasTest,
    bool Closed,
    DateTimeOffset? PublishedAt,
    string Url,
    DateTimeOffset? CreatedAt = null,
    int? ResponsesCount = null);

public sealed record HhVacancyDetail(
    long Id,
    string Host,
    string Name,
    long? CompanyId,
    string? CompanyName,
    string? AreaName,
    string? CountryCode,
    WorkFormat Formats,
    decimal? SalaryFrom,
    decimal? SalaryTo,
    string? Currency,
    bool? SalaryGross,
    EnglishLevel English,
    string? Experience,
    List<string> KeySkills,
    string Description,
    bool Closed,
    DateTimeOffset? PublishedAt,
    string Url,
    string RawJson);

public sealed record HhEmployer(
    long Id,
    string Name,
    string? Site,
    string? Area,
    string? SizeCategory,
    List<string> Industries,
    string? Description,
    decimal? Rating,
    bool ItAccredited,
    int? OpenVacancies);

/// <summary>
/// hh.ru and rabota.by render every page with its whole state as JSON in <c>&lt;template id="HH-Lux-InitialState"&gt;</c>.
/// Parsing that JSON survives markup redesigns; the field shapes differ between the search row and the detail page
/// (e.g. <c>workFormats</c> is nested in one and flat in the other), so every reader here accepts both.
/// </summary>
public static class HhStateParser
{
    private const string Marker = "id=\"HH-Lux-InitialState\"";

    public static JsonNode ExtractState(string html)
    {
        var at = html.IndexOf(Marker, StringComparison.Ordinal);
        if (at < 0) throw new FormatException("HH-Lux-InitialState not found (anti-bot page or a layout change)");
        var start = html.IndexOf('>', at) + 1;
        var end = html.IndexOf("</template>", start, StringComparison.Ordinal);
        if (start <= 0 || end < 0) throw new FormatException("HH-Lux-InitialState is not closed");
        return JsonNode.Parse(WebUtility.HtmlDecode(html[start..end])) ?? throw new FormatException("HH-Lux-InitialState is empty");
    }

    public static (List<HhVacancyBrief> Items, int Total) ParseSearch(JsonNode state, string host)
    {
        var result = state["vacancySearchResult"] ?? throw new FormatException("vacancySearchResult missing");
        var items = new List<HhVacancyBrief>();
        foreach (var v in result["vacancies"]?.AsArray() ?? [])
        {
            if (v is null) continue;
            var id = Long(v["vacancyId"]);
            if (id is null) continue;
            var company = v["company"];
            items.Add(new HhVacancyBrief(
                id.Value,
                host,
                Str(v["name"]) ?? "",
                Long(company?["id"]),
                Str(company?["visibleName"]) ?? Str(company?["name"]),
                Str(v["area"]?["name"]),
                CountryFromArea(v["area"]),
                Formats(v),
                Bool(v["@responseLetterRequired"]),
                Bool(v["userTestPresent"]),
                Bool(v["closedForApplicants"]),
                Date(v["publicationTime"]?["$"]),
                VacancyUrl(host, id.Value),
                // A plain ISO string in search rows; tolerate the {"$": …} wrapper publicationTime uses.
                Date(v["creationTime"] is JsonObject created ? created["$"] : v["creationTime"]),
                (int?)Long(v["totalResponsesCount"])));
        }

        return (items, (int)(Long(result["totalResults"]) ?? items.Count));
    }

    public static HhVacancyDetail ParseVacancy(JsonNode state, string host)
    {
        var v = state["vacancyView"]?["vacancyFull"]?["vacancy"] ?? throw new FormatException("vacancyView.vacancyFull.vacancy missing");
        var id = Long(v["vacancyId"]) ?? throw new FormatException("vacancyId missing");
        var comp = v["compensation"];
        decimal? from = null, to = null;
        string? currency = null;
        bool? gross = null;
        var mode = Str(comp?["mode"]);
        if (comp is not null && !Bool(comp["noCompensation"]) && (mode is null or "MONTH"))
        {
            from = Dec(comp["from"]);
            to = Dec(comp["to"]);
            currency = Currency.Normalize(Str(comp["currencyCode"]));
            gross = comp["gross"] is null ? null : Bool(comp["gross"]);
        }

        var status = v["status"];
        return new HhVacancyDetail(
            id,
            host,
            Str(v["name"]) ?? "",
            Long(v["company"]?["id"]),
            Str(v["company"]?["visibleName"]) ?? Str(v["company"]?["name"]),
            Str(v["area"]?["name"]),
            Str(v["area"]?["countryIsoCode"]) ?? CountryFromArea(v["area"]),
            Formats(v),
            from,
            to,
            currency,
            gross,
            English(v["languages"]),
            Str(v["workExperience"]),
            (v["keySkills"]?.AsArray() ?? []).Select(Str).OfType<string>().ToList(),
            TextUtil.HtmlToText(Str(v["description"])),
            Bool(v["closedForApplicants"]) || Bool(status?["archived"]) || Bool(status?["disabled"]),
            Date(v["publicationTimeIso"]),
            VacancyUrl(host, id),
            v.ToJsonString());
    }

    public static HhEmployer ParseEmployer(JsonNode state)
    {
        var info = state["employerInfo"] ?? throw new FormatException("employerInfo missing");
        var id = Long(info["id"]) ?? throw new FormatException("employer id missing");
        var industries = (info["industries"]?.AsArray() ?? [])
            .Select(i => Str(i?["trl"]) ?? Str(i?["name"]))
            .OfType<string>()
            .ToList();
        return new HhEmployer(
            id,
            Str(info["name"]) ?? "",
            Str(info["site"]?["href"]) ?? Str(info["site"]),
            Str(info["address"]),
            Str(info["sizeCategory"]),
            industries,
            TextUtil.HtmlToText(Str(info["description"])),
            Dec(state["employerOrganizationSchema"]?["aggregateRating"]?["ratingValue"]),
            Bool(info["accreditedITEmployer"]),
            (int?)Long(state["activeEmployerVacancyCount"]));
    }

    public static string VacancyUrl(string host, long id) => $"https://{host}/vacancy/{id}";

    /// <summary>hh area path is ".country.region.city." — the first segment is the country area id.</summary>
    internal static string? CountryFromArea(JsonNode? area)
    {
        var iso = Str(area?["countryIsoCode"]);
        if (iso is not null) return iso;
        var path = Str(area?["path"]);
        if (path is null) return null;
        var first = path.Split('.', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        return first switch
        {
            "113" => "RU",
            "16" => "BY",
            "40" => "KZ",
            "5" => "UA",
            "28" => "GE",
            "48" => "KG",
            "97" => "UZ",
            "9" => "AZ",
            "1001" => null, // "other countries" bucket
            _ => null,
        };
    }

    /// <summary>ON_SITE / HYBRID / REMOTE wherever they sit (flat list, workFormatsElement wrappers, legacy @workSchedule).</summary>
    internal static WorkFormat Formats(JsonNode vacancy)
    {
        var result = WorkFormat.None;
        foreach (var s in Strings(vacancy["workFormats"]))
        {
            result |= s switch
            {
                "ON_SITE" or "FIELD_WORK" => WorkFormat.Office,
                "HYBRID" => WorkFormat.Hybrid,
                "REMOTE" => WorkFormat.Remote,
                _ => WorkFormat.None,
            };
        }

        if (result == WorkFormat.None && Str(vacancy["@workSchedule"]) == "remote") result = WorkFormat.Remote;
        return result;
    }

    /// <summary>The English requirement: any object with isoCode "eng" and a degree, at any depth of the languages node.</summary>
    internal static EnglishLevel English(JsonNode? languages)
    {
        var best = EnglishLevel.Unknown;
        Walk(languages);
        return best;

        void Walk(JsonNode? node)
        {
            switch (node)
            {
                case JsonArray array:
                    foreach (var item in array) Walk(item);
                    break;
                case JsonObject obj:
                    if (Str(obj["isoCode"]) == "eng")
                    {
                        var level = EnglishLevels.Parse(Str(obj["degree"]));
                        if (level > best) best = level;
                    }

                    foreach (var (_, child) in obj) Walk(child);
                    break;
            }
        }
    }

    private static IEnumerable<string> Strings(JsonNode? node)
    {
        switch (node)
        {
            case JsonArray array:
                foreach (var item in array)
                foreach (var s in Strings(item))
                    yield return s;
                break;
            case JsonObject obj:
                foreach (var (_, child) in obj)
                foreach (var s in Strings(child))
                    yield return s;
                break;
            case JsonValue value when value.GetValueKind() == JsonValueKind.String:
                yield return value.GetValue<string>();
                break;
        }
    }

    internal static string? Str(JsonNode? node) => node is JsonValue v && v.GetValueKind() is JsonValueKind.String or JsonValueKind.Number
        ? v.ToString()
        : null;

    internal static long? Long(JsonNode? node)
    {
        if (node is not JsonValue v) return null;
        return v.GetValueKind() switch
        {
            JsonValueKind.Number => (long)v.GetValue<double>(),
            JsonValueKind.String when long.TryParse(v.GetValue<string>(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var l) => l,
            _ => null,
        };
    }

    internal static decimal? Dec(JsonNode? node)
    {
        if (node is not JsonValue v) return null;
        return v.GetValueKind() switch
        {
            JsonValueKind.Number => (decimal)v.GetValue<double>(),
            JsonValueKind.String when decimal.TryParse(v.GetValue<string>(), NumberStyles.Number, CultureInfo.InvariantCulture, out var d) => d,
            _ => null,
        };
    }

    internal static bool Bool(JsonNode? node) =>
        node is JsonValue v && (v.GetValueKind() == JsonValueKind.True
                                || v.GetValueKind() == JsonValueKind.String && v.GetValue<string>() is "true" or "True");

    private static DateTimeOffset? Date(JsonNode? node) =>
        Str(node) is { } s && DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : null;
}
