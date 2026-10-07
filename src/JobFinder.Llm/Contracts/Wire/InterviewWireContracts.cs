using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;
using JobFinder.Llm.Client;
using JobFinder.Llm.Tasks;

namespace JobFinder.Llm.Contracts.Wire;

public sealed record InterviewPlanWirePrioritiesItem
{
    [JsonRequired] public string Topic { get; set; } = "";

    [JsonRequired, WireChoices("critical", "high", "medium", "low")] public string Priority { get; set; } = "";

    [JsonRequired] public string Reason { get; set; } = "";

    [JsonRequired] public string Evidence { get; set; } = "";

    [JsonRequired, WireChoices("strong", "adequate", "gap", "unknown")] public string CandidateLevel { get; set; } = "";

    [JsonRequired, WireChoices("confirmed", "likely", "speculative")] public string Basis { get; set; } = "";
}

public sealed record InterviewPlanWireQuestionsItemStory
{
    [JsonRequired] public string Ref { get; set; } = "";

    [JsonRequired] public string WhyRelevant { get; set; } = "";

    [JsonRequired] public string Contribution { get; set; } = "";

    [JsonRequired] public string Result { get; set; } = "";

    [JsonRequired] public List<string> SupportedClaims { get; set; } = [];
}

public sealed record InterviewPlanWireQuestionsItem
{
    [JsonRequired] public string Id { get; set; } = "";

    [JsonRequired] public string Question { get; set; } = "";

    [JsonRequired, WireChoices("hr", "motivation", "technical", "technical_depth", "system_design", "coding", "behavioral", "negotiation")] public string Category { get; set; } = "";

    [JsonRequired, WireChoices("critical", "high", "medium", "low")] public string Priority { get; set; } = "";

    [JsonRequired, WireChoices("high", "medium", "low")] public string Likelihood { get; set; } = "";

    [JsonRequired, WireChoices("confirmed", "likely", "speculative")] public string Basis { get; set; } = "";

    [JsonRequired] public string Source { get; set; } = "";

    [JsonRequired] public string Tests { get; set; } = "";

    [JsonRequired, WireChoices("high", "medium", "low")] public string Risk { get; set; } = "";

    [JsonRequired, WireChoices("strong", "adequate", "gap", "unknown")] public string CandidateLevel { get; set; } = "";

    [JsonRequired] public List<string> Evidence { get; set; } = [];

    [JsonRequired] public InterviewPlanWireQuestionsItemStory? Story { get; set; }
}

public sealed record InterviewPlanWireGapsItem
{
    [JsonRequired] public string Topic { get; set; } = "";

    [JsonRequired, WireChoices("gap", "unknown", "adequate")] public string Level { get; set; } = "";

    [JsonRequired] public string Known { get; set; } = "";

    [JsonRequired] public List<string> Learn { get; set; } = [];

    [JsonRequired] public string SafeWording { get; set; } = "";

    [JsonRequired] public List<string> DoNotSay { get; set; } = [];
}

public sealed record InterviewPlanWireEmployerQuestionsMustAskItem
{
    [JsonRequired] public string Question { get; set; } = "";

    [JsonRequired] public string Why { get; set; } = "";
}

public sealed record InterviewPlanWireEmployerQuestionsNiceToAskItem
{
    [JsonRequired] public string Question { get; set; } = "";

    [JsonRequired] public string Why { get; set; } = "";
}

public sealed record InterviewPlanWireEmployerQuestions
{
    [JsonRequired] public List<InterviewPlanWireEmployerQuestionsMustAskItem> MustAsk { get; set; } = [];

    [JsonRequired] public List<InterviewPlanWireEmployerQuestionsNiceToAskItem> NiceToAsk { get; set; } = [];
}

public sealed record InterviewPlanWireNegotiation
{
    [JsonRequired] public string Stance { get; set; } = "";

    [JsonRequired] public string Market { get; set; } = "";

    [JsonRequired] public List<string> Conditions { get; set; } = [];
}

public sealed record InterviewPlanWireRisksItem
{
    [JsonRequired] public string Risk { get; set; } = "";

    [JsonRequired, WireChoices("high", "medium", "low")] public string Severity { get; set; } = "";

    [JsonRequired] public string Mitigation { get; set; } = "";
}

public sealed record InterviewPlanWireCompany
{
    [JsonRequired] public string Brief { get; set; } = "";

    [JsonRequired] public List<string> WhatTheyLookFor { get; set; } = [];

    [JsonRequired] public List<string> ToVerify { get; set; } = [];

    [JsonRequired] public List<string> SystemDesignThemes { get; set; } = [];

    [JsonRequired] public List<string> CodingThemes { get; set; } = [];
}

public sealed record InterviewPlanWire : IValidatable
{
    [JsonRequired] public string Role { get; set; } = "";

    [JsonRequired, WireChoices("high", "medium", "low")] public string OverallRisk { get; set; } = "";

    [JsonRequired] public List<InterviewPlanWirePrioritiesItem> Priorities { get; set; } = [];

    [JsonRequired] public List<InterviewPlanWireQuestionsItem> Questions { get; set; } = [];

    [JsonRequired] public List<InterviewPlanWireGapsItem> Gaps { get; set; } = [];

    [JsonRequired] public InterviewPlanWireEmployerQuestions EmployerQuestions { get; set; } = new();

    [JsonRequired] public InterviewPlanWireNegotiation Negotiation { get; set; } = new();

    [JsonRequired] public List<InterviewPlanWireRisksItem> Risks { get; set; } = [];

    [JsonRequired] public InterviewPlanWireCompany Company { get; set; } = new();

    public InterviewPlan ToDomain() => WireMapping.Map(this, GeneratedWireJsonContext.Default.InterviewPlanWire, InterviewJsonContext.Default.InterviewPlan);

    public string? Validate() => WireMapping.Validate(ToDomain());
}

public sealed record SpokenAnswerWireDoNotSayItem
{
    [JsonRequired] public string Say { get; set; } = "";

    [JsonRequired] public string Instead { get; set; } = "";
}

public sealed record SpokenAnswerWireFollowUpsItem
{
    [JsonRequired] public string Question { get; set; } = "";

    [JsonRequired] public string Start { get; set; } = "";

    [JsonRequired] public string Core { get; set; } = "";

    [JsonRequired] public string? Extended { get; set; }
}

public sealed record SpokenAnswerWire : IValidatable
{
    [JsonRequired] public string Start { get; set; } = "";

    [JsonRequired] public string Core { get; set; } = "";

    [JsonRequired] public string? Extended { get; set; }

    [JsonRequired] public string DeepDive { get; set; } = "";

    [JsonRequired] public List<SpokenAnswerWireDoNotSayItem> DoNotSay { get; set; } = [];

    [JsonRequired] public List<SpokenAnswerWireFollowUpsItem> FollowUps { get; set; } = [];

    [JsonRequired] public List<string> Verify { get; set; } = [];

    [JsonRequired] public bool RequiresApproval { get; set; }

    [JsonRequired] public string? ApprovalReason { get; set; }

    [JsonRequired, WireChoices("verified", "derived", "personal_unknown", "unsupported")] public string EvidenceLevel { get; set; } = "";

    [JsonRequired] public List<string> PositionOptions { get; set; } = [];

    [JsonRequired] public List<string> UsedFacts { get; set; } = [];

    public SpokenAnswer ToDomain() => WireMapping.Map(this, GeneratedWireJsonContext.Default.SpokenAnswerWire, InterviewJsonContext.Default.SpokenAnswer);

    public string? Validate() => WireMapping.Validate(ToDomain());
}
