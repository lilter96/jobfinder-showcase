using System.Text.Json.Serialization;
using JobFinder.Llm.Client;

namespace JobFinder.Llm.Tasks;

public sealed record StoryDraft : IValidatable
{
    public string Title { get; set; } = "";

    public string Situation { get; set; } = "";

    public string Task { get; set; } = "";

    public string Action { get; set; } = "";

    public string Result { get; set; } = "";

    public string Reflection { get; set; } = "";

    public List<string> Skills { get; set; } = [];

    public string? Validate() => string.IsNullOrWhiteSpace(Action) || string.IsNullOrWhiteSpace(Title) ? "empty story" : null;
}

public sealed record MockQuestion
{
    public string Question { get; set; } = "";

    /// <summary>What the interviewer checks with it.</summary>
    public string Focus { get; set; } = "";

    /// <summary>Outline of a strong answer (shown after the user answers).</summary>
    public string Ideal { get; set; } = "";
}

public sealed record MockQuestions : IValidatable
{
    public List<MockQuestion> Questions { get; set; } = [];

    public string? Validate() => Questions.Count(q => !string.IsNullOrWhiteSpace(q.Question)) < 2 ? "fewer than 2 questions" : null;
}

public sealed record MockReview : IValidatable
{
    /// <summary>1..5.</summary>
    public int Score { get; set; }

    public List<string> Good { get; set; } = [];

    public List<string> Missing { get; set; } = [];

    public string Tip { get; set; } = "";

    public string? Validate() => Score is < 1 or > 5 ? "score outside 1..5" : null;
}

public sealed record MockSummary : IValidatable
{
    public int Score { get; set; }

    public string Verdict { get; set; } = "";

    public List<string> Strengths { get; set; } = [];

    public List<string> Focus { get; set; } = [];

    public string? Validate() => Score is < 1 or > 5 ? "score outside 1..5" : null;
}

public sealed record DebriefResult : IValidatable
{
    public string Summary { get; set; } = "";

    public List<string> QuestionsAsked { get; set; } = [];

    public List<string> WentWell { get; set; } = [];

    public List<string> ToImprove { get; set; } = [];

    public string NextSteps { get; set; } = "";

    /// <summary>none / hr_screen / tech_interview / final / offer / rejected.</summary>
    public string Stage { get; set; } = "none";

    public string ThankYouNote { get; set; } = "";

    public string? Validate() => string.IsNullOrWhiteSpace(Summary) ? "empty summary" : null;
}

public sealed record ReplyAnalysis : IValidatable
{
    /// <summary>invitation / question / rejection / offer / viewed / other.</summary>
    public string Classification { get; set; } = "other";

    public string Summary { get; set; } = "";

    /// <summary>ISO-8601 local date-time of a proposed interview, if the message names one.</summary>
    public string? InterviewAt { get; set; }

    /// <summary>hr / technical / system_design / coding / final / other, for an invitation.</summary>
    public string? InterviewKind { get; set; }

    /// <summary>A reply to send, or null when nothing needs answering.</summary>
    public string? Draft { get; set; }

    /// <summary>
    /// What the employer wants: interest_check, facts, schedule, scheduled, questionnaire, test_task, open_question, technical, offer,
    /// rejection, small_talk, other.
    /// </summary>
    public string Intent { get; set; } = "other";

    /// <summary>human / bot / unknown: an HR assistant bot gets short point-by-point answers.</summary>
    public string Counterpart { get; set; } = "unknown";

    /// <summary>A person would answer it without thinking (confirm interest, give a known fact, thank). The code still decides.</summary>
    public bool Routine { get; set; }

    /// <summary>Keys of the candidate facts the draft relies on.</summary>
    public List<string> UsedFacts { get; set; } = [];

    /// <summary>Keys of the facts the answer needs but the facts sheet does not have.</summary>
    public List<string> MissingFacts { get; set; } = [];

    /// <summary>The employer's conditions that contradict the candidate's facts (remote only from Russia, office only, salary cap).</summary>
    public List<string> Conflicts { get; set; } = [];

