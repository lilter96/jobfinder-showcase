namespace JobFinder.Core.Domain;

public enum ReplyProcessingMode { Incoming, Redraft, Preview, Reprocess }
public enum ReplyProcessingStage { Capturing, Fast, Deep, Publishing, Completed, Cancelled, Failed }

public sealed class ReplyProcessingRequest
{
    public Guid Id { get; set; }
    public string BusinessKey { get; set; } = "";
    public long ApplicationId { get; set; }
    public long VacancyId { get; set; }
    public long? EventId { get; set; }
    public Guid? RetryOfId { get; set; }
    public ReplyProcessingMode Mode { get; set; }
    public ReplyProcessingStage Stage { get; set; }
    public string RawJson { get; set; } = "";
    public string RawHash { get; set; } = "";
    public string? InputJson { get; set; }
    public string? InputHash { get; set; }
    public string? BestJson { get; set; }
    public int DeepAttempt { get; set; }
    public Guid? ClaimEnvelopeId { get; set; }
    public DateTimeOffset? RetryAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public string? Error { get; set; }
}
