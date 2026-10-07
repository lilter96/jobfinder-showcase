namespace JobFinder.Core.Domain;

public sealed class ApplicationBlockRevision
{
    public long Revision { get; set; }
    public string? BeforeValue { get; set; }
    public string? Value { get; set; }
    public DateTimeOffset PublishedAt { get; set; }
    public string Actor { get; set; } = "";
    public Guid? AttemptId { get; set; }
}
