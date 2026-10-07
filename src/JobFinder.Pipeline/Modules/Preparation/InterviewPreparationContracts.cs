using System.Text.Json;
using System.Text.Json.Serialization;
using JobFinder.Core.Artifacts;
using JobFinder.Core.Domain;
using JobFinder.Core.Text;
using JobFinder.Llm.Configuration;
using JobFinder.Llm.Tasks;
using Wolverine.Attributes;

namespace JobFinder.Pipeline.Modules.Preparation;

public sealed record InterviewSourceSnapshot
{
    public long Id { get; set; }
    public long ApplicationId { get; set; }
    public long Revision { get; set; }
    public InterviewKind Kind { get; set; }
    public InterviewStatus Status { get; set; }
    public DateTimeOffset? ScheduledAt { get; set; }
    public int DurationMinutes { get; set; }
    public string? Debrief { get; set; }
    public DateTimeOffset CreatedAt { get; set; }

    public static InterviewSourceSnapshot Capture(Interview interview) => new()
    {
        Id = interview.Id, ApplicationId = interview.ApplicationId, Revision = interview.PreparationRevision, Kind = interview.Kind, Status = interview.Status,
        ScheduledAt = Normalize(interview.ScheduledAt), DurationMinutes = interview.DurationMinutes, Debrief = interview.Debrief, CreatedAt = Normalize(interview.CreatedAt)!.Value,
    };
    private static DateTimeOffset? Normalize(DateTimeOffset? value) => value is { } date ? new DateTimeOffset(date.UtcTicks - date.UtcTicks % 10, TimeSpan.Zero) : null;
    public string Hash() => CanonicalJson.Hash(JsonSerializer.Serialize(this, InterviewPreparationJsonContext.Default.InterviewSourceSnapshot));
    public bool Matches(Interview interview, Guid requestId) => interview.PreparationRequestId == requestId && Capture(interview).Hash() == Hash();
}

public sealed record InterviewPreparationSeed
{
    public int Version { get; set; } = 1;
    public InterviewSourceSnapshot Source { get; set; } = new();
    public LlmOperationSnapshot Operation { get; set; } = new();
    public ArtifactRecipe Recipe { get; set; } = new();
    public DateTimeOffset RenderedAt { get; set; }
}

public sealed record InterviewPreparationInput
{
    public int Version { get; set; } = 1;
    public ArtifactRecipe Recipe { get; set; } = new();
    public InterviewAnswerSnapshot AnswerSource { get; set; } = new();
    public long ProfileVersion { get; set; }
    public long FactsVersion { get; set; }
    public string SupportHash { get; set; } = "";
    public string CompanyResearch { get; set; } = "";
    public string MarketText { get; set; } = "";
}

public sealed record InterviewPlanCheckpoint
{
    public InterviewPlan Plan { get; set; } = new();
    public string? Model { get; set; }
    public decimal CostUsd { get; set; }
    public bool Reused { get; set; }
}

public sealed record InterviewPrintCheckpoint
{
    public string Path { get; set; } = "";
    public string Hash { get; set; } = "";
}

public sealed record InterviewPreparationSupport
{
    public ResumeVacancyInput Vacancy { get; set; } = new();
    public string? CompanyDescription { get; set; }
    public Dictionary<string, string> Catalog { get; set; } = [];
    public string Conversation { get; set; } = "";
    public string Notes { get; set; } = "";
    public string Research { get; set; } = "";
    public string? HabrUrl { get; set; }
    public string? Site { get; set; }

    public string Hash() => CanonicalJson.Hash(JsonSerializer.Serialize(this, InterviewPreparationJsonContext.Default.InterviewPreparationSupport));
}

[MessageIdentity("jobfinder.preparation.interview.v1")]
public sealed record PrepareInterview
{
    public Guid RequestId { get; set; }
    public InterviewPreparationStage Stage { get; set; }
    public int Version { get; set; } = 1;
}

public sealed class UnsupportedInterviewPreparationException() : Exception("unsupported interview preparation command or checkpoint");
public sealed class InterviewPreparationSupersededException() : Exception("interview preparation inputs changed");
public sealed class InterviewPreparationPrerequisiteException(string reason) : Exception(reason);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, UseStringEnumConverter = true)]
[JsonSerializable(typeof(InterviewSourceSnapshot))]
[JsonSerializable(typeof(InterviewPreparationSeed))]
[JsonSerializable(typeof(InterviewPreparationInput))]
[JsonSerializable(typeof(InterviewPlanCheckpoint))]
[JsonSerializable(typeof(InterviewPrintCheckpoint))]
[JsonSerializable(typeof(InterviewPreparationSupport))]
[JsonSerializable(typeof(InterviewCopilot))]
[JsonSerializable(typeof(PrepareInterview))]
public sealed partial class InterviewPreparationJsonContext : JsonSerializerContext;
