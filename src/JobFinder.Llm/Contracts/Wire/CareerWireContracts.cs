using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;
using JobFinder.Llm.Client;
using JobFinder.Llm.Tasks;

namespace JobFinder.Llm.Contracts.Wire;

public sealed record StoryDraftWire : IValidatable
{
    [JsonRequired] public string Title { get; set; } = "";

    [JsonRequired] public string Situation { get; set; } = "";

    [JsonRequired] public string Task { get; set; } = "";

    [JsonRequired] public string Action { get; set; } = "";

    [JsonRequired] public string Result { get; set; } = "";

    [JsonRequired] public string Reflection { get; set; } = "";

    [JsonRequired] public List<string> Skills { get; set; } = [];

    public StoryDraft ToDomain() => WireMapping.Map(this, GeneratedWireJsonContext.Default.StoryDraftWire, CareerJsonContext.Default.StoryDraft);

    public string? Validate() => WireMapping.Validate(ToDomain());
}

public sealed record MockQuestionsWireQuestionsItem
{
    [JsonRequired] public string Question { get; set; } = "";

    [JsonRequired] public string Focus { get; set; } = "";

    [JsonRequired] public string Ideal { get; set; } = "";
}

public sealed record MockQuestionsWire : IValidatable
{
    [JsonRequired] public List<MockQuestionsWireQuestionsItem> Questions { get; set; } = [];

    public MockQuestions ToDomain() => WireMapping.Map(this, GeneratedWireJsonContext.Default.MockQuestionsWire, CareerJsonContext.Default.MockQuestions);

    public string? Validate() => WireMapping.Validate(ToDomain());
}

public sealed record MockReviewWire : IValidatable
{
    [JsonRequired, Range(1, 5)] public int Score { get; set; }

    [JsonRequired] public List<string> Good { get; set; } = [];

    [JsonRequired] public List<string> Missing { get; set; } = [];

    [JsonRequired] public string Tip { get; set; } = "";

    public MockReview ToDomain() => WireMapping.Map(this, GeneratedWireJsonContext.Default.MockReviewWire, CareerJsonContext.Default.MockReview);

    public string? Validate() => WireMapping.Validate(ToDomain());
}

public sealed record MockSummaryWire : IValidatable
{
    [JsonRequired, Range(1, 5)] public int Score { get; set; }

    [JsonRequired] public string Verdict { get; set; } = "";

    [JsonRequired] public List<string> Strengths { get; set; } = [];

    [JsonRequired] public List<string> Focus { get; set; } = [];

    public MockSummary ToDomain() => WireMapping.Map(this, GeneratedWireJsonContext.Default.MockSummaryWire, CareerJsonContext.Default.MockSummary);

    public string? Validate() => WireMapping.Validate(ToDomain());
}

public sealed record DebriefResultWire : IValidatable
{
    [JsonRequired] public string Summary { get; set; } = "";

    [JsonRequired] public List<string> QuestionsAsked { get; set; } = [];

    [JsonRequired] public List<string> WentWell { get; set; } = [];

    [JsonRequired] public List<string> ToImprove { get; set; } = [];

    [JsonRequired] public string NextSteps { get; set; } = "";

    [JsonRequired, WireChoices("none", "hr_screen", "tech_interview", "final", "offer", "rejected")] public string Stage { get; set; } = "";

    [JsonRequired] public string ThankYouNote { get; set; } = "";

    public DebriefResult ToDomain() => WireMapping.Map(this, GeneratedWireJsonContext.Default.DebriefResultWire, CareerJsonContext.Default.DebriefResult);

    public string? Validate() => WireMapping.Validate(ToDomain());
}

public sealed record ReplyAnalysisWire : IValidatable
{
    [JsonRequired, WireChoices("invitation", "question", "rejection", "offer", "viewed", "other")] public string Classification { get; set; } = "";

    [JsonRequired] public string Summary { get; set; } = "";

    [JsonRequired] public string? InterviewAt { get; set; }

