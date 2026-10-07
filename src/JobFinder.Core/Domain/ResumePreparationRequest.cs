namespace JobFinder.Core.Domain;

public enum ResumePreparationStage { Drafting, Printing, Reviewing, Finalizing, Completed, Cancelled, Failed }

public sealed class ResumePreparationRequest
{
    public Guid Id { get; set; }
    public long VacancyId { get; set; }
    public string InputHash { get; set; } = "";
    public string InputJson { get; set; } = "{}";
    public ResumePreparationStage Stage { get; set; }
    public int Round { get; set; } = 1;
    public Guid? ClaimEnvelopeId { get; set; }
    public string? CurrentJson { get; set; }
    public string? BestJson { get; set; }
    public long? ResultId { get; set; }
    public int Failures { get; set; }
    public string? Error { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? RetryAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
}
