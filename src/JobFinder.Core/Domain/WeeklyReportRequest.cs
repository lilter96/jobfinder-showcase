namespace JobFinder.Core.Domain;

public enum WeeklyReportStage { Preparing, Publishing, Completed, Cancelled, Failed }

public sealed class WeeklyReportRequest
{
    public Guid Id { get; set; }
    public string BusinessKey { get; set; } = "";
    public string InputJson { get; set; } = "{}";
    public string InputHash { get; set; } = "";
    public WeeklyReportStage Stage { get; set; }
    public string? ResultJson { get; set; }
    public string? ResultHash { get; set; }
    public long? JobRunId { get; set; }
    public long NotificationId { get; set; }
    public string? PublishedHtmlHash { get; set; }
    public bool AdvicePublished { get; set; }
    public Guid? ClaimEnvelopeId { get; set; }
    public DateTimeOffset? RetryAt { get; set; }
    public int Failures { get; set; }
    public string? Error { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
}