    [JsonRequired, WireChoices("hr", "technical", "system_design", "coding", "final", "other", AllowNull = true)] public string? InterviewKind { get; set; }

    [JsonRequired] public string? Draft { get; set; }

    [JsonRequired, WireChoices("interest_check", "facts", "schedule", "scheduled", "questionnaire", "test_task", "open_question", "technical", "offer", "rejection", "small_talk", "other")] public string Intent { get; set; } = "";

    [JsonRequired, WireChoices("human", "bot", "unknown")] public string Counterpart { get; set; } = "";

    [JsonRequired] public bool Routine { get; set; }

    [JsonRequired] public List<string> UsedFacts { get; set; } = [];

    [JsonRequired] public List<string> MissingFacts { get; set; } = [];

    [JsonRequired] public List<string> Conflicts { get; set; } = [];

    [JsonRequired] public List<string> Verify { get; set; } = [];

    public ReplyAnalysis ToDomain() => WireMapping.Map(this, GeneratedWireJsonContext.Default.ReplyAnalysisWire, CareerJsonContext.Default.ReplyAnalysis);

    public string? Validate() => WireMapping.Validate(ToDomain());
}

public sealed record OfferAnalysisWireTermsItem
{
    [JsonRequired] public string Name { get; set; } = "";

    [JsonRequired] public string Value { get; set; } = "";
}

public sealed record OfferAnalysisWire : IValidatable
{
    [JsonRequired] public string Company { get; set; } = "";

    [JsonRequired] public decimal? MonthlyAmount { get; set; }

    [JsonRequired] public string? Currency { get; set; }

    [JsonRequired] public bool? Gross { get; set; }

    [JsonRequired] public List<OfferAnalysisWireTermsItem> Terms { get; set; } = [];

    [JsonRequired] public List<string> Strengths { get; set; } = [];

    [JsonRequired] public List<string> Risks { get; set; } = [];

    [JsonRequired] public List<string> QuestionsToClarify { get; set; } = [];

    [JsonRequired] public string CounterTarget { get; set; } = "";

    [JsonRequired] public string WalkAway { get; set; } = "";

    [JsonRequired] public string CounterScript { get; set; } = "";

    [JsonRequired] public string Verdict { get; set; } = "";

    public OfferAnalysis ToDomain() => WireMapping.Map(this, GeneratedWireJsonContext.Default.OfferAnalysisWire, CareerJsonContext.Default.OfferAnalysis);

    public string? Validate() => WireMapping.Validate(ToDomain());
}

public sealed record LinkedInPeopleWirePeopleItem
{
    [JsonRequired] public int Index { get; set; }

    [JsonRequired] public bool IsResult { get; set; }

    [JsonRequired] public string Name { get; set; } = "";

    [JsonRequired] public string Headline { get; set; } = "";

    [JsonRequired] public string? CurrentCompany { get; set; }

    [JsonRequired] public string? CurrentRole { get; set; }

    [JsonRequired] public bool IsRecruiter { get; set; }

    [JsonRequired] public bool WorksAtTarget { get; set; }

    [JsonRequired] public bool OnLeave { get; set; }
}

public sealed record LinkedInPeopleWire : IValidatable
{
    [JsonRequired] public List<LinkedInPeopleWirePeopleItem> People { get; set; } = [];

    public LinkedInPeople ToDomain() => WireMapping.Map(this, GeneratedWireJsonContext.Default.LinkedInPeopleWire, CareerJsonContext.Default.LinkedInPeople);

    public string? Validate() => WireMapping.Validate(ToDomain());
}

public sealed record RecruiterMessageWireFactsItem
{
    [JsonRequired] public string Fact { get; set; } = "";

    [JsonRequired] public string Need { get; set; } = "";

    [JsonRequired] public string WhyInteresting { get; set; } = "";

    [JsonRequired] public string SourceQuote { get; set; } = "";
}

public sealed record RecruiterMessageWire : IValidatable
{
    [JsonRequired] public string Hook { get; set; } = "";

    [JsonRequired] public List<string> Needs { get; set; } = [];

