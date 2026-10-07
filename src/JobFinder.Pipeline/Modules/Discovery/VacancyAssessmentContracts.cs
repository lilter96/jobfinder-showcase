using System.Text.Json;
using System.Text.Json.Serialization;
using JobFinder.Core.Artifacts;
using JobFinder.Core.Domain;
using JobFinder.Core.Policies;
using JobFinder.Core.Profile;
using JobFinder.Llm.Configuration;
using JobFinder.Pipeline.Settings;
using Wolverine.Attributes;

namespace JobFinder.Pipeline.Modules.Discovery;


public sealed record VacancyAssessmentInput
{
    public int Version { get; set; } = 1;
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public AssessmentAnnotationSnapshot? Annotation { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public AssessmentPreviewSnapshot? Preview { get; set; }
    public AssessmentVacancy Vacancy { get; set; } = new();
    public CandidateProfile Profile { get; set; } = new();
    public CandidateFacts Facts { get; set; } = new();
    public long ProfileVersion { get; set; }
    public long FactsVersion { get; set; }
    public SearchPolicy Search { get; set; } = new();
    public long SearchVersion { get; set; }
    public AutomationPolicy Automation { get; set; } = new();
    public long AutomationVersion { get; set; }
    public LlmOperationSnapshot Operation { get; set; } = new();
    public ArtifactRecipe Recipe { get; set; } = new();
    public string SalaryNote { get; set; } = "";
    public int SalaryAdjustment { get; set; }
    public string? MarketNote { get; set; }
    public decimal? RatioToP90 { get; set; }
    public int Reposts { get; set; }
    public DateTimeOffset CapturedAt { get; set; }
    public bool Responded { get; set; }
    public bool CanAutoApply { get; set; }
    public int AutoToday { get; set; }
    public bool EmailReady { get; set; }
}

public sealed record AssessmentScore
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public decimal? CostUsd { get; set; }
    public VacancyEvaluation? Evaluation { get; set; }
    public int? Score { get; set; }
    public EnglishLevel EnglishRequired { get; set; }
    public Legitimacy Legitimacy { get; set; }
    public VacancyStatus Status { get; set; }
    public Decision Decision { get; set; }
    public string? RejectReason { get; set; }
}

public sealed record AssessmentPreviewSnapshot
{
    public string Key { get; set; } = "";
}

[MessageIdentity("jobfinder.discovery.vacancy-preview-score.v1")]
public sealed record ScoreVacancyPreview { public Guid RequestId { get; set; } public int Version { get; set; } = 1; }
[MessageIdentity("jobfinder.discovery.vacancy-preview-finish.v1")]
public sealed record FinishVacancyPreview { public Guid RequestId { get; set; } public int Version { get; set; } = 1; }

public sealed record AssessmentLetter
{
    public Guid? PreparationId { get; set; }
    public string Text { get; set; } = "";
    public ApplicationLetterReview? Review { get; set; }
    public List<string> Problems { get; set; } = [];
}

[MessageIdentity("jobfinder.discovery.vacancy-score.v1")]
public sealed record ScoreVacancy { public Guid RequestId { get; set; } public int Version { get; set; } = 1; }
[MessageIdentity("jobfinder.discovery.vacancy-capture.v1")]
public sealed record CaptureVacancyAssessment
{
    public long VacancyId { get; set; }
    public Guid CaptureId { get; set; }
    public long Revision { get; set; }
    public int Version { get; set; } = 1;
}
[MessageIdentity("jobfinder.hiring.vacancy-letter.v1")]
public sealed record PrepareVacancyLetter { public Guid RequestId { get; set; } public int Version { get; set; } = 1; }
[MessageIdentity("jobfinder.discovery.vacancy-finish.v1")]
public sealed record FinishVacancyAssessment { public Guid RequestId { get; set; } public int Version { get; set; } = 1; }

public sealed class UnsupportedVacancyAssessmentException() : Exception("unsupported vacancy assessment command or snapshot version");
public sealed class VacancyAssessmentSupersededException() : Exception("vacancy assessment inputs or recipe changed");
public sealed class VacancyAssessmentCheckpointException(Exception inner) : Exception("vacancy assessment checkpoint persistence failed", inner);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, UseStringEnumConverter = true)]
[JsonSerializable(typeof(VacancyAssessmentInput))]
[JsonSerializable(typeof(AssessmentVacancy))]
[JsonSerializable(typeof(AssessmentScore))]
[JsonSerializable(typeof(AssessmentAnnotationSnapshot))]
[JsonSerializable(typeof(AssessmentPreviewSnapshot))]
[JsonSerializable(typeof(ScoreVacancyPreview))]
[JsonSerializable(typeof(FinishVacancyPreview))]
[JsonSerializable(typeof(AssessmentLetter))]
[JsonSerializable(typeof(ScoreVacancy))]
[JsonSerializable(typeof(CaptureVacancyAssessment))]
[JsonSerializable(typeof(PrepareVacancyLetter))]
[JsonSerializable(typeof(FinishVacancyAssessment))]
public sealed partial class VacancyAssessmentJsonContext : JsonSerializerContext;
