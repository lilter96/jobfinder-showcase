namespace JobFinder.Core.Domain;

/// <summary>An employer seen on hh.ru / rabota.by / Habr. Collected nightly from .NET searches (<c>CompanyCollectorJob</c>).</summary>
public sealed class Company
{
    public long Id { get; set; }

    public required string Name { get; set; }

    /// <summary>hh employer id (shared by hh.ru and rabota.by).</summary>
    public long? HhId { get; set; }

    public string? HabrAlias { get; set; }

    public string? Site { get; set; }

    public string? Area { get; set; }

    public string? SizeCategory { get; set; }

    public List<string> Industries { get; set; } = [];

    /// <summary>Plain text company description.</summary>
    public string? Description { get; set; }

    public decimal? Rating { get; set; }

    public bool ItAccredited { get; set; }

    /// <summary>All open vacancies of the employer (hh page counter).</summary>
    public int? OpenVacancies { get; set; }

    /// <summary>Open .NET vacancies seen in the last collector run.</summary>
    public int DotnetVacancies { get; set; }

    /// <summary>Configured keywords found in the description or industries (<c>Companies:Tags</c>).</summary>
    public List<string> Tags { get; set; } = [];

    public bool Watch { get; set; }

    public bool Blacklisted { get; set; }

    public long PreferencesRevision { get; set; }

    public string? Note { get; set; }

    public DateTimeOffset FirstSeenAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public DateTimeOffset? DetailsFetchedAt { get; set; }
}

public sealed class Application
{
    public long Id { get; set; }

    public Guid? DeliveryRequestId { get; set; }

    public Guid OperationId { get; set; } = Guid.NewGuid();

    public long Revision { get; set; }

    public long VacancyId { get; set; }

    public Vacancy? Vacancy { get; set; }

    public ApplicationChannel Channel { get; set; }

    public ApplicationMode Mode { get; set; }

    public ApplicationStatus Status { get; set; }

    public string? CoverLetter { get; set; }

    /// <summary>Telegram DM target (@username) when <see cref="Channel"/> is <see cref="ApplicationChannel.TelegramDm"/>.</summary>
    public string? Target { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset? SentAt { get; set; }

    public int Attempts { get; set; }

    public string? Error { get; set; }

    public string? ScreenshotPath { get; set; }

    /// <summary>Funnel position once sent (null before).</summary>
    public ApplicationStage? Stage { get; set; }

    public DateTimeOffset? StageChangedAt { get; set; }

    public DateTimeOffset? LastInboundAt { get; set; }

    public DateTimeOffset? LastOutboundAt { get; set; }

    public DateTimeOffset? FollowUpSentAt { get; set; }

    /// <summary>Telegram user id of <see cref="Target"/>: replies are matched by it.</summary>
    public long? TargetPeerId { get; set; }

    /// <summary>Message-ID of the sent e-mail: replies are matched by In-Reply-To / References.</summary>
    public string? EmailMessageId { get; set; }

    public long? TailoredResumeId { get; set; }

    public List<ApplicationEvent> Events { get; set; } = [];
}

/// <summary>A Telegram message or a Threads post before the LLM decided whether it is a vacancy.</summary>
public sealed class RawPost
{
    public long Id { get; set; }

    public Guid? ProcessingRequestId { get; set; }

    public long ProcessingRevision { get; set; }

    public SourceKind Source { get; set; }

    /// <summary>Telegram: channel/chat id; Threads: author username.</summary>
    public required string ChatRef { get; set; }

    public string? ChatTitle { get; set; }

    public required string ExternalId { get; set; }

    public string? Url { get; set; }

    public string? Author { get; set; }

    public required string Text { get; set; }

    /// <summary>Links the text does not show (Telegram text links, URL buttons, mentions of people): where the details and contacts often are.</summary>
    public List<PostLink> Links { get; set; } = [];

    public DateTimeOffset? PublishedAt { get; set; }

    public DateTimeOffset FetchedAt { get; set; }

    public RawPostStatus Status { get; set; }

    public long? VacancyId { get; set; }

    public int Attempts { get; set; }

    public string? Error { get; set; }
}

/// <summary>A Threads comment prepared by the LLM. Nothing is published before the user picks a variant.</summary>
public sealed class CommentDraft
{
    public long Id { get; set; }

    public long Revision { get; set; }

    public Guid? SourceRequestId { get; set; }
    public Guid? DeliveryRequestId { get; set; }