    [JsonRequired] public List<RecruiterMessageWireFactsItem> Facts { get; set; } = [];

    [JsonRequired] public string Greeting { get; set; } = "";

    [JsonRequired] public List<string> Paragraphs { get; set; } = [];

    [JsonRequired] public string Closing { get; set; } = "";

    public RecruiterMessage ToDomain() => WireMapping.Map(this, GeneratedWireJsonContext.Default.RecruiterMessageWire, CareerJsonContext.Default.RecruiterMessage);

    public string? Validate() => WireMapping.Validate(ToDomain());
}

public sealed record MessageCheckWireClaimsItem
{
    [JsonRequired] public string Claim { get; set; } = "";

    [JsonRequired, WireChoices("experience", "logistics")] public string Kind { get; set; } = "";

    [JsonRequired] public bool Supported { get; set; }

    [JsonRequired] public string Quote { get; set; } = "";
}

public sealed record MessageCheckWireVerdictChecksItem
{
    [JsonRequired, WireChoices("main_need", "best_case", "result", "clarity", "tone", "next_step", "brevity")] public string Id { get; set; } = "";

    [JsonRequired] public bool Passed { get; set; }

    [JsonRequired] public string Note { get; set; } = "";
}

public sealed record MessageCheckWireVerdict
{
    [JsonRequired] public List<MessageCheckWireVerdictChecksItem> Checks { get; set; } = [];

    [JsonRequired] public int Score { get; set; }

    [JsonRequired] public bool WouldReply { get; set; }

    [JsonRequired] public string Weakest { get; set; } = "";

    [JsonRequired] public string Fix { get; set; } = "";
}

public sealed record MessageCheckWire : IValidatable
{
    [JsonRequired] public List<MessageCheckWireClaimsItem> Claims { get; set; } = [];

    [JsonRequired] public int ProjectCases { get; set; }

    [JsonRequired] public bool Natural { get; set; }

    [JsonRequired] public bool HasReason { get; set; }

    [JsonRequired] public string Note { get; set; } = "";

    [JsonRequired] public MessageCheckWireVerdict Verdict { get; set; } = new();

    public MessageCheck ToDomain() => WireMapping.Map(this, GeneratedWireJsonContext.Default.MessageCheckWire, CareerJsonContext.Default.MessageCheck);

    public string? Validate() => WireMapping.Validate(ToDomain());
}

public sealed record RejectionReviewWireReasonsItem
{
    [JsonRequired] public string Reason { get; set; } = "";

    [JsonRequired] public string Evidence { get; set; } = "";
}

public sealed record RejectionReviewWire : IValidatable
{
    [JsonRequired] public string Summary { get; set; } = "";

    [JsonRequired] public List<RejectionReviewWireReasonsItem> Reasons { get; set; } = [];

    [JsonRequired] public List<string> ResumeFixes { get; set; } = [];

    [JsonRequired] public List<string> KeywordsToAdd { get; set; } = [];

    [JsonRequired] public List<string> SearchFixes { get; set; } = [];

    [JsonRequired] public List<string> Learn { get; set; } = [];

    public RejectionReview ToDomain() => WireMapping.Map(this, GeneratedWireJsonContext.Default.RejectionReviewWire, CareerJsonContext.Default.RejectionReview);

    public string? Validate() => WireMapping.Validate(ToDomain());
}

public sealed record HhResumeAdviceWire : IValidatable
{
    [JsonRequired] public string Title { get; set; } = "";

    [JsonRequired] public string About { get; set; } = "";

    [JsonRequired] public List<string> SkillsToAdd { get; set; } = [];

    [JsonRequired] public List<string> Improvements { get; set; } = [];

    [JsonRequired] public List<string> MarketGaps { get; set; } = [];

    public HhResumeAdvice ToDomain() => WireMapping.Map(this, GeneratedWireJsonContext.Default.HhResumeAdviceWire, CareerJsonContext.Default.HhResumeAdvice);

    public string? Validate() => WireMapping.Validate(ToDomain());
}
