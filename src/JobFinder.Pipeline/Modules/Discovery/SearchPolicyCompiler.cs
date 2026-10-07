using System.Globalization;
using System.Text.Json;
using JobFinder.Core.Domain;
using JobFinder.Core.Filtering;
using JobFinder.Core.Options;
using JobFinder.Core.Policies;
using JobFinder.Integrations.Habr;
using JobFinder.Integrations.HeadHunter;
using JobFinder.Integrations.Threads;
using JobFinder.Pipeline.Jobs;

namespace JobFinder.Pipeline.Modules.Discovery;

public sealed record SearchPolicySnapshot(SearchPolicy Value, long Version)
{
    public HardFilter Filter() => new(new FilterOptions
    {
        TitleStopWords = Value.Filter.TitleStopWords, RequiredMarkers = Value.Filter.RequiredMarkers,
        OfficeCities = Value.Filter.OfficeCities, RemoteCountries = Value.Filter.RemoteCountries,
        MaxEnglish = Value.Filter.MaxEnglish, CompanyBlacklist = Value.Filter.CompanyBlacklist,
    });

    public PostPrefilter Prefilter() => new(new PrefilterOptions
    {
        TechMarkers = Value.Prefilter.TechMarkers, VacancyMarkers = Value.Prefilter.VacancyMarkers,
        RejectMarkers = Value.Prefilter.RejectMarkers, MinLength = Value.Prefilter.MinLength,
    });
}

public static class SearchPolicyCompiler
{
    public static SearchPolicy Import(FilterOptions filter, PrefilterOptions prefilter, HhOptions hh, HabrOptions habr, ThreadsOptions threads)
    {
        var policy = Clone(new SearchPolicy
        {
            Filter = new()
            {
                TitleStopWords = filter.TitleStopWords, RequiredMarkers = filter.RequiredMarkers,
                OfficeCities = filter.OfficeCities, RemoteCountries = filter.RemoteCountries,
                MaxEnglish = filter.MaxEnglish, CompanyBlacklist = filter.CompanyBlacklist,
            },
            Prefilter = new()
            {
                TechMarkers = prefilter.TechMarkers, VacancyMarkers = prefilter.VacancyMarkers,
                RejectMarkers = prefilter.RejectMarkers, MinLength = prefilter.MinLength,
            },
            HeadHunter = new()
            {
                Enabled = hh.Enabled, Searches = hh.Searches.Select(Import).ToList(),
                IntervalMinutes = hh.IntervalMinutes, JitterMinutes = hh.JitterMinutes, QuietHours = hh.QuietHours,
            },
            Habr = new()
            {
                Enabled = habr.Enabled, Queries = habr.Queries, Filters = habr.Filters, MaxPages = habr.MaxPages,
                IntervalMinutes = habr.IntervalMinutes, JitterMinutes = habr.JitterMinutes,
            },
            Companies = new()
            {
                Enabled = hh.Companies.Enabled, RunAt = hh.Companies.RunAt, RefreshDays = hh.Companies.RefreshDays,
                Searches = hh.Companies.Searches.Select(Import).ToList(), Tags = hh.Companies.Tags,
            },
            Threads = new()
            {
                Enabled = threads.Enabled, VacancyQueries = threads.VacancyQueries, AudienceQueries = threads.AudienceQueries,
                IntervalMinutes = threads.IntervalMinutes, JitterMinutes = threads.JitterMinutes,
                ResultsPerQuery = threads.ResultsPerQuery, MaxPostAgeHours = threads.MaxPostAgeHours,
            },
        });
        Validate(policy);
        return policy;
    }

    public static SearchPolicy Clone(SearchPolicy value) => JsonSerializer.Deserialize(
        JsonSerializer.Serialize(value, SearchPolicyJsonContext.Default.SearchPolicy), SearchPolicyJsonContext.Default.SearchPolicy)
        ?? throw new JsonException("search policy is null");

