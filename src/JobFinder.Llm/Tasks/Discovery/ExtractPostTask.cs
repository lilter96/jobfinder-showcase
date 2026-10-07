using System.Text.Json.Serialization;
using JobFinder.Core.Domain;
using JobFinder.Llm.Client;
using JobFinder.Llm.Contracts;

namespace JobFinder.Llm.Tasks.Discovery;

public sealed class StrictStringEnumConverter<TEnum>() : JsonStringEnumConverter<TEnum>(allowIntegerValues: false) where TEnum : struct, Enum;

[JsonConverter(typeof(StrictStringEnumConverter<WireEnglish>))]
public enum WireEnglish
{
    [JsonStringEnumMemberName("unknown")] Unknown,
    [JsonStringEnumMemberName("none")] None,
    [JsonStringEnumMemberName("a1")] A1,
    [JsonStringEnumMemberName("a2")] A2,
    [JsonStringEnumMemberName("b1")] B1,
    [JsonStringEnumMemberName("b2")] B2,
    [JsonStringEnumMemberName("c1")] C1,
    [JsonStringEnumMemberName("c2")] C2,
    [JsonStringEnumMemberName("native")] Native,
}

[JsonConverter(typeof(StrictStringEnumConverter<WireSeniority>))]
public enum WireSeniority
{
    [JsonStringEnumMemberName("unknown")] Unknown,
    [JsonStringEnumMemberName("junior")] Junior,
    [JsonStringEnumMemberName("middle")] Middle,
    [JsonStringEnumMemberName("senior")] Senior,
    [JsonStringEnumMemberName("lead")] Lead,
    [JsonStringEnumMemberName("architect")] Architect,
}

[JsonConverter(typeof(StrictStringEnumConverter<WireWorkFormat>))]
public enum WireWorkFormat
{
    [JsonStringEnumMemberName("office")] Office,
    [JsonStringEnumMemberName("hybrid")] Hybrid,
    [JsonStringEnumMemberName("remote")] Remote,
}

public sealed record PostExtractionWire : IValidatable
{
    [JsonRequired] public bool IsVacancy { get; set; }

    [JsonRequired] public bool IsDotnet { get; set; }

    [JsonRequired] public string? Title { get; set; }

    [JsonRequired] public string? Company { get; set; }

    [JsonRequired] public string? City { get; set; }

    [JsonRequired] public string? Country { get; set; }

    [JsonRequired] public List<WireWorkFormat> WorkFormats { get; set; } = [];

    [JsonRequired] public decimal? SalaryFrom { get; set; }

    [JsonRequired] public decimal? SalaryTo { get; set; }

    [JsonRequired] public string? Currency { get; set; }

    [JsonRequired] public bool? SalaryGross { get; set; }

    [JsonRequired] public WireEnglish EnglishLevel { get; set; }

    [JsonRequired] public WireSeniority Seniority { get; set; }

    [JsonRequired] public List<string> Skills { get; set; } = [];

    [JsonRequired] public List<string> Contacts { get; set; } = [];

    [JsonRequired] public string? ApplyUrl { get; set; }

    public string? Validate() => IsVacancy && string.IsNullOrWhiteSpace(Title) ? "vacancy without a title" : null;

    public PostExtraction ToDomain() => new()
    {
        IsVacancy = IsVacancy, IsDotnet = IsDotnet, Title = Title, Company = Company, City = City, Country = Country,
        WorkFormats = WorkFormats.Select(format => format.ToString().ToLowerInvariant()).ToList(), SalaryFrom = SalaryFrom, SalaryTo = SalaryTo,
        Currency = Currency, SalaryGross = SalaryGross, EnglishLevel = EnglishLevel.ToString().ToLowerInvariant(), Seniority = Seniority.ToString().ToLowerInvariant(),
        Skills = Skills, Contacts = Contacts, ApplyUrl = ApplyUrl,
    };
}

public sealed record ExtractPostInput(string Text, string SourceTitle, string Today, IReadOnlyList<PostLink>? Links, IReadOnlyList<LinkedPage>? Pages);

public static class ExtractPostTask
{
    public static LlmTaskDefinition<ExtractPostInput, PostExtractionWire> Definition { get; } = new(
        "post_extraction", "extract_post", "1", DiscoveryWireJsonContext.Default.PostExtractionWire, [LlmPurpose.Extract],
        input => new LlmTaskPayload(LlmPurpose.Extract, new Dictionary<string, string> { ["today"] = input.Today },
            JobFinderLlm.PostInput(input.Text, input.SourceTitle, input.Links, input.Pages)), probeInput: TaskProbeInputs.ExtractPost);
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    RespectNullableAnnotations = true)]
[JsonSerializable(typeof(PostExtractionWire))]
internal sealed partial class DiscoveryWireJsonContext : JsonSerializerContext;
