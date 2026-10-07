namespace JobFinder.Core.Domain;

public enum ChannelMembershipStage
{
    Resolving, Joining, Muting, Archiving, Filing, ReadingHistory, Publishing, Completed, Failed, Cancelled, OutcomeUnknown, Reconciled,
}

public enum ChannelMembershipOutcome { Sending, Confirmed, KnownNotApplied, OutcomeUnknown, Reconciled, InviteRequested }

public sealed class ChannelMembershipRequest
{
    public Guid Id { get; set; }
    public long ChannelId { get; set; }
    public long DecisionRevision { get; set; }
    public ChannelMembershipStage Stage { get; set; }
    public required string InputJson { get; set; }
    public required string InputHash { get; set; }
    public long? AccountId { get; set; }
    public string? ChatJson { get; set; }
    public string? ChatHash { get; set; }
    public string? HistoryJson { get; set; }
    public string? HistoryHash { get; set; }
    public Guid? ClaimEnvelopeId { get; set; }
    public Guid? AttemptId { get; set; }
    public int SpacingSeconds { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public DateTimeOffset? RetryAt { get; set; }
    public string? Error { get; set; }
}

public sealed class ChannelMembershipAttempt
{
    public Guid Id { get; set; }
    public Guid RequestId { get; set; }
    public Guid EnvelopeId { get; set; }
    public long AccountId { get; set; }
    public long PeerId { get; set; }
    public required string InputHash { get; set; }
    public required string ChatHash { get; set; }
    public ChannelMembershipStage Stage { get; set; }
    public ChannelMembershipOutcome Outcome { get; set; }
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public string? Evidence { get; set; }
    public bool? CheckedApplied { get; set; }
}
