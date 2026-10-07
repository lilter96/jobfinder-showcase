using System.Text.Json;
using System.Text.Json.Serialization;
using JobFinder.Llm.Client;

namespace JobFinder.Llm.Tasks;

// The interview copilot. Pass 1 (interview_plan) decides what matters for this interview and which question gets which real case;
// pass 2 (interview_answer) writes, per question, the speech the candidate says. The stored result (InterviewCopilot) joins both with
// the checks the code made. Records read back from JSON use { get; set; } (see JsonDefaultsTests).

/// <summary>A topic that matters for this interview, with why and how strong the candidate is on it.</summary>
public sealed record InterviewPriority
{
    public string Topic { get; set; } = "";

    /// <summary>critical / high / medium / low.</summary>
    public string Priority { get; set; } = "medium";

    public string Reason { get; set; } = "";

    /// <summary>Where it comes from: a line of the vacancy, a recruiter's question, the evaluation.</summary>
    public string Evidence { get; set; } = "";

    /// <summary>strong / adequate / gap / unknown.</summary>
    public string CandidateLevel { get; set; } = "unknown";

    /// <summary>confirmed (the vacancy or the conversation says so) / likely (follows from the requirements) / speculative.</summary>
    public string Basis { get; set; } = "likely";
}

/// <summary>The one real episode that proves what the question checks.</summary>
public sealed record StoryChoice
{
    /// <summary>An id of the evidence catalog: S1 (story), A1 (achievement), E1 (job).</summary>
    public string Ref { get; set; } = "";

    public string WhyRelevant { get; set; } = "";

    /// <summary>What the candidate personally did.</summary>
    public string Contribution { get; set; } = "";

    public string Result { get; set; } = "";

    /// <summary>Claims the answer may make because the source states them.</summary>
    public List<string> SupportedClaims { get; set; } = [];
}

public sealed record PlannedQuestion
{
    /// <summary>A short slug; about, why_company, why_role and salary are fixed.</summary>
    public string Id { get; set; } = "";

    public string Question { get; set; } = "";

    /// <summary>hr / motivation / technical / technical_depth / system_design / coding / behavioral / negotiation.</summary>
    public string Category { get; set; } = "technical";

    public string Priority { get; set; } = "medium";

    /// <summary>high / medium / low.</summary>
    public string Likelihood { get; set; } = "medium";

    public string Basis { get; set; } = "likely";

    /// <summary>Why the question is on the list.</summary>
    public string Source { get; set; } = "";

    /// <summary>What the interviewer checks with it.</summary>
    public string Tests { get; set; } = "";

    /// <summary>How dangerous it is to answer unprepared: high / medium / low.</summary>
    public string Risk { get; set; } = "medium";

    public string CandidateLevel { get; set; } = "unknown";

    /// <summary>Evidence catalog ids the answer rests on.</summary>
    public List<string> Evidence { get; set; } = [];

    public StoryChoice? Story { get; set; }
}

/// <summary>A topic the candidate is weak or unproven on: what is known nearby, what to learn, how to talk about it.</summary>
public sealed record TopicGap
{
    public string Topic { get; set; } = "";

    /// <summary>gap / unknown / adequate.</summary>
    public string Level { get; set; } = "gap";

    /// <summary>The nearest confirmed experience.</summary>
    public string Known { get; set; } = "";

    /// <summary>What to go over before the interview, a few concrete points.</summary>
    public List<string> Learn { get; set; } = [];

    /// <summary>What to say, as speech.</summary>
    public string SafeWording { get; set; } = "";

    public List<string> DoNotSay { get; set; } = [];
}

public sealed record AskItem
{
    public string Question { get; set; } = "";

    public string Why { get; set; } = "";
}

public sealed record EmployerQuestions
{
    public List<AskItem> MustAsk { get; set; } = [];

    public List<AskItem> NiceToAsk { get; set; } = [];
}

public sealed record NegotiationPlan
{
    /// <summary>The position: expectations, the market, level, when to name a number.</summary>
    public string Stance { get; set; } = "";

    public string Market { get; set; } = "";

    /// <summary>Conditions to settle: format, location, employment, level.</summary>
    public List<string> Conditions { get; set; } = [];
}

public sealed record InterviewRisk
{
    public string Risk { get; set; } = "";

    /// <summary>high / medium / low.</summary>
    public string Severity { get; set; } = "medium";

    public string Mitigation { get; set; } = "";
}

/// <summary>Supporting data from the research: the page shows it folded, the plan uses it.</summary>
public sealed record CompanyNotes
{
    public string Brief { get; set; } = "";

    public List<string> WhatTheyLookFor { get; set; } = [];

    /// <summary>Claims from the model's own knowledge, not from the sources.</summary>
    public List<string> ToVerify { get; set; } = [];

    public List<string> SystemDesignThemes { get; set; } = [];

    public List<string> CodingThemes { get; set; } = [];
}

/// <summary>Pass 1: what matters for this interview, the question shortlist with real cases, gaps, questions to ask, money, risks.</summary>
public sealed record InterviewPlan : IValidatable
{
    public string Role { get; set; } = "";

