namespace JobFinder.Core.Domain;

public enum VacancyAssessmentStage { Scoring, Lettering, Finishing, Completed, Cancelled, Failed }

public sealed class VacancyAssessmentRequest
{
    public Guid Id { get; set; }
    public long VacancyId { get; set; }
    public string? PreviewKey { get; set; }
    public string InputHash { get; set; } = "";
    public string InputJson { get; set; } = "{}";
    public VacancyAssessmentStage Stage { get; set; }
    public Guid? ClaimEnvelopeId { get; set; }
    public string? ScoreJson { get; set; }
    public string? ScoreHash { get; set; }
    public string? LetterJson { get; set; }
    public int Failures { get; set; }
    public string? Error { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? RetryAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
}