    /// <summary>Concrete episodes in the draft that the model inferred from the experience rather than read in the profile.</summary>
    public List<string> Verify { get; set; } = [];

    public string? Validate() => string.IsNullOrWhiteSpace(Summary) ? "empty summary" : null;
}

public sealed record OfferTerm
{
    public string Name { get; set; } = "";

    public string Value { get; set; } = "";
}

public sealed record OfferAnalysis : IValidatable
{
    public string Company { get; set; } = "";

    public decimal? MonthlyAmount { get; set; }

    public string? Currency { get; set; }

    public bool? Gross { get; set; }

    public List<OfferTerm> Terms { get; set; } = [];

    public List<string> Strengths { get; set; } = [];

    public List<string> Risks { get; set; } = [];

    public List<string> QuestionsToClarify { get; set; } = [];

    public string CounterTarget { get; set; } = "";

    public string WalkAway { get; set; } = "";

    public string CounterScript { get; set; } = "";

    public string Verdict { get; set; } = "";

    public string? Validate() => string.IsNullOrWhiteSpace(Verdict) ? "empty verdict" : null;
}

public sealed record HhResumeAdvice : IValidatable
{
    public string Title { get; set; } = "";

    public string About { get; set; } = "";

    /// <summary>Key skills to add to the hh resume: ones the candidate has but the resume does not name.</summary>
    public List<string> SkillsToAdd { get; set; } = [];

    public List<string> Improvements { get; set; } = [];

    /// <summary>In demand, not in the experience: to learn, never to claim.</summary>
    public List<string> MarketGaps { get; set; } = [];

    public string? Validate() => string.IsNullOrWhiteSpace(Title) ? "empty title" : null;
}

public sealed record RejectionReason
{
    public string Reason { get; set; } = "";

    /// <summary>The numbers or responses behind it, as given in the input.</summary>
    public string Evidence { get; set; } = "";
}

/// <summary>Why responses end in rejections or silence, from the computed factors: the work on mistakes.</summary>
public sealed record RejectionReview : IValidatable
{
    public string Summary { get; set; } = "";

    public List<RejectionReason> Reasons { get; set; } = [];

    /// <summary>Changes to the hh resume (title, about, skills) that are true to the experience.</summary>
    public List<string> ResumeFixes { get; set; } = [];

    /// <summary>Keywords the resume should name because the experience has them.</summary>
    public List<string> KeywordsToAdd { get; set; } = [];

    /// <summary>Which vacancies are not worth a response (filters, thresholds).</summary>
    public List<string> SearchFixes { get; set; } = [];

    /// <summary>Asked for often, not in the experience: to learn, never to claim.</summary>
    public List<string> Learn { get; set; } = [];

    public string? Validate() => string.IsNullOrWhiteSpace(Summary) ? "empty summary" : null;
}

/// <summary>How the model read one numbered block of a LinkedIn people-search page.</summary>
public sealed record LinkedInPersonReading
{
    /// <summary>The block number [N]; the profile link is taken from that block by code, never from the model.</summary>
    public int Index { get; set; }

    /// <summary>A result card; false for name-only links and the mutual-connections line.</summary>
    public bool IsResult { get; set; }

    public string Name { get; set; } = "";

    public string Headline { get; set; } = "";

    /// <summary>The current employer as written in the block; past employers never count.</summary>
    public string? CurrentCompany { get; set; }

    public string? CurrentRole { get; set; }

    public bool IsRecruiter { get; set; }

    public bool WorksAtTarget { get; set; }

    /// <summary>Maternity leave, "open to work", not working now.</summary>
    public bool OnLeave { get; set; }
}

public sealed record LinkedInPeople : IValidatable
{
    public List<LinkedInPersonReading> People { get; set; } = [];

    public string? Validate() => People.Any(p => p.Index < 1) ? "block index < 1" : null;
}

