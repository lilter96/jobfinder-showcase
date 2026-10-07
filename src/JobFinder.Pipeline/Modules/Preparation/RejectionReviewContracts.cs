using System.Text.Json.Serialization;
using JobFinder.Core.Artifacts;
using JobFinder.Llm.Configuration;
using JobFinder.Llm.Tasks;
using Wolverine.Attributes;

namespace JobFinder.Pipeline.Modules.Preparation;

public sealed record RejectionReviewInputSnapshot
{
    public int Version { get; set; } = 1;
    public string ProfileJson { get; set; } = "";
    public long ProfileVersion { get; set; }
    public string ResumeText { get; set; } = "";
    public string SourceJson { get; set; } = "[]";
    public string FactsText { get; set; } = "";
    public int Responses { get; set; }
    public int Rejections { get; set; }
    public string? PreviousJson { get; set; }
    public string? PreviousAt { get; set; }
    public DateTimeOffset CapturedAt { get; set; }
    public LlmOperationSnapshot Operation { get; set; } = new();
    public ArtifactRecipe Recipe { get; set; } = new();
}

public sealed record RejectionReviewResult
{
    public RejectionReview Review { get; set; } = new();
    public string Model { get; set; } = "";
    public decimal CostUsd { get; set; }
    public DateTimeOffset GeneratedAt { get; set; }
}

[MessageIdentity("jobfinder.preparation.analyze-rejections.v1")]
public sealed record AnalyzeRejectionReview { public Guid RequestId { get; set; } public int Version { get; set; } = 1; }
[MessageIdentity("jobfinder.preparation.publish-rejections.v1")]
public sealed record PublishRejectionReview { public Guid RequestId { get; set; } public int Version { get; set; } = 1; }

public sealed class UnsupportedRejectionReviewException() : Exception("unsupported or damaged rejection review");
public sealed class RejectionReviewSupersededException() : Exception("rejection review source or recipe changed");
public sealed class RejectionReviewCheckpointException(Exception inner) : Exception("rejection review checkpoint persistence failed", inner);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, UseStringEnumConverter = true)]
[JsonSerializable(typeof(RejectionReviewInputSnapshot))]
[JsonSerializable(typeof(RejectionReviewResult))]
[JsonSerializable(typeof(AnalyzeRejectionReview))]
[JsonSerializable(typeof(PublishRejectionReview))]
public sealed partial class RejectionReviewJsonContext : JsonSerializerContext;
