using System.Text.Json.Serialization;
using JobFinder.Core.Artifacts;
using JobFinder.Core.Profile;
using JobFinder.Llm.Configuration;
using JobFinder.Llm.Tasks;
using JobFinder.Pipeline.Career;
using Wolverine.Attributes;

namespace JobFinder.Pipeline.Modules.Candidate;

public sealed record StoryCandidateSnapshot
{
    public CandidateProfile Profile { get; set; } = new();
    public long ProfileVersion { get; set; }
    public long FactsVersion { get; set; }
    public string ResumeText { get; set; } = "";
    public string ResumeHash { get; set; } = "";
    public List<string> Questions { get; set; } = [];
}

public sealed record StoryPreparationInput
{
    public int Version { get; set; } = 1;
    public ConversationState Before { get; set; } = new("", 0, 0, null, [], null);
    public ConversationState Waiting { get; set; } = new("", 0, 0, null, [], null);
    public StoryCandidateSnapshot Candidate { get; set; } = new();
    public List<string> Questions { get; set; } = [];
    public string? Answer { get; set; }
    public DateTimeOffset CapturedAt { get; set; }
    public LlmOperationSnapshot Operation { get; set; } = new();
    public ArtifactRecipe Recipe { get; set; } = new();

    public string AnswersText() => string.Join("\n", Waiting.Answers.Select((answer, index) => $"{Questions[Math.Min(index, Questions.Count - 1)]}\n→ {answer}"));
}

public sealed record StoryPreparationResult
{
    public int Version { get; set; } = 1;
    public StoryDraft Draft { get; set; } = new();
}

[MessageIdentity("jobfinder.candidate.draft-story.v1")]
public sealed record DraftStory { public Guid RequestId { get; set; } public int Version { get; set; } = 1; }
[MessageIdentity("jobfinder.candidate.publish-story.v1")]
public sealed record PublishStory { public Guid RequestId { get; set; } public int Version { get; set; } = 1; }

public sealed class UnsupportedStoryPreparationException() : Exception("unsupported or damaged story preparation");
public sealed class StoryPreparationSupersededException() : Exception("story candidate, dialogue or recipe changed");
public sealed class StoryPreparationCheckpointException(Exception inner) : Exception("story checkpoint persistence failed", inner);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, UseStringEnumConverter = true)]
[JsonSerializable(typeof(StoryPreparationInput))]
[JsonSerializable(typeof(StoryPreparationResult))]
[JsonSerializable(typeof(StoryCandidateSnapshot))]
[JsonSerializable(typeof(DraftStory))]
[JsonSerializable(typeof(PublishStory))]
public sealed partial class StoryPreparationJsonContext : JsonSerializerContext;
