namespace JobFinder.Core.Domain;

public enum ApplicationDeliveryStage { Preparing, Ready, Sending, Completed, Cancelled, Failed, OutcomeUnknown }

public sealed class ApplicationDeliveryRequest
{
    public Guid Id { get; set; }
    public long ApplicationId { get; set; }
    public long ApplicationRevision { get; set; }
    public required string InputJson { get; set; }
    public required string InputHash { get; set; }
    public ApplicationDeliveryStage Stage { get; set; }
    public Guid? ClaimEnvelopeId { get; set; }
    public Guid? LetterReviewRequestId { get; set; }
    public Guid? ResumeRequestId { get; set; }
    public ApplicationLetterReview? Review { get; set; }
    public long? TailoredResumeId { get; set; }
    public string? ResumeHash { get; set; }
    public DateTimeOffset? RetryAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public string? Error { get; set; }
}
