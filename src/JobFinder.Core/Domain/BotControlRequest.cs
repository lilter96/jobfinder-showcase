namespace JobFinder.Core.Domain;

public enum BotControlStage { Pending, Executing, Completed, OutcomeUnknown, Reconciled, Quarantined }

public sealed class BotControlRequest
{
    public Guid Id { get; set; }
    public long BotId { get; set; }
    public int UpdateId { get; set; }
    public BotControlStage Stage { get; set; }
    public string InputJson { get; set; } = "";
    public string InputHash { get; set; } = "";
    public Guid? ClaimEnvelopeId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public string? Error { get; set; }
    public string? ReconciliationJson { get; set; }
}
