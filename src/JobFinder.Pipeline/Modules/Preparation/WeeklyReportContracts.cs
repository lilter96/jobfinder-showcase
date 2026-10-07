using System.Text.Json.Serialization;
using JobFinder.Core.Artifacts;
using JobFinder.Llm.Configuration;
using JobFinder.Llm.Tasks;
using Wolverine.Attributes;

namespace JobFinder.Pipeline.Modules.Preparation;

public sealed record WeeklyReportFrame
{
    public string HeaderHtml { get; set; } = "";
    public string CostHtml { get; set; } = "";
    public int Sent { get; set; }
    public string DemandText { get; set; } = "";
    public string FunnelText { get; set; } = "";
    public bool HasDemand { get; set; }
}

public sealed record WeeklyReportInput
{
    public int Version { get; set; } = 1;
    public string? ProfileJson { get; set; }
    public long ProfileVersion { get; set; }
    public string ResumeText { get; set; } = "";
    public WeeklyReportFrame Frame { get; set; } = new();
    public DateTimeOffset CapturedAt { get; set; }
    public long? JobRunId { get; set; }
    public DateTimeOffset? JobStartedAt { get; set; }
    public bool Manual { get; set; } = true;
    public DateTimeOffset? Slot { get; set; }
    public LlmOperationSnapshot Operation { get; set; } = new();
    public ArtifactRecipe Recipe { get; set; } = new();
    [JsonIgnore] public bool NeedsAdvice => Frame.HasDemand && !string.IsNullOrWhiteSpace(ResumeText);
}

public sealed record WeeklyReportResult
{
    public string Html { get; set; } = "";
    public int Sent { get; set; }
    public HhResumeAdvice? Advice { get; set; }
    public string? Model { get; set; }
    public decimal CostUsd { get; set; }
    public DateTimeOffset GeneratedAt { get; set; }
}

public sealed record WeeklyReportCursor
{
    public int Version { get; set; } = 1;
    public DateTimeOffset Slot { get; set; }
    public Guid RequestId { get; set; }
    public long JobRunId { get; set; }
    public long NotificationId { get; set; }
}

[MessageIdentity("jobfinder.preparation.prepare-weekly-report.v1")]
public sealed record PrepareWeeklyReport { public Guid RequestId { get; set; } public int Version { get; set; } = 1; }
[MessageIdentity("jobfinder.preparation.publish-weekly-report.v1")]
public sealed record PublishWeeklyReport { public Guid RequestId { get; set; } public int Version { get; set; } = 1; }

public sealed class UnsupportedWeeklyReportException() : Exception("unsupported or damaged weekly report");
public sealed class WeeklyReportSupersededException() : Exception("weekly report candidate, publication or recipe changed");
public sealed class WeeklyReportCheckpointException(Exception inner) : Exception("weekly report checkpoint persistence failed", inner);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, UseStringEnumConverter = true)]
[JsonSerializable(typeof(WeeklyReportInput))]
[JsonSerializable(typeof(WeeklyReportResult))]
[JsonSerializable(typeof(WeeklyReportCursor))]
[JsonSerializable(typeof(PrepareWeeklyReport))]
[JsonSerializable(typeof(PublishWeeklyReport))]
public sealed partial class WeeklyReportJsonContext : JsonSerializerContext;
