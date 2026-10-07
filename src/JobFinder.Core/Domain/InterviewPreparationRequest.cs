namespace JobFinder.Core.Domain;

public enum InterviewPreparationStage { Capturing, Planning, Answering, Printing, Publishing, Completed, Cancelled, Failed }

public sealed class InterviewPreparationRequest
{
    public Guid Id { get; set; }
    public long InterviewId { get; set; }
    public long Revision { get; set; }
    public InterviewPreparationStage Stage { get; set; }
    public string SeedJson { get; set; } = "{}";
    public string SeedHash { get; set; } = "";
    public string? InputJson { get; set; }
    public string? InputHash { get; set; }
    public string? PlanJson { get; set; }
    public string? PlanHash { get; set; }
    public string? CopilotJson { get; set; }
    public string? CopilotHash { get; set; }
    public string? PdfPath { get; set; }
    public string? PdfHash { get; set; }
    public Guid? ClaimEnvelopeId { get; set; }
    public int Failures { get; set; }
    public string? Error { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? RetryAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
}
