namespace JobFinder.Core.Domain;

public enum CommentSourceStage { Queued, Waiting, Completed, Cancelled, Failed }

public sealed class CommentSourceRequest
{
    public Guid Id { get; set; }
    public string SourceKey { get; set; } = "";
    public string PostId { get; set; } = "";
    public string Author { get; set; } = "";
    public string InputJson { get; set; } = "{}";
    public string InputHash { get; set; } = "";
    public CommentSourceStage Stage { get; set; }
    public Guid? ClaimEnvelopeId { get; set; }
    public Guid? PreparationRequestId { get; set; }
    public long? DraftId { get; set; }
    public long? ReplacesDraftId { get; set; }
    public bool AutoPublish { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? RetryAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public string? Error { get; set; }
}