/// <summary>One piece of the candidate's experience picked for what the vacancy needs.</summary>
public sealed record PickedFact
{
    /// <summary>What the candidate did and what it gave, in plain words.</summary>
    public string Fact { get; set; } = "";

    /// <summary>Which need of the vacancy it answers.</summary>
    public string Need { get; set; } = "";

    /// <summary>Why the recruiter would want to go on talking because of it.</summary>
    public string WhyInteresting { get; set; } = "";

    /// <summary>The fragment of the profile, copied verbatim, that states it: code checks it is there; a fact without one is not used.</summary>
    public string SourceQuote { get; set; } = "";
}

/// <summary>
/// A message to a recruiter, written in steps the schema enforces: what the vacancy needs most, one main case of the experience (plus
/// at most one short supporting fact) that answers exactly that, each quoted from the profile, and only then the text.
/// </summary>
public sealed record RecruiterMessage : IValidatable
{
    /// <summary>What caught the candidate in this vacancy (one concrete thing) and their confirmed link to it: the letter opens with it.</summary>
    public string Hook { get; set; } = "";

    public List<string> Needs { get; set; } = [];

    /// <summary>The main case first; a second one only as a short support.</summary>
    public List<PickedFact> Facts { get; set; } = [];

    /// <summary>"Здравствуйте, Валентина!" (the name as a Russian speaker writes it); empty in a running conversation.</summary>
    public string Greeting { get; set; } = "";

    /// <summary>The body, one to three short paragraphs; the vacancy line and the closing are not part of it.</summary>
    public List<string> Paragraphs { get; set; } = [];

    /// <summary>One sentence about the next step for the reader; empty in a running conversation.</summary>
    public string Closing { get; set; } = "";

    public string? Validate() =>
        Paragraphs.All(string.IsNullOrWhiteSpace) ? "empty message"
        : Paragraphs.Count > 4 ? "more than four paragraphs"
        : Facts.Count > 2 ? "more than two facts"
        : Needs.Count > 2 ? "more than two needs"
        : null;
}

/// <summary>A statement of the message about the candidate's past, and what in the profile supports it.</summary>
public sealed record CheckedClaim
{
    public string Claim { get; set; } = "";

    /// <summary>experience (what was done, known, achieved) or logistics (where the candidate is, formats, availability).</summary>
    public string Kind { get; set; } = "experience";

    public bool Supported { get; set; }

    /// <summary>The supporting fragment copied verbatim from the profile or the facts; empty when not supported.</summary>
    public string Quote { get; set; } = "";
}

/// <summary>An independent reading of a finished message: every claim against the profile, the number of project cases, the naturalness test.</summary>
public sealed record MessageCheck : IValidatable
{
    public List<CheckedClaim> Claims { get; set; } = [];

    /// <summary>Different past projects or companies the message describes.</summary>
    public int ProjectCases { get; set; }

    /// <summary>Without the job title and the technologies it still reads as a natural message to this person.</summary>
    public bool Natural { get; set; }

    /// <summary>The opening gives a concrete reason to write about this vacancy (not "the role is interesting, my experience matches").</summary>
    public bool HasReason { get; set; }

    public string Note { get; set; } = "";

    /// <summary>A recruiter's reading of a first message: 1-10, would they answer it, the weakest place and how to fix it.</summary>
    public RecruiterVerdict Verdict { get; set; } = new();

    public string? Validate() => ProjectCases < 0 ? "negative project count" : Verdict.Score is < 1 or > 10 ? "verdict score out of 1-10" : null;
}

