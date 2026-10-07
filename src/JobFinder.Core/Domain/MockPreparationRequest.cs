namespace JobFinder.Core.Domain;

public enum MockPreparationStage { Preparing, Publishing, Completed, Cancelled, Failed }

public sealed class MockPreparationRequest
{
    public Guid Id { get; set; }
    public long SessionId { get; set; }
    public string BusinessKey { get; set; } = "";
    public string InputJson { get; set; } = "{}";
    public string InputHash { get; set; } = "";
    public MockPreparationStage Stage { get; set; }
    public string? ResultJson { get; set; }
    public string? ResultHash { get; set; }
    public Guid? ClaimEnvelopeId { get; set; }
    public DateTimeOffset? RetryAt { get; set; }
    public int Failures { get; set; }
    public string? Error { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
}

public sealed class MockSessionRevision
{
    public long Id { get; set; }
    public long SessionId { get; set; }
    public long Revision { get; set; }
    public string? BeforeJson { get; set; }
    public string AfterJson { get; set; } = "{}";
    public string Change { get; set; } = "";
    public DateTimeOffset RecordedAt { get; set; }
}
