namespace JobFinder.Core.Domain;

public sealed class CandidateRevision
{
    public required string Key { get; set; }

    public long Version { get; set; }

    public required string Content { get; set; }

    public required string Actor { get; set; }

    public DateTimeOffset PublishedAt { get; set; }
}
