using System.Text.Json.Serialization;
using JobFinder.Core.Artifacts;
using JobFinder.Core.Domain;
using JobFinder.Core.Profile;
using JobFinder.Llm.Configuration;
using JobFinder.Pipeline.Settings;
using Wolverine.Attributes;

namespace JobFinder.Pipeline.Modules.Hiring;

public sealed record ApplicationDeliveryInput
{
    public int Version { get; set; } = 1;
    public long ApplicationId { get; set; }
    public Guid OperationId { get; set; }
    public long Revision { get; set; }
    public ApplicationMode Mode { get; set; }
    public ApplicationChannel Channel { get; set; }
    public string? Target { get; set; }
    public string? OriginalLetter { get; set; }
    public string Text { get; set; } = "";
    public AssessmentVacancy Vacancy { get; set; } = new();
    public VacancyEvaluation? Evaluation { get; set; }
    public int? Score { get; set; }
    public Decision Decision { get; set; }
    public ApplicationLetterReview? CachedReview { get; set; }
    public CandidateProfile Profile { get; set; } = new();
    public long ProfileVersion { get; set; }
    public CandidateFacts Facts { get; set; } = new();
    public long FactsVersion { get; set; }
    public AutomationPolicy Automation { get; set; } = new();
    public long AutomationVersion { get; set; }
    public LlmOperationSnapshot Operation { get; set; } = new();
    public ArtifactRecipe Recipe { get; set; } = new();
    public string ReviewContextHash { get; set; } = "";
    public DateTimeOffset CapturedAt { get; set; }
}

[MessageIdentity("jobfinder.hiring.application-prepare.v1")]
public sealed record PrepareApplicationDelivery
{
    public Guid RequestId { get; set; }
    public int Version { get; set; } = 1;
}

[MessageIdentity("jobfinder.hiring.application-dispatch.v1")]
public sealed record DispatchApplicationDelivery
{
    public Guid RequestId { get; set; }
    public int Version { get; set; } = 1;
}

[MessageIdentity("jobfinder.hiring.application-contact.v1")]
public sealed record RememberApplicationContact
{
    public Guid RequestId { get; set; }
    public int Version { get; set; } = 1;
    public string? Name { get; set; }
    public string? Email { get; set; }
    public string? Phone { get; set; }
}

[MessageIdentity("jobfinder.hiring.application-retry.v1")]
public sealed record RetryApplicationDelivery
{
    public Guid RequestId { get; set; }
    public long ApplicationRevision { get; set; }
    public int Version { get; set; } = 1;
}

public sealed class UnsupportedApplicationDeliveryException() : Exception("unsupported application delivery snapshot or command");
public sealed class ApplicationDeliverySupersededException() : Exception("application delivery source or candidate superseded");

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, UseStringEnumConverter = true)]
[JsonSerializable(typeof(ApplicationDeliveryInput))]
[JsonSerializable(typeof(PrepareApplicationDelivery))]
[JsonSerializable(typeof(DispatchApplicationDelivery))]
[JsonSerializable(typeof(RememberApplicationContact))]
[JsonSerializable(typeof(RetryApplicationDelivery))]
[JsonSerializable(typeof(VacancyEvaluation))]
public sealed partial class ApplicationDeliveryJsonContext : JsonSerializerContext;
