namespace JobFinder.Core.Domain;

public enum DeliveryAttemptStatus
{
    Submitting,
    Confirmed,
    KnownNotSent,
    OutcomeUnknown,
    DryRun,
    Reconciled,
}

public sealed class DeliveryAttempt
{
    public Guid Id { get; set; }

    public long? ApplicationId { get; set; }

    public Guid? MessageId { get; set; }

    public Guid? ApplicationDeliveryRequestId { get; set; }

    public EmployerMessage? Message { get; set; }

    public Application? Application { get; set; }

    public long ApplicationRevision { get; set; }

    public Guid OperationId { get; set; }

    public ApplicationChannel Channel { get; set; }

    public string? Target { get; set; }

    public required string Text { get; set; }

    public required string TextHash { get; set; }

    public ApplicationLetterReview Review { get; set; } = new();

    public long? TailoredResumeId { get; set; }

    public string? ResumeHash { get; set; }

    public string? ResumeFileName { get; set; }

    public DeliveryAttemptStatus Status { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset? CompletedAt { get; set; }

    public string? Error { get; set; }

    public string? ExternalId { get; set; }
}
