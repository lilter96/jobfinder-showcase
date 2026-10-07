namespace JobFinder.Core.Domain;

public enum OfferPreparationStage { Analyzing, Publishing, Completed, Cancelled, Failed }

public sealed class OfferPreparationRequest
{
    public Guid Id { get; set; }
    public string BusinessKey { get; set; } = "";
    public string InputJson { get; set; } = "{}";
    public string InputHash { get; set; } = "";
    public OfferPreparationStage Stage { get; set; }
    public string? ResultJson { get; set; }
    public string? ResultHash { get; set; }
    public long? OfferId { get; set; }
    public Guid? ClaimEnvelopeId { get; set; }
    public DateTimeOffset? RetryAt { get; set; }
    public int Failures { get; set; }
    public string? Error { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
}
