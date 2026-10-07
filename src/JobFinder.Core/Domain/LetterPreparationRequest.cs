namespace JobFinder.Core.Domain;

public enum LetterPreparationStage { Active, Completed, Cancelled, Failed }
public enum LetterLaneStage { Drafting, Reviewing, Completed, Abandoned, Failed }

public sealed class LetterPreparationRequest
{
    public Guid Id { get; set; }
    public long? VacancyId { get; set; }
    public string BusinessKey { get; set; } = "";
    public string InputHash { get; set; } = "";
    public string InputJson { get; set; } = "";
    public LetterPreparationStage Stage { get; set; }
    public int Round { get; set; } = 1;
    public string? BestJson { get; set; }
    public string? ResultJson { get; set; }
    public DateTimeOffset? GraceUntil { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public string? Error { get; set; }
}

public sealed class LetterPreparationLane
{
    public Guid Id { get; set; }
    public Guid RequestId { get; set; }
    public int Round { get; set; }
    public int Lane { get; set; }
    public LetterLaneStage Stage { get; set; }
    public Guid? ClaimEnvelopeId { get; set; }
    public string? DraftJson { get; set; }
    public string? ResultJson { get; set; }
    public int Failures { get; set; }
    public DateTimeOffset? RetryAt { get; set; }
    public string? Error { get; set; }
}
