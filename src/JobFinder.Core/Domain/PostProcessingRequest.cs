namespace JobFinder.Core.Domain;

public enum PostProcessingStage { Reading, Extracting, Verifying, Publishing, Completed, Cancelled, Failed }

public sealed class PostProcessingRequest
{
    public Guid Id { get; set; }
    public long PostId { get; set; }
    public long Revision { get; set; }
    public PostProcessingStage Stage { get; set; }
    public string InputJson { get; set; } = "{}";
    public string InputHash { get; set; } = "";
    public Guid? ClaimEnvelopeId { get; set; }
    public string? ReadingJson { get; set; }
    public string? ReadingHash { get; set; }
    public string? ExtractionJson { get; set; }
    public string? ExtractionHash { get; set; }
    public string? VerificationJson { get; set; }
    public string? VerificationHash { get; set; }
    public string? ResultJson { get; set; }
    public string? ResultHash { get; set; }
    public int Failures { get; set; }
    public string? Error { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? RetryAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
}
