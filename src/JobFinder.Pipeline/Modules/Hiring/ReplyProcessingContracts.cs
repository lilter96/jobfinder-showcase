using System.Text.Json.Serialization;
using JobFinder.Core.Artifacts;
using JobFinder.Core.Domain;
using JobFinder.Core.Profile;
using JobFinder.Llm.Configuration;
using JobFinder.Llm.Tasks;
using JobFinder.Pipeline.Modules.Discovery;
using Wolverine.Attributes;

namespace JobFinder.Pipeline.Modules.Hiring;

public sealed record InboundReply
{
    public int Version { get; set; } = 1;
    public long ApplicationId { get; set; }
    public EventChannel Channel { get; set; }
    public string Author { get; set; } = "";
    public string Text { get; set; } = "";
    public string? ExternalId { get; set; }
    public DateTimeOffset At { get; set; }
    public string? Subject { get; set; }
    public bool YourChat { get; set; }

    public string Message() => (Subject is null ? "" : Subject + "\n") + Text;
}

public sealed record ReplyProcessingInput
{
    public int Version { get; set; } = 1;
    public long ApplicationId { get; set; }
    public AssessmentVacancy Vacancy { get; set; } = new();
    public CandidateProfile Profile { get; set; } = new();
    public CandidateFacts Facts { get; set; } = new();
    public long ProfileVersion { get; set; }
    public long FactsVersion { get; set; }
    public string Stories { get; set; } = "";
    public string ContextHash { get; set; } = "";
    public string[] Interviews { get; set; } = [];
    public string Conversation { get; set; } = "";
    public string Message { get; set; } = "";
    public DateTimeOffset CapturedAt { get; set; }
    public string TimeZone { get; set; } = "Europe/Minsk";
    public bool AutoReply { get; set; }
    public LlmOperationSnapshot Operation { get; set; } = new();
    public ArtifactRecipe Recipe { get; set; } = new();
    public ReplyDraftSnapshot? DraftSnapshot { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? OriginalEventJson { get; set; }
}

public sealed record ReplyProcessingResult
{
    public ReplyAnalysis Analysis { get; set; } = new();
    public List<string> Problems { get; set; } = [];
}

public sealed record ReplyProcessingTicket(Guid RequestId, bool AlreadyPublished = false);

[MessageIdentity("jobfinder.hiring.process-reply.v1")]
public sealed record ProcessReply
{
    public int Version { get; set; } = 1;
    public Guid RequestId { get; set; }
    public ReplyProcessingStage Stage { get; set; }
    public int DeepAttempt { get; set; }
}

public sealed class UnsupportedReplyProcessingException() : Exception("unsupported or damaged reply processing protocol");
public sealed class ReplyProcessingSupersededException() : Exception("reply source or candidate inputs changed");

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, UseStringEnumConverter = true)]
[JsonSerializable(typeof(InboundReply))]
[JsonSerializable(typeof(ReplyProcessingInput))]
[JsonSerializable(typeof(ReplyProcessingResult))]
[JsonSerializable(typeof(ProcessReply))]
public sealed partial class ReplyProcessingJsonContext : JsonSerializerContext;
