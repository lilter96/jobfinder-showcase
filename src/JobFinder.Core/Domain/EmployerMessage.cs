namespace JobFinder.Core.Domain;

public enum EmployerMessageKind { Recruiter, Reply, Outreach, HhChat, HhLetter }
public enum EmployerMessageStatus { Pending, Ready, Sending, Sent, NeedsManual, OutcomeUnknown, Cancelled }

public sealed class EmployerMessage
{
    public Guid Id { get; set; }
    public required string BusinessKey { get; set; }
    public long VacancyId { get; set; }
    public long? ApplicationId { get; set; }
    public required string InputJson { get; set; }
    public EmployerMessageStatus Status { get; set; }
    public Guid? ClaimEnvelopeId { get; set; }
    public Guid? DispatchEnvelopeId { get; set; }
    public DateTimeOffset? RetryAt { get; set; }
    public ApplicationLetterReview? Review { get; set; }
    public Guid? ResumeRequestId { get; set; }
    public Guid? LetterReviewRequestId { get; set; }
    public long? TailoredResumeId { get; set; }
    public string? ResumeHash { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public string? Error { get; set; }
}
