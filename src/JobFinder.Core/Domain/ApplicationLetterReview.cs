namespace JobFinder.Core.Domain;

public sealed record ApplicationLetterReview
{
    public string TextHash { get; set; } = "";

    public string ContextHash { get; set; } = "";
    public Artifacts.ArtifactRecipe? Recipe { get; set; }

    public int Score { get; set; }

    public List<string> Problems { get; set; } = [];

    public DateTimeOffset CheckedAt { get; set; }
}
