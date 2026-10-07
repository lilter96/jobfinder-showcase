namespace JobFinder.Core.Domain;

public enum NotificationDeliveryStage { Preparing, Ready, Sending, Completed, Cancelled, Failed, OutcomeUnknown, Reconciled, AwaitingTarget }
public enum NotificationEffectKind { Text, Document, EditKeyboard, EditText, EditPrompt }

public sealed class NotificationDeliveryRequest
{
    public Guid Id { get; set; }
    public long NotificationId { get; set; }
    public string InputJson { get; set; } = "{}";
    public string InputHash { get; set; } = "";
    public string? AdmissionJson { get; set; }
    public string? AdmissionHash { get; set; }
    public string? LegacyReconciliationJson { get; set; }
    public string? LegacyReconciliationHash { get; set; }
    public string? PlanJson { get; set; }
    public string? PlanHash { get; set; }
    public NotificationDeliveryStage Stage { get; set; }
    public int EffectIndex { get; set; }
    public bool PlainText { get; set; }
    public Guid? ClaimEnvelopeId { get; set; }
    public Guid? AttemptId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? RetryAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public string? Error { get; set; }
}

public sealed class NotificationDeliveryAttempt
{
    public Guid Id { get; set; }
    public Guid RequestId { get; set; }
    public int EffectIndex { get; set; }
    public int Number { get; set; }
    public string InputHash { get; set; } = "";
    public string PlanHash { get; set; } = "";
    public string EffectJson { get; set; } = "{}";
    public string EffectHash { get; set; } = "";
    public DeliveryAttemptStatus Status { get; set; }
    public int? TelegramMessageId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public bool? ReconciledSent { get; set; }
    public string? Error { get; set; }
}