    /// <summary>low / medium / high.</summary>
    public string OverallRisk { get; set; } = "medium";

    public List<InterviewPriority> Priorities { get; set; } = [];

    public List<PlannedQuestion> Questions { get; set; } = [];

    public List<TopicGap> Gaps { get; set; } = [];

    public EmployerQuestions EmployerQuestions { get; set; } = new();

    public NegotiationPlan Negotiation { get; set; } = new();

    public List<InterviewRisk> Risks { get; set; } = [];

    public CompanyNotes Company { get; set; } = new();

    public string? Validate() => Questions.Count(q => !string.IsNullOrWhiteSpace(q.Question)) < 6 ? "fewer than 6 questions" : null;
}

public sealed record DoNotSay
{
    /// <summary>The dangerous wording.</summary>
    public string Say { get; set; } = "";

    /// <summary>What to say instead.</summary>
    public string Instead { get; set; } = "";
}

public sealed record FollowUpAnswer
{
    public string Question { get; set; } = "";

    public string Start { get; set; } = "";

    public string Core { get; set; } = "";

    public string? Extended { get; set; }
}

/// <summary>Pass 2: the speech for one question, from a 15-second start to a 60-second version, and where the talk goes next.</summary>
public sealed record SpokenAnswer : IValidatable
{
    /// <summary>10–15 seconds: the direct answer, the way in.</summary>
    public string Start { get; set; } = "";

    /// <summary>25–35 seconds: what happened, what the candidate did, why, the result.</summary>
    public string Core { get; set; } = "";

    /// <summary>45–60 seconds: trade-offs, details, limits, personal part; null for a simple question.</summary>
    public string? Extended { get; set; }

    /// <summary>Where the interviewer may dig, what to know there, what to be careful with.</summary>
    public string DeepDive { get; set; } = "";

    public List<DoNotSay> DoNotSay { get; set; } = [];

    public List<FollowUpAnswer> FollowUps { get; set; } = [];

    /// <summary>Concrete claims of the answer that the sources do not state literally.</summary>
    public List<string> Verify { get; set; } = [];

    public bool RequiresApproval { get; set; }

    public string? ApprovalReason { get; set; }

    /// <summary>
    /// verified (the sources say it), derived (safely inferred from several sources), personal_unknown (depends on the candidate's own
    /// position, which no source gives: no speech, <see cref="PositionOptions"/> instead), unsupported (not enough data).
    /// </summary>
    public string EvidenceLevel { get; set; } = "derived";

    /// <summary>For personal_unknown: 2-4 honest positions the candidate picks from; the pick becomes a fact and the answer is written.</summary>
    public List<string> PositionOptions { get; set; } = [];

    /// <summary>Fact keys the answer relies on: a change of one of them rebuilds this answer, the others are reused.</summary>
    public List<string> UsedFacts { get; set; } = [];

    public bool NeedsPosition => EvidenceLevel == "personal_unknown";

    public string? Validate() =>
        !NeedsPosition && (string.IsNullOrWhiteSpace(Start) || string.IsNullOrWhiteSpace(Core)) ? "empty start or core"
        : NeedsPosition && PositionOptions.Count == 0 ? "personal_unknown without position options"
        : null;
}

/// <summary>One question of the preparation with its speech and what the code found.</summary>
public sealed record PrepItem
{
    public PlannedQuestion Q { get; set; } = new();

    public SpokenAnswer? A { get; set; }

    /// <summary>Machine-enforced: the user checks the answer before relying on it.</summary>
    public bool RequiresApproval { get; set; }

    public List<string> ApprovalReasons { get; set; } = [];

    /// <summary>What the speech check still finds after the rewrite.</summary>
    public List<string> StyleProblems { get; set; } = [];

    /// <summary>
    /// blocker (the user must decide: a position, depth, money, a gap), review (inferred details to glance at), none. The page shows a
    /// blocker loudly and a review folded.
    /// </summary>
    public string CheckLevel { get; set; } = "none";

    /// <summary>
    /// Hash of everything the answer was generated from (question, prompt, route, profile, evidence, the facts it depends on): the same
    /// fingerprint on the next build reuses the answer instead of asking the model again.
    /// </summary>
    public string? Fingerprint { get; set; }

    /// <summary>Fact keys the fingerprint covers (declared by the bank plus those the answer used).</summary>
    public List<string> Deps { get; set; } = [];

    /// <summary>The LLM route (reasoning tier) the answer was written on.</summary>
    public string? Purpose { get; set; }

    /// <summary>Taken from the previous build unchanged (nothing it depends on changed).</summary>
    public bool Reused { get; set; }

    public DateTimeOffset? GeneratedAt { get; set; }

    /// <summary>A piece of speech to say, learn and rehearse (a question waiting for the candidate's position has none).</summary>
    public bool HasSpeech => A is { NeedsPosition: false } a && !string.IsNullOrWhiteSpace(a.Start);
}

