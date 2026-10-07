using System.Text.Json.Serialization;
using JobFinder.Core.Artifacts;
using JobFinder.Llm.Configuration;
using JobFinder.Llm.Tasks;
using Wolverine.Attributes;

namespace JobFinder.Pipeline.Modules.Preparation;

public sealed record HrPlaybookInput
{
    public int Version { get; set; } = 1;
    public string ProfileJson { get; set; } = "";
    public long ProfileVersion { get; set; }
    public string FactsJson { get; set; } = "";
    public long FactsVersion { get; set; }
    public string Evidence { get; set; } = "";
    public string Funnel { get; set; } = "";
    public HrBank Bank { get; set; } = new();
    public string? PreviousJson { get; set; }
    public DateTimeOffset CapturedAt { get; set; }
    public LlmOperationSnapshot Operation { get; set; } = new();
    public ArtifactRecipe Recipe { get; set; } = new();
}

public sealed record HrPositionEditSnapshot
{
    public int Version { get; set; } = 1;
    public long ProfileVersion { get; set; }
    public long FactsVersion { get; set; }
    public string FactsJson { get; set; } = "";
    public HrQuestion Question { get; set; } = new();
    public string QuestionHash { get; set; } = "";
}

[MessageIdentity("jobfinder.preparation.build-hr-playbook.v1")]
public sealed record BuildHrPlaybook { public Guid RequestId { get; set; } public int Version { get; set; } = 1; }
[MessageIdentity("jobfinder.preparation.publish-hr-playbook.v1")]
public sealed record PublishHrPlaybook { public Guid RequestId { get; set; } public int Version { get; set; } = 1; }

public sealed class UnsupportedHrPlaybookException() : Exception("unsupported or damaged HR preparation");
public sealed class HrPlaybookSupersededException() : Exception("HR preparation source or recipe changed");
public sealed class HrPlaybookCheckpointException(Exception inner, string? partialJson = null) : Exception("HR checkpoint persistence failed", inner)
{
    public string? PartialJson { get; } = partialJson;
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, UseStringEnumConverter = true)]
[JsonSerializable(typeof(HrPlaybookInput))]
[JsonSerializable(typeof(HrPositionEditSnapshot))]
[JsonSerializable(typeof(BuildHrPlaybook))]
[JsonSerializable(typeof(PublishHrPlaybook))]
public sealed partial class HrPlaybookJsonContext : JsonSerializerContext;
