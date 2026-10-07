using System.Text.Json.Serialization;
using JobFinder.Core.Profile;
using JobFinder.Llm.Client;

namespace JobFinder.Llm.Tasks;

/// <summary>What the model reads out of a Telegram/Threads post.</summary>
/// <summary>A page a post links to, as the extraction model sees it.</summary>
public sealed record LinkedPage(string Url, string? Title, string Text);

public sealed record PostExtraction : IValidatable
{
    public bool IsVacancy { get; set; }

    public bool IsDotnet { get; set; }

    public string? Title { get; set; }

    public string? Company { get; set; }

    public string? City { get; set; }

    /// <summary>ISO-2 of the employer's country (BY, RU, …) or null.</summary>
    public string? Country { get; set; }

    public List<string> WorkFormats { get; set; } = [];

    public decimal? SalaryFrom { get; set; }

    public decimal? SalaryTo { get; set; }

    public string? Currency { get; set; }

    public bool? SalaryGross { get; set; }

    public string EnglishLevel { get; set; } = "unknown";

    public string Seniority { get; set; } = "unknown";

    public List<string> Skills { get; set; } = [];

    public List<string> Contacts { get; set; } = [];

    public string? ApplyUrl { get; set; }

    public string? Validate() => IsVacancy && string.IsNullOrWhiteSpace(Title) ? "vacancy without a title" : null;
}

public sealed record VacancyScoring : IValidatable
{
    public int Score { get; set; }

    public int StackMatch { get; set; }

    public int SeniorityMatch { get; set; }

    public int DomainMatch { get; set; }

    public int FormatMatch { get; set; }

    public string EnglishRequired { get; set; } = "unknown";

    public List<string> Strengths { get; set; } = [];

    public List<string> Gaps { get; set; } = [];

    public List<string> RedFlags { get; set; } = [];

    public string Summary { get; set; } = "";

    /// <summary>Hard skills and terms of the vacancy (ATS keywords), in its own spelling.</summary>
    public List<string> Keywords { get; set; } = [];

    /// <summary>"ru" or "en".</summary>
    public string Language { get; set; } = "ru";

    /// <summary>ok / suspicious / scam.</summary>
    public string Legitimacy { get; set; } = "ok";

    public List<string> LegitimacyReasons { get; set; } = [];

    public string? Validate()
    {
        int[] all = [Score, StackMatch, SeniorityMatch, DomainMatch, FormatMatch];
        if (all.Any(s => s is < 0 or > 100)) return "a score outside 0..100";
        return string.IsNullOrWhiteSpace(Summary) ? "empty summary" : null;
    }
}

public sealed record CommentPlan : IValidatable
{
    /// <summary>recruiter / hiring_manager / tech / hiring / other.</summary>
    public string Audience { get; set; } = "other";

    public bool WorthCommenting { get; set; }

    public string Reason { get; set; } = "";

    public List<string> Variants { get; set; } = [];

    public string? Validate() =>
        WorthCommenting && Variants.Count(v => !string.IsNullOrWhiteSpace(v)) < 2 ? "fewer than 2 comment variants" : null;
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true,
    NumberHandling = JsonNumberHandling.AllowReadingFromString, AllowTrailingCommas = true,
    ReadCommentHandling = System.Text.Json.JsonCommentHandling.Skip)]
[JsonSerializable(typeof(PostExtraction))]
[JsonSerializable(typeof(VacancyScoring))]
[JsonSerializable(typeof(CommentPlan))]
[JsonSerializable(typeof(CandidateProfile))]
[JsonSerializable(typeof(JobFinder.Core.Resume.ResumeDocument))]
[JsonSerializable(typeof(ResumeDraft))]
[JsonSerializable(typeof(JobFinder.Core.Resume.ResumeReview))]
internal sealed partial class LlmJsonContext : JsonSerializerContext;

/// <summary>A tailored resume as the model answers: the plan (each requirement of the vacancy and its evidence) and the document written by it.</summary>
public sealed record ResumeDraft : IValidatable
{
    public List<JobFinder.Core.Resume.RequirementMatch> Requirements { get; set; } = [];

    public JobFinder.Core.Resume.ResumeDocument Document { get; set; } = new();

    public string? Validate() =>
        Document.Experience.Count == 0 ? "no experience"
        : string.IsNullOrWhiteSpace(Document.Summary) ? "no summary"
        : Requirements.Count == 0 ? "no requirements analysis"
        : null;
}

/// <summary>
/// Strict JSON schemas (every property required, nullable = a type union, no additional properties): the shape OpenAI-compatible strict
/// mode accepts. Hand-written so the contract a prompt promises is reviewable in one place.
/// </summary>
internal static class Schemas
{
    public static JsonSchemaSpec PostExtraction => Discovery.ExtractPostTask.Definition.Schema;

    public static JsonSchemaSpec VacancyScoring => Contracts.NativeWireSchemas.VacancyScoring;

    public static JsonSchemaSpec CommentPlan => Contracts.NativeWireSchemas.CommentPlan;

    public static JsonSchemaSpec Resume => Contracts.NativeWireSchemas.ResumeDraft;

    public static JsonSchemaSpec ResumeReview => Contracts.NativeWireSchemas.ResumeReview;

    public static JsonSchemaSpec Profile => Contracts.NativeWireSchemas.CandidateProfile;
}