/// <summary>
/// A recruiter's reading of a first message: a checklist judged against what the profile has (a gap of the profile is not the message's
/// fault), scored by the code (<see cref="Scored"/>): 10 minus 3 per critical failure, 1.5 per major one, 0.5 per minor one — 9 means no
/// critical and no major failure.
/// </summary>
public sealed record RecruiterVerdict
{
    /// <summary>Criterion id → weight and what it asks (the prompt lists the same).</summary>
    public static readonly IReadOnlyDictionary<string, (double Weight, string Question)> Criteria = new Dictionary<string, (double, string)>
    {
        ["main_need"] = (3, "главная потребность вакансии связана с конкретным делом кандидата"),
        ["best_case"] = (1.5, "выбран самый сильный под эту потребность кейс из того, что есть в профиле"),
        ["result"] = (1.5, "у кейса понятный результат: что изменилось для системы или бизнеса"),
        ["clarity"] = (1.5, "понятно без контекста: нет внутренних названий без пояснения, повторов и лишних слов"),
        ["tone"] = (1.5, "уверенно и по-человечески: без шаблонов, оговорок, лести и канцелярита"),
        ["next_step"] = (0.5, "понятный следующий шаг"),
        ["brevity"] = (0.5, "коротко: каждая фраза нужна"),
    };

    public int Score { get; set; } = 1;

    public List<VerdictCheck> Checks { get; set; } = [];

    /// <summary>The score of the checklist: a criterion not answered counts as failed.</summary>
    public RecruiterVerdict Scored()
    {
        var failed = Criteria.Keys.Where(id => Checks.FirstOrDefault(c => c.Id == id) is not { Passed: true }).ToList();
        return this with { Score = (int)Math.Clamp(Math.Floor(10 - failed.Sum(id => Criteria[id].Weight)), 1, 10) };
    }

    public bool WouldReply { get; set; }

    /// <summary>What most keeps the recruiter from answering (empty when nothing does).</summary>
    public string Weakest { get; set; } = "";

    /// <summary>A concrete change that removes it: what to say instead, not "make it stronger".</summary>
    public string Fix { get; set; } = "";
}

public sealed record VerdictCheck
{
    public string Id { get; set; } = "";

    public bool Passed { get; set; }

    public string Note { get; set; } = "";
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true,
    NumberHandling = JsonNumberHandling.AllowReadingFromString, AllowTrailingCommas = true)]
[JsonSerializable(typeof(RecruiterMessage))]
[JsonSerializable(typeof(MessageCheck))]
[JsonSerializable(typeof(StoryDraft))]
[JsonSerializable(typeof(MockQuestions))]
[JsonSerializable(typeof(MockReview))]
[JsonSerializable(typeof(MockSummary))]
[JsonSerializable(typeof(DebriefResult))]
[JsonSerializable(typeof(ReplyAnalysis))]
[JsonSerializable(typeof(OfferAnalysis))]
[JsonSerializable(typeof(HhResumeAdvice))]
[JsonSerializable(typeof(LinkedInPeople))]
[JsonSerializable(typeof(RejectionReview))]
public sealed partial class CareerJsonContext : JsonSerializerContext;

internal static class CareerSchemas
{
    public static JsonSchemaSpec Story => Contracts.NativeWireSchemas.StoryDraft;

    public static JsonSchemaSpec MockQuestions => Contracts.NativeWireSchemas.MockQuestions;

    public static JsonSchemaSpec MockReview => Contracts.NativeWireSchemas.MockReview;

    public static JsonSchemaSpec MockSummary => Contracts.NativeWireSchemas.MockSummary;

    public static JsonSchemaSpec Debrief => Contracts.NativeWireSchemas.DebriefResult;

    public static JsonSchemaSpec Reply => Contracts.NativeWireSchemas.ReplyAnalysis;

    public static JsonSchemaSpec Offer => Contracts.NativeWireSchemas.OfferAnalysis;

    public static JsonSchemaSpec LinkedInPeople => Contracts.NativeWireSchemas.LinkedInPeople;

    public static JsonSchemaSpec Recruiter => Contracts.NativeWireSchemas.RecruiterMessage;

    public static JsonSchemaSpec RecruiterCheck => Contracts.NativeWireSchemas.MessageCheck;

    public static JsonSchemaSpec Rejections => Contracts.NativeWireSchemas.RejectionReview;

    public static JsonSchemaSpec HhAdvice => Contracts.NativeWireSchemas.HhResumeAdvice;
}
