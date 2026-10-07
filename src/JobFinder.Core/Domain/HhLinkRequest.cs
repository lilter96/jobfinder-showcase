namespace JobFinder.Core.Domain;

public enum HhLinkStage { Reading, Linking, Completed, Cancelled, Failed }

public sealed class HhLinkRequest
{
    public Guid Id { get; set; }
    public long HhId { get; set; }
    public List<string> Contacts { get; set; } = [];
    public HhLinkStage Stage { get; set; }
    public long? VacancyId { get; set; }
    public string? Summary { get; set; }
    public int Failures { get; set; }
    public string? LastError { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? RetryAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
}
