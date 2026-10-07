namespace JobFinder.Core.Domain;

/// <summary>
/// One job opening, merged across sources (an hh vacancy reposted in three Telegram channels is ONE row with four
/// <see cref="VacancySource"/> rows). The status machine is driven by the pipeline workers; every field a rule or a prompt
/// needs is normalized here, the raw source payload stays in <see cref="VacancySource.RawJson"/>.
/// </summary>
public sealed class Vacancy
{
    public long Id { get; set; }

    public required string Title { get; set; }

    public string? CompanyName { get; set; }

    public long? CompanyId { get; set; }

    public Company? Company { get; set; }

    /// <summary>Plain text (HTML stripped), capped by the ingestor.</summary>
    public string Description { get; set; } = "";

    public decimal? SalaryFrom { get; set; }

    public decimal? SalaryTo { get; set; }

    /// <summary>ISO-ish code as the source gives it, normalized to RUB/BYN/USD/EUR by <see cref="Text.Currency"/>.</summary>
    public string? Currency { get; set; }

    /// <summary>true = before tax; null = the source did not say.</summary>
    public bool? SalaryGross { get; set; }

    public string? City { get; set; }

    /// <summary>"BY", "RU" or another ISO code; null when unknown.</summary>
    public string? CountryCode { get; set; }

    public WorkFormat WorkFormats { get; set; }

    public EnglishLevel EnglishRequired { get; set; }

    public List<string> Skills { get; set; } = [];

    /// <summary>Source wording of the experience requirement ("between3And6", "Senior", "5+ лет").</summary>
    public string? Experience { get; set; }

    /// <summary>
    /// Ways to reach the employer, normalized (see <see cref="Contacts.ContactExtractor"/>): from the post, its hidden links and buttons, and
    /// the pages it links to. The order is the source's: the first Telegram of the list is the first the post named.
    /// </summary>
    public List<VacancyContact> Contacts { get; set; } = [];

    /// <summary>The best link to open: the hh page if one of the sources is hh, else the first source.</summary>
    public required string Url { get; set; }

    public DateTimeOffset? PublishedAt { get; set; }

    /// <summary>When the employer first created it (hh "creationTime"; republishing moves <see cref="PublishedAt"/>, not this).</summary>
    public DateTimeOffset? OriginalPostedAt { get; set; }

    /// <summary>Applications the vacancy already has (hh): competition, and a ghost-job signal when huge and old.</summary>
    public int? ResponsesCount { get; set; }

    public DateTimeOffset FirstSeenAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    /// <summary>Company + title key used to merge the same opening from different sources (<see cref="Text.VacancyFingerprint"/>).</summary>
    public required string Fingerprint { get; set; }

    public VacancyStatus Status { get; set; }

    public string? RejectReason { get; set; }

    /// <summary>Final score: <see cref="LlmScore"/> + <see cref="SalaryAdjustment"/>, clamped to 0..100.</summary>
    public int? Score { get; set; }

    public int? LlmScore { get; set; }

    public int SalaryAdjustment { get; set; }

    public VacancyEvaluation? Evaluation { get; set; }

    public Decision Decision { get; set; }

    public Legitimacy Legitimacy { get; set; }

    public string? CoverLetter { get; set; }

    public long LetterRevision { get; set; }

    public ApplicationLetterReview? LetterReview { get; set; }

    /// <summary>The approval card (or the auto-apply report) was queued for the bot: never twice.</summary>
    public bool Notified { get; set; }

    public int Attempts { get; set; }

    public long AssessmentRevision { get; set; }
    public Guid? AssessmentCaptureId { get; set; }

    public string? LastError { get; set; }

    public DateTimeOffset? ScoredAt { get; set; }

    public List<VacancySource> Sources { get; set; } = [];

    public List<Application> Applications { get; set; } = [];

    public List<TailoredResume> TailoredResumes { get; set; } = [];
}

public sealed class VacancySource
{
    public long Id { get; set; }

    public long VacancyId { get; set; }

    public Vacancy? Vacancy { get; set; }

    public SourceKind Source { get; set; }

    /// <summary>hh vacancy id, Habr vacancy id, "{chat}/{messageId}" for Telegram, the media id for Threads.</summary>
    public required string ExternalId { get; set; }

    /// <summary>hh.ru or rabota.by for HeadHunter (the site the vacancy was found on), t.me channel for Telegram.</summary>
    public string? Host { get; set; }

    public required string Url { get; set; }

    /// <summary>hh: the employer requires a cover letter.</summary>
    public bool LetterRequired { get; set; }

    /// <summary>hh: the employer attached a test/questionnaire — a bot must not answer it, the application becomes manual.</summary>
    public bool HasEmployerTest { get; set; }

    /// <summary>The source payload as fetched (search item + detail for hh, the post for Telegram), for debugging and re-parsing.</summary>
    public string? RawJson { get; set; }

    public DateTimeOffset FetchedAt { get; set; }
}

/// <summary>LLM verdict, stored as jsonb. Everything the dashboard and the bot card show about "why this score".</summary>
public sealed record VacancyEvaluation
{
    public int Score { get; set; }

    public int StackMatch { get; set; }

    public int SeniorityMatch { get; set; }

    public int DomainMatch { get; set; }

    public int FormatMatch { get; set; }

    public string EnglishRequired { get; set; } = "unknown";

    public List<string> Strengths { get; set; } = [];

    public List<string> Gaps { get; set; } = [];

    public List<string> RedFlags { get; set; } = [];

    public string Summary { get; set; } = "";

    /// <summary>The model that produced it (after rotation), for auditing a strange score.</summary>
    public string? Model { get; set; }

    /// <summary>Human-readable explanation of <see cref="Vacancy.SalaryAdjustment"/>.</summary>
    public string? SalaryNote { get; set; }

    /// <summary>Hard skills and terms of the vacancy, in its own spelling: the ATS keywords the tailored resume must cover.</summary>
    public List<string> Keywords { get; set; } = [];

    /// <summary>"ru" or "en": the language of the vacancy text (the tailored resume is written in it).</summary>
    public string Language { get; set; } = "ru";

    /// <summary>Why the vacancy looks like a ghost job or a scam (rules and the model together).</summary>
    public List<string> LegitimacyReasons { get; set; } = [];

    /// <summary>The offer against the market collected by the system ("медиана Senior .NET удалёнка РФ $3 400, эта вилка — p70").</summary>
    public string? MarketNote { get; set; }
}
