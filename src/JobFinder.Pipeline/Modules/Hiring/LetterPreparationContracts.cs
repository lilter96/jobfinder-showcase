using System.Text.Json;
using System.Text.Json.Serialization;
using JobFinder.Core.Artifacts;
using JobFinder.Core.Domain;
using JobFinder.Core.Profile;
using JobFinder.Llm.Configuration;
using JobFinder.Llm.Tasks;
using JobFinder.Pipeline.Career;
using Wolverine.Attributes;

namespace JobFinder.Pipeline.Modules.Hiring;

public sealed record LetterBriefInput
{
    public AssessmentVacancy Vacancy { get; set; } = new();
    public VacancyEvaluation? Evaluation { get; set; }
    public LetterChannel Channel { get; set; }
    public string Contact { get; set; } = "";
    public string Situation { get; set; } = "";
    public bool AttachesResume { get; set; }
    public string Conversation { get; set; } = "";
    public bool First { get; set; }
    public bool NeedsFact { get; set; }
    public bool Deep { get; set; }

    public static LetterBriefInput From(LetterBrief brief) => new()
    {
        Vacancy = AssessmentVacancy.From(brief.Vacancy), Evaluation = brief.Vacancy.Evaluation,
        Channel = brief.Channel, Contact = brief.Contact, Situation = brief.Situation,
        AttachesResume = brief.AttachesResume, Conversation = brief.Conversation, First = brief.First,
        NeedsFact = brief.NeedsFact, Deep = brief.Deep,
    };

    public LetterBrief ToBrief()
    {
        var vacancy = Vacancy.ToVacancy();
        vacancy.Evaluation = Evaluation;
        return new(vacancy, Channel, Contact, Situation, AttachesResume, Conversation, First, NeedsFact, Deep);
    }
}

public sealed record LetterPreparationInput
{
    public int Version { get; set; } = 1;
    public LetterBriefInput Brief { get; set; } = new();
    public AssessmentVacancy Anchor { get; set; } = new();
    public string SourceContext { get; set; } = "";
    public CandidateProfile Profile { get; set; } = new();
    public CandidateFacts Facts { get; set; } = new();
    public long ProfileVersion { get; set; }
    public long FactsVersion { get; set; }
    public DateTimeOffset CapturedAt { get; set; }
    public LlmOperationSnapshot Operation { get; set; } = new();
    public ArtifactRecipe Recipe { get; set; } = new();
    public string? ExactText { get; set; }
    public string ReviewContextHash { get; set; } = "";
}

public sealed record LetterCandidate
{
    public string Text { get; set; } = "";
    public string Written { get; set; } = "";
    public List<RecruiterEvidence> Evidence { get; set; } = [];
    public List<string> Problems { get; set; } = [];
    public int Score { get; set; }
    public ApplicationLetterReview? Review { get; set; }
    public LetterDraft ToDraft() => new(Text, Evidence, Problems, Review);
}

public sealed record LetterPreparationTicket(Guid RequestId, LetterDraft? Result);
public sealed record LetterReviewTicket(Guid? RequestId, ApplicationLetterReview? Review);

[MessageIdentity("jobfinder.hiring.letter-draft.v1")]
public sealed record DraftLetterLane { public Guid RequestId { get; set; } public Guid LaneId { get; set; } public int Round { get; set; } = 1; public int Version { get; set; } = 1; }
[MessageIdentity("jobfinder.hiring.letter-review.v1")]
public sealed record ReviewLetterLane { public Guid RequestId { get; set; } public Guid LaneId { get; set; } public int Round { get; set; } = 1; public int Version { get; set; } = 1; }
[MessageIdentity("jobfinder.hiring.letter-choose.v1")]
public sealed record ChooseLetterRound { public Guid RequestId { get; set; } public int Round { get; set; } = 1; public int Version { get; set; } = 1; }

public sealed class UnsupportedLetterPreparationException() : Exception("unsupported letter preparation command or snapshot");
public sealed class LetterPreparationSupersededException(string reason = "letter preparation inputs or recipe superseded") : Exception(reason);
public sealed class LetterPreparationFailedException(string reason) : Exception("letter preparation failed: " + reason);
public sealed class LetterPreparationCheckpointException(Exception inner) : Exception("letter checkpoint persistence failed", inner);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, UseStringEnumConverter = true)]
[JsonSerializable(typeof(LetterPreparationInput))]
[JsonSerializable(typeof(LetterBriefInput))]
[JsonSerializable(typeof(AssessmentVacancy))]
[JsonSerializable(typeof(LetterCandidate))]
[JsonSerializable(typeof(DraftLetterLane))]
[JsonSerializable(typeof(ReviewLetterLane))]
[JsonSerializable(typeof(ChooseLetterRound))]
public sealed partial class LetterPreparationJsonContext : JsonSerializerContext;
