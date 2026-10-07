namespace JobFinder.Core.Domain;

public sealed class LearningProgressRevision
{
    public long Id { get; set; }
    public string Scope { get; set; } = "";
    public long Revision { get; set; }
    public string BeforeJson { get; set; } = "{}";
    public string AfterJson { get; set; } = "{}";
    public string Change { get; set; } = "";
    public DateTimeOffset RecordedAt { get; set; }
}
