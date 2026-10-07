namespace JobFinder.Core.Domain;

public enum CommentDeliveryStage { Preparing, Ready, Sending, Completed, Cancelled, Failed, OutcomeUnknown }

public sealed class CommentDeliveryRequest
{
    public Guid Id { get; set; }
    public long DraftId { get; set; }
    public long Revision { get; set; }
    public string InputJson { get; set; } = "{}";
    public string InputHash { get; set; } = "";
    public CommentDeliveryStage Stage { get; set; }
    public Guid? ClaimEnvelopeId { get; set; }
    public string? PreparedJson { get; set; }
    public string? PreparedHash { get; set; }
    public Guid? AttemptId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? RetryAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public string? Error { get; set; }
}

public sealed class CommentDeliveryAttempt
{
    public Guid Id { get; set; }
    public Guid RequestId { get; set; }
    public long DraftId { get; set; }
    public long Revision { get; set; }
    public string Target { get; set; } = "";
    public string Text { get; set; } = "";
    public string TextHash { get; set; } = "";
    public string InputHash { get; set; } = "";
    public string PreparedHash { get; set; } = "";
    public DeliveryAttemptStatus Status { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public string? ExternalId { get; set; }
    public string? Error { get; set; }
    public bool? ReconciledSent { get; set; }
}