public sealed record LastMinuteStory
{
    public string Ref { get; set; } = "";

    public string Title { get; set; } = "";

    public string Line { get; set; } = "";
}

public sealed record LastMinuteLine
{
    public string Topic { get; set; } = "";

    public string Line { get; set; } = "";
}

/// <summary>The screen for the last ten minutes, assembled by code from the answers (nothing new is generated for it).</summary>
public sealed record LastMinuteBrief
{
    public string About { get; set; } = "";

    public string WhyCompany { get; set; } = "";

    public string WhyRole { get; set; } = "";

    public List<LastMinuteStory> TopStories { get; set; } = [];

    public List<string> TopRisks { get; set; } = [];

    public List<LastMinuteLine> TopTopics { get; set; } = [];

    public List<LastMinuteLine> FollowUps { get; set; } = [];

    public List<DoNotSay> DoNotSay { get; set; } = [];
}

/// <summary>The stored preparation (<c>interviews.pack_json</c>).</summary>
public sealed record InterviewCopilot
{
    public const int CurrentVersion = 2;

    /// <summary>0 = a pack of the old research format: shown as "rebuild".</summary>
    public int Version { get; set; }

    public string Role { get; set; } = "";

    public string OverallRisk { get; set; } = "medium";

    public List<InterviewPriority> Priorities { get; set; } = [];

    public List<PrepItem> Preparation { get; set; } = [];

    /// <summary>"What are your salary expectations", kept apart from the preparation: it is negotiation.</summary>
    public PrepItem? Salary { get; set; }

    public List<TopicGap> Gaps { get; set; } = [];

    public EmployerQuestions EmployerQuestions { get; set; } = new();

    public NegotiationPlan Negotiation { get; set; } = new();

    public List<InterviewRisk> Risks { get; set; } = [];

    public CompanyNotes Company { get; set; } = new();

    public LastMinuteBrief LastMinute { get; set; } = new();

    /// <summary>The evidence catalog the answers cite: id → text.</summary>
    public Dictionary<string, string> Evidence { get; set; } = [];

    public DateTimeOffset GeneratedAt { get; set; }

    public string? Model { get; set; }

    public decimal CostUsd { get; set; }
}

/// <summary>One question of the HR bank (<c>prompts/hr_bank.json</c>): what it checks and how to answer it well and honestly.</summary>
public sealed record HrQuestion
{
    public string Id { get; set; } = "";

    public string Category { get; set; } = "";

    /// <summary>всегда / часто / редко.</summary>
    public string Frequency { get; set; } = "часто";

    /// <summary>The answer depends on the company: every interview preparation writes its own; the HR document keeps a general one.</summary>
    public bool Tailored { get; set; }

    /// <summary>A personal question (family, loans): a calm boundary, always the user's call.</summary>
    public bool Personal { get; set; }

    public string Question { get; set; } = "";

    public string Why { get; set; } = "";

    public string Approach { get; set; } = "";

    public string Avoid { get; set; } = "";

    /// <summary>Facts (profile/facts.json keys) the answer needs; "funnel" = the real state of the search.</summary>
    public List<string> Facts { get; set; } = [];

    /// <summary>low / medium / high / max; empty = by category (formalities low, behavior medium).</summary>
    public string? Effort { get; set; }

    /// <summary>Positions to pick when the fact is missing (overtime, trips); empty = free text.</summary>
    public List<string> Options { get; set; } = [];
}

public sealed record HrBank
{
    public List<HrQuestion> Questions { get; set; } = [];
}

public sealed record HrPlaybookItem
{
    public HrQuestion Bank { get; set; } = new();

    public PrepItem Item { get; set; } = new();
}

/// <summary>The HR document: every question of the bank answered once from the profile, facts and stories (<c>settings</c>, <c>hr.playbook</c>).</summary>
public sealed record HrPlaybook
{
    public const int CurrentVersion = 1;

    public int Version { get; set; }

    public List<HrPlaybookItem> Items { get; set; } = [];

    public DateTimeOffset GeneratedAt { get; set; }

    public string? Model { get; set; }

    public decimal CostUsd { get; set; }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true,
    NumberHandling = JsonNumberHandling.AllowReadingFromString, AllowTrailingCommas = true, ReadCommentHandling = JsonCommentHandling.Skip)]
[JsonSerializable(typeof(HrBank))]
[JsonSerializable(typeof(HrPlaybook))]
[JsonSerializable(typeof(InterviewPlan))]
[JsonSerializable(typeof(SpokenAnswer))]
[JsonSerializable(typeof(PlannedQuestion))]
[JsonSerializable(typeof(PrepItem))]
[JsonSerializable(typeof(InterviewCopilot))]
[JsonSerializable(typeof(Dictionary<string, string>))]
public sealed partial class InterviewJsonContext : JsonSerializerContext;

internal static class InterviewSchemas
{
    public static JsonSchemaSpec Plan => Contracts.NativeWireSchemas.InterviewPlan;

    public static JsonSchemaSpec Answer => Contracts.NativeWireSchemas.SpokenAnswer;
}
