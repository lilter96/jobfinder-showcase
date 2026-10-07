namespace JobFinder.Core.Domain;

/// <summary>Where an application is in the hiring funnel (after it was sent). Terminal: Accepted, Rejected, NoResponse, Withdrawn.</summary>
public enum ApplicationStage
{
    Sent,
    Viewed,

    /// <summary>The employer wrote something that is neither an invitation nor a rejection (a question, "send your salary").</summary>
    Replied,
    HrScreen,
    TechInterview,
    Final,
    Offer,
    Accepted,
    Rejected,
    NoResponse,
    Withdrawn,
}

public enum Legitimacy
{
    Unknown,
    Ok,

    /// <summary>Signals of a ghost job or a doubtful employer: marked, never auto-applied.</summary>
    Suspicious,

    /// <summary>Clear scam markers: rejected.</summary>
    Scam,
}

public enum EventKind
{
    StageChanged,
    MessageIn,
    MessageOut,
    FollowUp,
    Note,
}

public enum EventChannel
{
    System,
    HeadHunter,
    Telegram,
    Email,
    Manual,
}

/// <summary>How the system read an inbound message.</summary>
public enum ReplyClass
{
    Other,
    Invitation,
    Question,
    Rejection,
    Offer,
    Viewed,
}

public enum DraftStatus
{
    None,
    Pending,
    Sent,
    Dismissed,
    Sending,
    OutcomeUnknown,
}

public enum InterviewKind
{
    HrScreen,
    Technical,
    SystemDesign,
    Coding,
    Final,
    Other,
}

public enum InterviewStatus
{
    Scheduled,
    Done,
    Cancelled,
}

public enum StorySource
{
    Resume,
    Interview,
    Debrief,
}

public enum ContactKind
{
    Telegram,
    Email,
    LinkedIn,
    Phone,
    WhatsApp,

    /// <summary>Not a person: a page or a form that takes the response (an ATS page, a Google form, a bot that hands out the contact).</summary>
    Link,
}

public enum OutreachStatus
{
    Draft,
    Sent,
    Dismissed,
    Sending,
    OutcomeUnknown,
}

public enum MockStatus
{
    Active,
    Finished,
    Abandoned,
}

/// <summary>One thing that happened to an application: a stage change, a message in or out, a follow-up, a note.</summary>
public sealed class ApplicationEvent
{
    public long Id { get; set; }

    public long Revision { get; set; }

    public long ApplicationId { get; set; }

    public Application? Application { get; set; }

    public DateTimeOffset At { get; set; }

    public EventKind Kind { get; set; }

    public EventChannel Channel { get; set; }

    public ApplicationStage? Stage { get; set; }

    public string? Author { get; set; }

    public string? Text { get; set; }

    /// <summary>Message id in its channel (Telegram message id, e-mail Message-ID, hh message id): inbound dedup.</summary>
    public string? ExternalId { get; set; }

    public ReplyClass? Classification { get; set; }

    /// <summary>Suggested answer to an inbound message.</summary>
    public string? Draft { get; set; }

    public DraftStatus DraftStatus { get; set; }
}

/// <summary>A resume rewritten for one vacancy, validated against the source facts and rendered to PDF.</summary>
public sealed class TailoredResume
{
    public long Id { get; set; }

    public long VacancyId { get; set; }

    public Vacancy? Vacancy { get; set; }

    public required string Language { get; set; }

    /// <summary>The structured content (jsonb) the PDF was rendered from.</summary>
    public required string ContentJson { get; set; }

    public required string PdfPath { get; set; }

    public required string FileName { get; set; }

    public List<string> KeywordsCovered { get; set; } = [];

    public List<string> KeywordsMissing { get; set; } = [];

    /// <summary>What the faithfulness gate removed or relabeled (bullets with unverified numbers, skills moved to "familiar").</summary>
    public List<string> Corrections { get; set; } = [];

    /// <summary>
    /// The plan, the recruiter review, the page checks (<see cref="Resume.ResumeReport"/>, jsonb); null for resumes made before them.
    /// </summary>
    public string? ReportJson { get; set; }

    public string? Model { get; set; }

