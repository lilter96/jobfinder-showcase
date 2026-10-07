namespace JobFinder.Core.Domain;

public enum LinkedInPeopleStage { Reading, Completed, Failed }

public sealed class LinkedInPeopleRequest
{
    public Guid Id { get; set; }
    public string BusinessKey { get; set; } = "";
    public LinkedInPeopleStage Stage { get; set; }
    public string InputJson { get; set; } = "";
    public string InputHash { get; set; } = "";
    public string? ResultJson { get; set; }
    public string? ResultHash { get; set; }
    public Guid? ClaimEnvelopeId { get; set; }
    public DateTimeOffset? RetryAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public string? Error { get; set; }
}
