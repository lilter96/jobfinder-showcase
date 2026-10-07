using System.Text.Json;
using System.Text.Json.Nodes;
using JobFinder.Core.Domain;
using JobFinder.Core.Filtering;
using JobFinder.Core.Options;
using JobFinder.Core.Policies;
using JobFinder.Integrations.Habr;
using JobFinder.Integrations.HeadHunter;
using JobFinder.Integrations.Threads;
using JobFinder.Pipeline.Modules.Discovery;
using Microsoft.Extensions.Configuration;

namespace JobFinder.Tests.Pipeline;

public sealed class SearchPolicyTests
{
    internal static SearchPolicy Bootstrap() => SearchPolicyCompiler.Import(new FilterOptions
    {
        RequiredMarkers = [".NET"], TitleStopWords = ["junior"], OfficeCities = ["Минск"], RemoteCountries = ["BY", "RU"],
    }, new PrefilterOptions { TechMarkers = [".NET"], VacancyMarkers = ["ваканс"], RejectMarkers = ["#резюме"] },
        new HhOptions { Searches = [new HhSearch { Name = "effective", Text = ".NET", Areas = [1002] }] },
        new HabrOptions { Queries = [".NET"], Filters = [""] }, new ThreadsOptions { VacancyQueries = [".NET vacancy"] });

    [Fact]
    public void Effective_public_configuration_import_preserves_every_search_and_filter()
    {
        var root = Path.GetFullPath(Path.Combine(Fixture.PromptsDir, ".."));
        var config = new ConfigurationBuilder().AddJsonFile(Path.Combine(root, "config", "appsettings.json")).Build();
        var filter = config.GetSection(FilterOptions.Section).Get<FilterOptions>()!;
        var prefilter = config.GetSection(PrefilterOptions.Section).Get<PrefilterOptions>()!;
        var hh = config.GetSection(HhOptions.Section).Get<HhOptions>()!;
        var habr = config.GetSection(HabrOptions.Section).Get<HabrOptions>()!;
        var threads = config.GetSection(ThreadsOptions.Section).Get<ThreadsOptions>()!;
        var imported = SearchPolicyCompiler.Import(filter, prefilter, hh, habr, threads);
        Assert.Equal(hh.Searches.Select(search => search.Text), imported.HeadHunter.Searches.Select(search => search.Text));
        Assert.Equal(hh.Companies.Searches.Select(search => search.MaxPages), imported.Companies.Searches.Select(search => search.MaxPages));
        Assert.Equal(filter.CompanyBlacklist, imported.Filter.CompanyBlacklist);
        Assert.Equal(prefilter.TechMarkers, imported.Prefilter.TechMarkers);
        Assert.Equal(hh.QuietHours, imported.HeadHunter.QuietHours);
        Assert.Equal(threads.ResultsPerQuery, imported.Threads.ResultsPerQuery);
        Assert.Equal(threads.AudienceQueries, imported.Threads.AudienceQueries);
        Assert.Equal(habr.Filters, imported.Habr.Filters);
        Assert.Equal(hh.Companies.Tags["финтех"], imported.Companies.Tags["финтех"]);
        hh.Searches[0].Areas.Clear();
        threads.AudienceQueries.Clear();
        Assert.NotEmpty(imported.HeadHunter.Searches[0].Areas);
        Assert.NotEmpty(imported.Threads.AudienceQueries);
    }

    [Fact]
    public void Compiler_owns_request_parameters_and_does_not_copy_deployment_credentials()
    {
        var threads = new ThreadsOptions { AccessToken = "synthetic-token-not-for-policy", Mode = "Api" };
        var policy = SearchPolicyCompiler.Import(new FilterOptions(), new PrefilterOptions(), new HhOptions { ResumeTitle = "private-selector" }, new HabrOptions(), threads);
        var json = JsonSerializer.Serialize(policy, SearchPolicyJsonContext.Default.SearchPolicy);
        Assert.DoesNotContain("synthetic-token-not-for-policy", json);
        Assert.DoesNotContain("private-selector", json);
        var request = SearchPolicyCompiler.ToRequest(Bootstrap().HeadHunter.Searches[0]);
        Assert.Equal([1002], request.Areas);
        Assert.Equal(".NET", request.Text);
    }