    public decimal CostUsd { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>A STAR+Reflection story. Confirmed stories are facts: their numbers may appear in resumes and letters.</summary>
public sealed class Story
{
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public Guid? PreparationRequestId { get; set; }
    public long Id { get; set; }

    public long Revision { get; set; }
    public DateTimeOffset? RetiredAt { get; set; }
    public string? CreationKey { get; set; }
    public string? CreationHash { get; set; }

    public required string Title { get; set; }

    /// <summary>The resume achievement the story expands (the interview in the bot walks through them).</summary>
    public string? Achievement { get; set; }

    public string Situation { get; set; } = "";

    public string Task { get; set; } = "";

    public string Action { get; set; } = "";

    public string Result { get; set; } = "";

    public string Reflection { get; set; } = "";

    public List<string> Skills { get; set; } = [];

    public StorySource Source { get; set; }

    public bool Confirmed { get; set; }

    /// <summary>The user's raw answers the story was built from (provenance).</summary>
    public string? RawNotes { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}

public sealed class Interview
{
    public long Id { get; set; }

    public Guid? PreparationRequestId { get; set; }

    public long PreparationRevision { get; set; }

    public long ApplicationId { get; set; }

    public Application? Application { get; set; }

    public InterviewKind Kind { get; set; }

    public DateTimeOffset? ScheduledAt { get; set; }

    public int DurationMinutes { get; set; } = 60;

    public InterviewStatus Status { get; set; }

    /// <summary>The preparation pack (jsonb) and its PDF.</summary>
    public string? PackJson { get; set; }

    public string? PackPdfPath { get; set; }

    public bool Reminded { get; set; }

    public bool DebriefAsked { get; set; }

    public string? Debrief { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>A person or address to reach at a company, collected from posts, hh, Telegram search and LinkedIn.</summary>
public sealed class CompanyContact
{
    public long Id { get; set; }

    public long Revision { get; set; }

    public DateTimeOffset? RetiredAt { get; set; }

    public long? CompanyId { get; set; }

    /// <summary>Normalized company name (<c>VacancyFingerprint.NormalizeCompany</c>): links posts that name the company differently.</summary>
    public required string CompanyKey { get; set; }

    public string? CompanyName { get; set; }

    public ContactKind Kind { get; set; }

    /// <summary>@username, e-mail, profile URL, phone.</summary>
    public required string Value { get; set; }

    public string? PersonName { get; set; }

    /// <summary>Recruiter, HR, team lead, CTO… as the source put it.</summary>
    public string? Role { get; set; }

    public required string Source { get; set; }

    public string? SourceRef { get; set; }

    public int SeenCount { get; set; }

    public DateTimeOffset FirstSeenAt { get; set; }

    public DateTimeOffset LastSeenAt { get; set; }
}

/// <summary>A short message to a contact at a company, in addition to the application. Drafted; sent only by the user.</summary>
public sealed class Outreach
{
    public long Id { get; set; }

    public long Revision { get; set; }

    public bool? Fresh { get; set; }

    public long VacancyId { get; set; }

    public Vacancy? Vacancy { get; set; }

    public long ContactId { get; set; }

    public CompanyContact? Contact { get; set; }

    public required string Text { get; set; }

    public OutreachStatus Status { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset? SentAt { get; set; }

    public string? Error { get; set; }
}

public sealed class Offer
{
    public long Id { get; set; }
    public Guid? PublicationId { get; set; }
    public string? PublicationHash { get; set; }
    public string? InputJson { get; set; }

    public long? ApplicationId { get; set; }

    public Application? Application { get; set; }

    public required string Company { get; set; }

    public required string RawText { get; set; }

    public decimal? MonthlyAmount { get; set; }

    public string? Currency { get; set; }

    public bool? Gross { get; set; }

    /// <summary>Monthly net in USD, computed by code for comparisons.</summary>
    public decimal? MonthlyNetUsd { get; set; }

    /// <summary>The LLM analysis (jsonb): terms, risks, questions, counter-offer.</summary>
    public string? AnalysisJson { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>
/// A response on hh.ru / rabota.by as the applicant's "Отклики" page lists it, including the ones the user made by hand before the system
/// existed. It keeps a vacancy from being offered again (by hh id, or by company + title when a post names it without a link) even when
/// the vacancy itself was never collected.
/// </summary>
public sealed class HhResponse
{
    public long HhVacancyId { get; set; }

    /// <summary>The page's lastState, lower-cased: response, invitation, interview, discard, offer, hired…</summary>
    public required string State { get; set; }

    public string? VacancyName { get; set; }

    public string? CompanyName { get; set; }

    /// <summary><c>VacancyFingerprint.Compute(company, name)</c>: matches the same opening posted elsewhere.</summary>
    public string? Fingerprint { get; set; }

    public bool ViewedByEmployer { get; set; }

    public bool HasUnread { get; set; }

    public long? ChatId { get; set; }

    public DateTimeOffset? RespondedAt { get; set; }

    public DateTimeOffset? UpdatedAt { get; set; }

    public DateTimeOffset SyncedAt { get; set; }
}

/// <summary>A practice interview in the bot: questions asked one by one, each answer reviewed.</summary>
public sealed class MockSession
{
    public long Id { get; set; }
    public long Revision { get; set; }
    public Guid? PreparationRequestId { get; set; }

    public long? VacancyId { get; set; }

    public InterviewKind Kind { get; set; }

    public MockStatus Status { get; set; }

    /// <summary>jsonb: [{question, answer, score, feedback, ideal}].</summary>
    public string TurnsJson { get; set; } = "[]";

    public int Current { get; set; }

    public string? Summary { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset? FinishedAt { get; set; }
}

/// <summary>
/// A generated answer (or plan), stored by the fingerprint of its inputs: durable memoization of expensive LLM work (minutes of
/// reasoning, real money). Reused across builds, restarts and container re-creates while nothing it depends on changed: <see cref="Key"/>
/// covers the versioned static inputs (question, prompt, answer schema, route with model and reasoning effort, profile, stories);
/// <see cref="DepsHash"/> the facts it actually used, checked at read time, so a changed fact rebuilds only the answers that use it.
/// <see cref="CostUsd"/> × <see cref="Hits"/> is what the reuse saved.
/// </summary>
public sealed class GeneratedAnswer
{
    public Artifacts.ArtifactRecipe? Recipe { get; set; }

    /// <summary>"{scope}:{question}:{hash of the static inputs}".</summary>
    public required string Key { get; set; }

    /// <summary>hr / interview / interview-plan.</summary>
    public string Scope { get; set; } = "";

    public string QuestionId { get; set; } = "";

    public List<string> Deps { get; set; } = [];

    public string DepsHash { get; set; } = "";

    /// <summary>The stored JSON (a prepared answer, a plan).</summary>
    public required string Value { get; set; }

    public string? Model { get; set; }

    /// <summary>The LLM route, i.e. the reasoning tier.</summary>
    public string Purpose { get; set; } = "";

    /// <summary>Hash of the prompt template the answer was generated with.</summary>
    public string PromptVersion { get; set; } = "";

    /// <summary>What generating it cost (all attempts).</summary>
    public decimal CostUsd { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset LastUsedAt { get; set; }

    /// <summary>Times it was reused instead of generated again.</summary>
    public int Hits { get; set; }
}
