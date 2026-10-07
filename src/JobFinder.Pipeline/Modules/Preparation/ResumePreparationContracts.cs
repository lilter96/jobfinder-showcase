using System.Text.Json;
using System.Text.Json.Serialization;
using JobFinder.Core.Artifacts;
using JobFinder.Core.Domain;
using JobFinder.Core.Profile;
using JobFinder.Core.Resume;
using JobFinder.Llm.Configuration;
using JobFinder.Pipeline.Documents;
using Wolverine.Attributes;

namespace JobFinder.Pipeline.Modules.Preparation;

public sealed record ResumeVacancyInput
{
    public long Id { get; set; }
    public string Title { get; set; } = "";
    public string? CompanyName { get; set; }
    public List<string> Industries { get; set; } = [];
    public string Description { get; set; } = "";
    public decimal? SalaryFrom { get; set; }
    public decimal? SalaryTo { get; set; }
    public string? Currency { get; set; }
    public bool? SalaryGross { get; set; }
    public string? City { get; set; }
    public string? CountryCode { get; set; }
    public WorkFormat WorkFormats { get; set; }
    public EnglishLevel EnglishRequired { get; set; }
    public string? Experience { get; set; }
    public List<string> Skills { get; set; } = [];
    public string Url { get; set; } = "";
    public string? Language { get; set; }
    public List<string> Keywords { get; set; } = [];

    public static ResumeVacancyInput From(Vacancy vacancy) => new()
    {
        Id = vacancy.Id, Title = vacancy.Title, CompanyName = vacancy.CompanyName, Industries = [.. vacancy.Company?.Industries ?? []],
        Description = vacancy.Description, SalaryFrom = vacancy.SalaryFrom, SalaryTo = vacancy.SalaryTo, Currency = vacancy.Currency,
        SalaryGross = vacancy.SalaryGross, City = vacancy.City, CountryCode = vacancy.CountryCode, WorkFormats = vacancy.WorkFormats,
        EnglishRequired = vacancy.EnglishRequired, Experience = vacancy.Experience, Skills = [.. vacancy.Skills], Url = vacancy.Url,
        Language = vacancy.Evaluation?.Language, Keywords = [.. vacancy.Evaluation?.Keywords ?? []],
    };

    public Vacancy ToVacancy() => new()
    {
        Id = Id, Title = Title, CompanyName = CompanyName,
        Company = CompanyName is null && Industries.Count == 0 ? null : new Company { Name = CompanyName ?? "", Industries = [.. Industries] },
        Description = Description, SalaryFrom = SalaryFrom, SalaryTo = SalaryTo, Currency = Currency, SalaryGross = SalaryGross,
        City = City, CountryCode = CountryCode, WorkFormats = WorkFormats, EnglishRequired = EnglishRequired,
        Experience = Experience, Skills = [.. Skills], Url = Url, Fingerprint = "resume-snapshot",
    };
}

public sealed record ResumePreparationInput
{
    public int SchemaVersion { get; set; } = 1;
    public CandidateProfile Profile { get; set; } = new();
    public string ResumeText { get; set; } = "";
    public string StoriesText { get; set; } = "";
    public string FactsText { get; set; } = "";
    public ResumeVacancyInput Vacancy { get; set; } = new();
    public LlmOperationSnapshot Operation { get; set; } = new();
    public ArtifactRecipe Recipe { get; set; } = new();
}

public sealed record ResumeRound
{
    public int Number { get; set; } = 1;
    public ResumeDocument Document { get; set; } = new();
    public List<RequirementMatch> Requirements { get; set; } = [];
    public List<string> Violations { get; set; } = [];
    public List<string> Corrections { get; set; } = [];
    public List<string> Covered { get; set; } = [];
    public List<string> Missing { get; set; } = [];
    public List<string> Unshown { get; set; } = [];
    public string? Model { get; set; }
    public decimal CostUsd { get; set; }
    public string? PdfPath { get; set; }
    public string? PdfHash { get; set; }
    public int Pages { get; set; }
    public List<string> PageTexts { get; set; } = [];
    public List<string> Layout { get; set; } = [];
    public List<string> LayoutFeedback { get; set; } = [];
    public ResumeReview? Review { get; set; }

    [JsonIgnore] public bool Ready => Review is { } review && review.HasCompleteChecklist() && review.Scored().Score >= ResumeTailor.MinReview && Pages is > 0 and <= ResumePdfCheck.MaxPages
        && Layout.Count == 0 && Violations.Count == 0 && Unshown.Count == 0;
    [JsonIgnore] public int Rank => (Ready ? 10000 : 0) + (Review?.Scored().Score ?? 0) * 10 - Layout.Count * 10 - Violations.Count * 10 - Unshown.Count;
}

[MessageIdentity("jobfinder.preparation.resume-draft.v1")]
public sealed record DraftResume { public Guid RequestId { get; set; } public int Round { get; set; } = 1; public int Version { get; set; } = 1; }
[MessageIdentity("jobfinder.preparation.resume-print.v1")]
public sealed record PrintResume { public Guid RequestId { get; set; } public int Round { get; set; } = 1; public int Version { get; set; } = 1; }
[MessageIdentity("jobfinder.preparation.resume-review.v1")]
public sealed record ReviewResume { public Guid RequestId { get; set; } public int Round { get; set; } = 1; public int Version { get; set; } = 1; }
[MessageIdentity("jobfinder.preparation.resume-finish.v1")]
public sealed record FinishResume { public Guid RequestId { get; set; } public int Round { get; set; } = 1; public int Version { get; set; } = 1; }

public sealed class UnsupportedResumeCommandException() : Exception("unsupported resume command identity, round or version");
public sealed class ResumePreparationSupersededException() : Exception("resume preparation inputs changed; request a new preparation");
public sealed class ResumePrintChangedException() : Exception("resume print checkpoint changed or is missing");

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(ResumePreparationInput))]
[JsonSerializable(typeof(ResumeRound))]
[JsonSerializable(typeof(DraftResume))]
[JsonSerializable(typeof(PrintResume))]
[JsonSerializable(typeof(ReviewResume))]
[JsonSerializable(typeof(FinishResume))]
public sealed partial class ResumePreparationJsonContext : JsonSerializerContext;
