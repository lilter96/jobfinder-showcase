using System.Text.Json.Serialization;
using JobFinder.Core.Domain;

namespace JobFinder.Core.Policies;

public sealed record SearchPolicy
{
    [JsonRequired] public int SchemaVersion { get; set; } = 1;
    [JsonRequired] public VacancyFilterPolicy Filter { get; set; } = new();
    [JsonRequired] public PostFilterPolicy Prefilter { get; set; } = new();
    [JsonRequired] public HeadHunterSearchPolicy HeadHunter { get; set; } = new();
    [JsonRequired] public HabrSearchPolicy Habr { get; set; } = new();
    [JsonRequired] public CompanySearchPolicy Companies { get; set; } = new();
    [JsonRequired] public ThreadsSearchPolicy Threads { get; set; } = new();
}

public sealed record VacancyFilterPolicy
{
    [JsonRequired] public List<string> TitleStopWords { get; set; } = [];
    [JsonRequired] public List<string> RequiredMarkers { get; set; } = [];
    [JsonRequired] public List<string> OfficeCities { get; set; } = [];
    [JsonRequired] public List<string> RemoteCountries { get; set; } = [];
    [JsonRequired, JsonConverter(typeof(JsonStringEnumConverter<EnglishLevel>))]
    public EnglishLevel MaxEnglish { get; set; } = EnglishLevel.B1;
    [JsonRequired] public List<string> CompanyBlacklist { get; set; } = [];
}

public sealed record PostFilterPolicy
{
    [JsonRequired] public List<string> TechMarkers { get; set; } = [];
    [JsonRequired] public List<string> VacancyMarkers { get; set; } = [];
    [JsonRequired] public List<string> RejectMarkers { get; set; } = [];
    [JsonRequired] public int MinLength { get; set; } = 80;
}

public sealed record HeadHunterQuery
{
    [JsonRequired] public string Name { get; set; } = "";
    [JsonRequired] public string Host { get; set; } = "hh.ru";
    [JsonRequired] public string Text { get; set; } = "";
    [JsonRequired] public string SearchField { get; set; } = "name";
    [JsonRequired] public List<int> Areas { get; set; } = [];
    [JsonRequired] public List<string> WorkFormats { get; set; } = [];
    [JsonRequired] public int PeriodDays { get; set; } = 3;
    [JsonRequired] public int MaxPages { get; set; } = 3;
}

public sealed record HeadHunterSearchPolicy
{
    [JsonRequired] public bool Enabled { get; set; } = true;
    [JsonRequired] public List<HeadHunterQuery> Searches { get; set; } = [];
    [JsonRequired] public int IntervalMinutes { get; set; } = 45;
    [JsonRequired] public int JitterMinutes { get; set; } = 15;
    [JsonRequired] public string QuietHours { get; set; } = "00:30-07:00";
}

public sealed record HabrSearchPolicy
{
    [JsonRequired] public bool Enabled { get; set; } = true;
    [JsonRequired] public List<string> Queries { get; set; } = [];
    [JsonRequired] public List<string> Filters { get; set; } = [];
    [JsonRequired] public int MaxPages { get; set; } = 2;
    [JsonRequired] public int IntervalMinutes { get; set; } = 30;
    [JsonRequired] public int JitterMinutes { get; set; } = 10;
}

public sealed record CompanySearchPolicy
{
    [JsonRequired] public bool Enabled { get; set; } = true;
    [JsonRequired] public string RunAt { get; set; } = "03:30";
    [JsonRequired] public List<HeadHunterQuery> Searches { get; set; } = [];
    [JsonRequired] public int RefreshDays { get; set; } = 14;
    [JsonRequired] public Dictionary<string, List<string>> Tags { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed record ThreadsSearchPolicy
{
    [JsonRequired] public bool Enabled { get; set; } = true;
    [JsonRequired] public List<string> VacancyQueries { get; set; } = [];
    [JsonRequired] public List<string> AudienceQueries { get; set; } = [];
    [JsonRequired] public int IntervalMinutes { get; set; } = 60;
    [JsonRequired] public int JitterMinutes { get; set; } = 20;
    [JsonRequired] public int ResultsPerQuery { get; set; } = 25;
    [JsonRequired] public int MaxPostAgeHours { get; set; } = 48;
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, RespectNullableAnnotations = true)]
[JsonSerializable(typeof(SearchPolicy))]
public sealed partial class SearchPolicyJsonContext : JsonSerializerContext;
