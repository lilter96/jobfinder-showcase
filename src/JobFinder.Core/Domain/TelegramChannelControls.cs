namespace JobFinder.Core.Domain;

public sealed class TelegramChannelDecisionRevision
{
    public long ChannelId { get; set; }
    public long Revision { get; set; }
    public required string BeforeJson { get; set; }
    public ChannelStatus Status { get; set; }
    public string Actor { get; set; } = "";
    public DateTimeOffset PublishedAt { get; set; }
    public long? BatchId { get; set; }
}

public sealed class TelegramChannelApprovalBatch
{
    public long Id { get; set; }
    public int Version { get; set; } = 1;
    public required string InputJson { get; set; }
    public required string InputHash { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? AppliedAt { get; set; }
    public long? NotificationId { get; set; }
    public Notification? Notification { get; set; }
}