    public required string PostId { get; set; }

    public required string PostUrl { get; set; }

    public required string Author { get; set; }

    public AudienceKind Audience { get; set; }

    public required string PostText { get; set; }

    public DateTimeOffset? PostPublishedAt { get; set; }

    public List<string> Variants { get; set; } = [];

    public string? ChosenText { get; set; }

    /// <summary>Why the LLM thinks this post is worth a comment.</summary>
    public string? Reason { get; set; }

    public CommentStatus Status { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset? DecidedAt { get; set; }

    public DateTimeOffset? PublishedAt { get; set; }

    public string? Error { get; set; }

    public int Attempts { get; set; }
}

public sealed class TelegramChannel
{
    public long Id { get; set; }
    public long DecisionRevision { get; set; }
    public Guid? MembershipRequestId { get; set; }

    /// <summary>Public @username without the at-sign; null for a private chat picked up from the "Jobs" folder.</summary>
    public string? Username { get; set; }

    public string? Title { get; set; }

    public ChannelKind Kind { get; set; }

    /// <summary>MTProto peer id (channel id without the -100 prefix).</summary>
    public long? PeerId { get; set; }

    public long? AccessHash { get; set; }

    public ChannelStatus Status { get; set; }

    /// <summary>Why it was proposed (topic, audience, posting rate) — shown on the approval page.</summary>
    public string? Reason { get; set; }

    public int LastMessageId { get; set; }

    public int PostsSeen { get; set; }

    public int VacanciesFound { get; set; }

    public DateTimeOffset AddedAt { get; set; }

    public DateTimeOffset? JoinedAt { get; set; }

    public string? Error { get; set; }
}

/// <summary>Bot outbox: a message is written in the same transaction as the state change it reports, and sent by the dispatcher.</summary>
public sealed class Notification
{
    public Guid? DeliveryRequestId { get; set; }
    public long Id { get; set; }

    public NotificationKind Kind { get; set; }

    /// <summary>Telegram HTML.</summary>
    public required string Text { get; set; }

    /// <summary>Inline keyboard rows: each button is "label|callback-data".</summary>
    public List<List<string>> Buttons { get; set; } = [];

    /// <summary>A file sent with the message (tailored resume, interview pack).</summary>
    public string? AttachmentPath { get; set; }

    public string? AttachmentName { get; set; }

    public long? EntityId { get; set; }

    public NotificationStatus Status { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset? SentAt { get; set; }

    public int? TelegramMessageId { get; set; }

    public int? EditKeyboardMessageId { get; set; }
    public long? EditParentNotificationId { get; set; }

    public int Attempts { get; set; }

    public string? Error { get; set; }
}

public sealed class AppSetting
{
    public required string Key { get; set; }

    public required string Value { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>A named JSON document (<see cref="Storage.IDocumentStore"/>): state that is one piece, not rows.</summary>
public sealed class AppDocument
{
    public required string Key { get; set; }

    public required string Content { get; set; }

    public long Version { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>The MTProto session of the user account, as WTelegramClient writes it (encrypted with the api_hash).</summary>
public sealed class TelegramSession
{
    public required string Name { get; set; }

    public required byte[] Data { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}

public sealed class JobRun
{
    public long Id { get; set; }

    public required string Job { get; set; }

    public DateTimeOffset StartedAt { get; set; }

    public bool? ManualTrigger { get; set; }

    public DateTimeOffset? ScheduledSlot { get; set; }

    public Guid? WeeklyReportRequestId { get; set; }

    public DateTimeOffset? FinishedAt { get; set; }

    public JobRunStatus Status { get; set; }

    public int Items { get; set; }

    public string? Message { get; set; }
}

public sealed class LlmCall
{
    public long Id { get; set; }

    public Guid? OperationId { get; set; }

    public Guid? CallId { get; set; }

    public int? AttemptNumber { get; set; }

    public string? RequestedModel { get; set; }

    public Artifacts.ArtifactRecipe? Recipe { get; set; }

    public required string Purpose { get; set; }

    public required string Model { get; set; }

    public int PromptTokens { get; set; }

    public int CompletionTokens { get; set; }

    public decimal CostUsd { get; set; }

    public int LatencyMs { get; set; }

    public bool Ok { get; set; }

    public string? Error { get; set; }

    public DateTimeOffset At { get; set; }
}