    public static void Validate(SearchPolicy value)
    {
        Require(value.SchemaVersion == 1, "unknown search policy schema version");
        Require(value.Filter is not null && value.Prefilter is not null && value.HeadHunter is not null
            && value.Habr is not null && value.Companies is not null && value.Threads is not null, "search policy sections are required");
        var filter = value.Filter!;
        Words(filter.TitleStopWords, "filter.titleStopWords");
        Words(filter.RequiredMarkers, "filter.requiredMarkers");
        Words(filter.OfficeCities, "filter.officeCities");
        Words(filter.RemoteCountries, "filter.remoteCountries");
        Require(filter.RemoteCountries.All(country => country.Length == 2 && country.All(char.IsAsciiLetter)), "remote countries must be ISO codes");
        Require(Enum.IsDefined(filter.MaxEnglish), "unknown maximum English level");
        Words(filter.CompanyBlacklist, "filter.companyBlacklist");
        var prefilter = value.Prefilter!;
        Words(prefilter.TechMarkers, "prefilter.techMarkers");
        Words(prefilter.VacancyMarkers, "prefilter.vacancyMarkers");
        Words(prefilter.RejectMarkers, "prefilter.rejectMarkers");
        Require(prefilter.MinLength >= 0, "prefilter minimum length cannot be negative");
        var hh = value.HeadHunter!;
        Interval(hh.IntervalMinutes, hh.JitterMinutes, "headHunter");
        Require(hh.QuietHours is not null && (hh.QuietHours.Length == 0 || QuietHours.IsValid(hh.QuietHours)), "headHunter.quietHours must be empty or a valid time window");
        Searches(hh.Searches, "headHunter.searches");
        var habr = value.Habr!;
        Interval(habr.IntervalMinutes, habr.JitterMinutes, "habr");
        Words(habr.Queries, "habr.queries");
        Require(habr.Filters is not null && habr.Filters.All(item => item is not null), "habr.filters cannot contain null");
        foreach (var query in habr.Filters!)
        {
            var parameters = System.Web.HttpUtility.ParseQueryString(query.TrimStart('&'));
            Require(parameters.AllKeys.All(key => key is not null && key.ToLowerInvariant() is not ("q" or "page" or "type" or "sort")), "Habr filters cannot replace owned search parameters");
        }
        Require(habr.MaxPages > 0, "habr.maxPages must be positive");
        var companies = value.Companies!;
        Require(TimeOnly.TryParseExact(companies.RunAt, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out _), "companies.runAt must be HH:mm");
        Require(companies.RefreshDays > 0, "companies.refreshDays must be positive");
        Searches(companies.Searches, "companies.searches");
        Require(companies.Tags is not null, "companies.tags is required");
        Require(companies.Tags!.Keys.Distinct(StringComparer.OrdinalIgnoreCase).Count() == companies.Tags.Count, "company tag names must be unique");
        foreach (var (tag, words) in companies.Tags)
        {
            Require(!string.IsNullOrWhiteSpace(tag), "company tag name is required");
            Words(words, "companies.tags." + tag);
        }
        var threads = value.Threads!;
        Interval(threads.IntervalMinutes, threads.JitterMinutes, "threads");
        Words(threads.VacancyQueries, "threads.vacancyQueries");
        Words(threads.AudienceQueries, "threads.audienceQueries");
        Require(threads.ResultsPerQuery > 0 && threads.MaxPostAgeHours > 0, "Threads search limits must be positive");
    }

    public static HhSearch ToRequest(HeadHunterQuery query) => new()
    {
        Name = query.Name, Host = query.Host, Text = query.Text, SearchField = query.SearchField,
        Areas = [.. query.Areas], WorkFormats = [.. query.WorkFormats], PeriodDays = query.PeriodDays, MaxPages = query.MaxPages,
    };

    private static HeadHunterQuery Import(HhSearch query) => new()
    {
        Name = query.Name, Host = query.Host, Text = query.Text, SearchField = query.SearchField,
        Areas = query.Areas, WorkFormats = query.WorkFormats, PeriodDays = query.PeriodDays, MaxPages = query.MaxPages,
    };

    private static void Searches(List<HeadHunterQuery> searches, string path)
    {
        Require(searches is not null && searches.All(search => search is not null), path + " cannot be null or contain null");
        foreach (var search in searches!)
        {
            Require(!string.IsNullOrWhiteSpace(search.Text), path + ": query text is required");
            Require(search.Name is not null && search.SearchField is not null, path + ": name/searchField cannot be null");
            Require(search.Host is "hh.ru" or "rabota.by" || search.Host is not null && search.Host.EndsWith(".hh.ru", StringComparison.Ordinal), path + ": unsupported host");
            Require(search.Host is not null && Uri.TryCreate("https://" + search.Host, UriKind.Absolute, out var uri)
                && uri.Host == search.Host && uri.AbsolutePath == "/" && uri.Port == 443 && uri.UserInfo.Length == 0, path + ": host must not contain a URL or port");
            Require(search.Areas is not null && search.Areas.All(area => area > 0), path + ": area IDs must be positive");
            Words(search.WorkFormats, path + ".workFormats");
            Require(search.WorkFormats.All(format => format is "REMOTE" or "HYBRID" or "ON_SITE"), path + ": unknown work format");
            Require(search.PeriodDays is 1 or 3 or 7 or 30 && search.MaxPages > 0, path + ": invalid period or page limit");
        }
    }

    private static void Interval(int minutes, int jitter, string path) =>
        Require(minutes > 0 && jitter >= 0 && jitter <= int.MaxValue / 60, path + ": interval must be positive and jitter nonnegative");

    private static void Words(List<string> words, string path) =>
        Require(words is not null && words.All(word => !string.IsNullOrWhiteSpace(word)), path + " cannot be null or contain empty entries");

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new ArgumentException(message, "policy");
    }
}
