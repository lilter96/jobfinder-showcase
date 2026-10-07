namespace JobFinder.Core.Domain;

public enum CommentPreparationStage { Drafting, Checking, Completed, Cancelled, Failed }

public sealed class CommentPreparationRequest
{
    public Guid Id { get; set; }
    public string BusinessKey { get; set; } = "";
    public string InputHash { get; set; } = "";
    public string InputJson { get; set; } = "{}";
    public CommentPreparationStage Stage { get; set; }
    public int Round { get; set; } = 1;
    public Guid? ClaimEnvelopeId { get; set; }
    public string? CurrentJson { get; set; }
    public string? FirstJson { get; set; }
    public string? ResultJson { get; set; }
    public int Failures { get; set; }
    public string? Error { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? RetryAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
}
