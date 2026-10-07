namespace JobFinder.Core.Domain;

// Every enum below is persisted as a string (see JobFinderDbContext): renaming a member is a data migration, adding one is free.

/// <summary>Where a vacancy or a post came from. hh.ru and rabota.by share one engine and one id space, so they are one source.</summary>
public enum SourceKind
{
    HeadHunter,
    Habr,
    Telegram,
    Threads,
}

public enum VacancyStatus
{
    /// <summary>Ingested, waiting for the hard filter and the LLM evaluation.</summary>
    New,

    /// <summary>Cut by a deterministic rule (<see cref="Vacancy.RejectReason"/>), never sent to the LLM.</summary>
    Rejected,

    /// <summary>Evaluated; <see cref="Vacancy.Decision"/> says what happens next.</summary>
    Scored,

    /// <summary>An application was sent (automatically or after approval).</summary>
    Applied,

    /// <summary>The user skipped it (bot or dashboard).</summary>
    Dismissed,

    /// <summary>Processing failed <see cref="Vacancy.Attempts"/> times; the dashboard can requeue it.</summary>
    Failed,
}

public enum Decision
{
    None,
    AutoApply,
    Approval,
    Ignore,
}

[Flags]
public enum WorkFormat
{
    None = 0,
    Office = 1,
    Hybrid = 2,
    Remote = 4,
}

/// <summary>CEFR scale plus the two answers a vacancy can give instead of a level. Order matters: comparisons use it.</summary>
public enum EnglishLevel
{
    Unknown = 0,
    NotRequired = 1,
    A1 = 2,
    A2 = 3,
    B1 = 4,
    B2 = 5,
    C1 = 6,
    C2 = 7,
    Native = 8,
}

public enum ApplicationChannel
{
    /// <summary>hh.ru / rabota.by response form, driven by the browser.</summary>
    HeadHunter,

    /// <summary>A direct message to the recruiter's Telegram, sent from the user's account, with the resume attached.</summary>
    TelegramDm,

    /// <summary>An e-mail to the address in the vacancy, with the resume attached.</summary>
    Email,

    /// <summary>The user applies by hand (Habr, a form, an e-mail); the system only tracks it.</summary>
    Manual,
}

public enum ApplicationMode
{
    Auto,
    Approved,

    /// <summary>Made by the user on the site; imported from the hh "Отклики" page.</summary>
    External,
}

public enum ApplicationStatus
{
    Pending,
    Sending,
    Sent,
    Failed,

    /// <summary>The employer requires something a bot must not fill (a test, a questionnaire): the user finishes it by hand.</summary>
    NeedsManual,
    Cancelled,

    /// <summary>
    /// Checked under dry-run, nothing pressed: held (and counted in the day's auto limit) until dry-run is off, then queued again —
    /// a test run must not use up the application.
    /// </summary>
    DryRun,

    OutcomeUnknown,
}

public enum RawPostStatus
{
    New,

    /// <summary>The regex prefilter found no .NET and vacancy markers: never sent to the LLM.</summary>
    Skipped,

    /// <summary>The LLM said it is not a vacancy, or not a .NET one.</summary>
    NotVacancy,

    /// <summary>Became (or was merged into) a vacancy: <see cref="RawPost.VacancyId"/>.</summary>
    Vacancy,
    Failed,
}

public enum AudienceKind
{
    Other,
    Recruiter,
    HiringManager,
    Tech,
    Hiring,
}

public enum CommentStatus
{
    Pending,
    Approved,
    Posted,
    Rejected,
    Failed,
    Expired,
    Sending,
    OutcomeUnknown,
}

public enum ChannelKind
{
    Channel,
    Group,
}

public enum ChannelStatus
{
    /// <summary>Suggested (seed file or discovery), waiting for the user's approval: the account does not join it.</summary>
    Proposed,
    Approved,
    Joined,
    Rejected,
    Failed,
}

public enum NotificationKind
{
    Info,
    Alert,
    VacancyApproval,
    AutoApplied,
    ApplyFailed,

    /// <summary>A response checked under dry-run and held: not a failure.</summary>
    ApplyDryRun,
    CommentDraft,
    ChannelsProposed,
    Digest,
    EditPrompt,
}

public enum NotificationStatus
{
    Pending,
    Sent,
    Failed,
    Sending,
    OutcomeUnknown,
    Reconciled,
}

public enum JobRunStatus
{
    Running,
    Ok,
    Failed,
}