    [Theory]
    [InlineData("schemaVersion", "2")]
    [InlineData("headHunter.intervalMinutes", "0")]
    [InlineData("headHunter.jitterMinutes", "-1")]
    [InlineData("headHunter.jitterMinutes", "2147483647")]
    [InlineData("headHunter.quietHours", "\"oops\"")]
    [InlineData("habr.maxPages", "0")]
    [InlineData("habr.queries", "[null]")]
    [InlineData("habr.filters", "[\"p%61ge=99\"]")]
    [InlineData("companies.runAt", "\"25:30\"")]
    [InlineData("companies.refreshDays", "0")]
    [InlineData("threads.resultsPerQuery", "0")]
    [InlineData("filter.remoteCountries", "[\"not-ISO\"]")]
    public void Invalid_policies_are_rejected_before_execution(string path, string value)
    {
        var json = JsonNode.Parse(JsonSerializer.Serialize(Bootstrap(), SearchPolicyJsonContext.Default.SearchPolicy))!;
        var parts = path.Split('.');
        var parent = parts.Length == 1 ? json : json[parts[0]]!;
        parent[parts[^1]] = JsonNode.Parse(value);
        var policy = JsonSerializer.Deserialize(json.ToJsonString(), SearchPolicyJsonContext.Default.SearchPolicy)!;
        Assert.Throws<ArgumentException>(() => SearchPolicyCompiler.Validate(policy));
    }

    [Theory]
    [InlineData("localhost")]
    [InlineData("hh.ru/other")]
    [InlineData("https://hh.ru")]
    [InlineData("other.example")]
    [InlineData("hh.ru:8080")]
    [InlineData("user@evil.hh.ru")]
    public void HeadHunter_query_hosts_cannot_escape_the_source_family(string host)
    {
        var policy = Bootstrap();
        policy.HeadHunter.Searches[0].Host = host;
        Assert.Throws<ArgumentException>(() => SearchPolicyCompiler.Validate(policy));
    }

    [Theory]
    [InlineData("filter")]
    [InlineData("prefilter")]
    [InlineData("headHunter")]
    public void Required_persisted_sections_do_not_silently_revert_to_permissive_defaults(string key)
    {
        var json = JsonNode.Parse(JsonSerializer.Serialize(Bootstrap(), SearchPolicyJsonContext.Default.SearchPolicy))!.AsObject();
        json.Remove(key);
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize(json.ToJsonString(), SearchPolicyJsonContext.Default.SearchPolicy));
        json[key] = null;
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize(json.ToJsonString(), SearchPolicyJsonContext.Default.SearchPolicy));
    }

    [Fact]
    public void Future_persisted_fields_remain_tolerant_but_known_missing_filters_fail_closed()
    {
        var json = JsonNode.Parse(JsonSerializer.Serialize(Bootstrap(), SearchPolicyJsonContext.Default.SearchPolicy))!.AsObject();
        json["future"] = 1;
        Assert.NotNull(JsonSerializer.Deserialize(json.ToJsonString(), SearchPolicyJsonContext.Default.SearchPolicy));
        json["filter"]!.AsObject().Remove("requiredMarkers");
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize(json.ToJsonString(), SearchPolicyJsonContext.Default.SearchPolicy));
    }

    [Fact]
    public void A_snapshot_builds_consistent_filter_and_prefilter_rules()
    {
        var snapshot = new SearchPolicySnapshot(Bootstrap(), 12);
        var vacancy = new Vacancy { Title = "Senior .NET developer", Description = "вакансия .NET", Fingerprint = "f", Url = "https://example.com/vacancy" };
        Assert.True(snapshot.Filter().Check(vacancy, false).Passed);
        Assert.True(snapshot.Prefilter().HasTechMarker(vacancy.Title));
        vacancy.Title = "Senior Java developer";
        vacancy.Description = "Java";
        Assert.False(snapshot.Filter().Check(vacancy, false).Passed);
        Assert.False(snapshot.Prefilter().HasTechMarker(vacancy.Title));
    }

    [Fact]
    public void Positive_additive_jitter_preserves_the_existing_scheduler_semantics()
    {
        var policy = Bootstrap();
        policy.HeadHunter.JitterMinutes = 90;
        SearchPolicyCompiler.Validate(policy);
    }
}
