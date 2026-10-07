using System.Text.Json.Serialization;
using JobFinder.Core.Artifacts;
using JobFinder.Core.Domain;
using JobFinder.Core.Profile;
using JobFinder.Core.Text;
using JobFinder.Llm.Configuration;
using JobFinder.Llm.Tasks;
using JobFinder.Pipeline.Actions;
using JobFinder.Pipeline.Career;
using Wolverine.Attributes;

namespace JobFinder.Pipeline.Modules.Preparation;

public enum MockPreparationKind { Questions, Review, Summary }

public sealed record MockSessionSnapshot
{
    public long Id { get; set; }
    public long Revision { get; set; }
    public Guid? PreparationRequestId { get; set; }
    public long? VacancyId { get; set; }
    public InterviewKind Kind { get; set; }
    public MockStatus Status { get; set; }
    public string TurnsJson { get; set; } = "[]";
    public int Current { get; set; }
    public string? Summary { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? FinishedAt { get; set; }

    public static MockSessionSnapshot Capture(MockSession source) => new()
    {
        Id = source.Id, Revision = source.Revision, PreparationRequestId = source.PreparationRequestId, VacancyId = source.VacancyId,
        Kind = source.Kind, Status = source.Status, TurnsJson = source.TurnsJson, Current = source.Current,
        Summary = source.Summary, CreatedAt = source.CreatedAt, FinishedAt = source.FinishedAt,
    };
}

public sealed record MockStartSnapshot
{
    public Guid IntentId { get; set; }
    public string? DialogueJson { get; set; }
    public MockSessionSnapshot? Previous { get; set; }
    public CandidateProfile Profile { get; set; } = new();
    public long ProfileVersion { get; set; }
    public long FactsVersion { get; set; }
    public string Stories { get; set; } = "";
    public AssessmentVacancy? Vacancy { get; set; }
    public InterviewKind Kind { get; set; }
    public long? InterviewId { get; set; }
    public long? InterviewRevision { get; set; }
    public long? InterviewApplicationId { get; set; }
    public string? PreparationJson { get; set; }
    public bool Hr { get; set; }
    public List<MockTurn> Turns { get; set; } = [];
    public string Header { get; set; } = "";
}

public sealed record MockPreparationInput
{
    public int Version { get; set; } = 1;
    public MockPreparationKind Kind { get; set; }
    public MockSessionSnapshot Source { get; set; } = new();
    public ConversationState Waiting { get; set; } = new("", 0, 0, null, [], null);
    public MockStartSnapshot? Start { get; set; }
    public string? Answer { get; set; }
    public LearningItemSnapshot? Learning { get; set; }
    public string Prefix { get; set; } = "";
    public DateTimeOffset CapturedAt { get; set; }
    public LlmOperationSnapshot Operation { get; set; } = new();
    public ArtifactRecipe Recipe { get; set; } = new();

    public string Transcript() => string.Join("\n\n", ConversationStore.Turns(Source.TurnsJson).Where(turn => turn.Answer is not null && turn.Score is not null)
        .Select(turn => $"Вопрос: {turn.Question}\nОтвет: {turn.Answer}\nОценка: {turn.Score}/5\nРазбор: {turn.Feedback}"));
}

public sealed record MockPreparationResult
{
    public int Version { get; set; } = 1;
    public List<MockTurn>? Questions { get; set; }
    public MockReview? Review { get; set; }
    public MockSummary? Summary { get; set; }
}

public sealed record MockPublication(ActionResult Result, Guid? RequestId = null);

[MessageIdentity("jobfinder.preparation.prepare-mock.v1")]
public sealed record PrepareMock { public Guid RequestId { get; set; } public int Version { get; set; } = 1; }
[MessageIdentity("jobfinder.preparation.publish-mock.v1")]
public sealed record PublishMock { public Guid RequestId { get; set; } public int Version { get; set; } = 1; }

public sealed class UnsupportedMockPreparationException() : Exception("unsupported or damaged mock interview preparation");
public sealed class MockPreparationSupersededException() : Exception("mock session, dialogue, candidate or recipe changed");
public sealed class MockPreparationCheckpointException(Exception inner) : Exception("mock checkpoint persistence failed", inner);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, UseStringEnumConverter = true)]
[JsonSerializable(typeof(MockStartSnapshot))]
[JsonSerializable(typeof(MockSessionSnapshot))]
[JsonSerializable(typeof(MockPreparationInput))]
[JsonSerializable(typeof(MockPreparationResult))]
[JsonSerializable(typeof(PrepareMock))]
[JsonSerializable(typeof(PublishMock))]
public sealed partial class MockPreparationJsonContext : JsonSerializerContext;
