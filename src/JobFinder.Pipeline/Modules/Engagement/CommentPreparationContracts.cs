using System.Text.Json;
using System.Text.Json.Serialization;
using JobFinder.Core.Artifacts;
using JobFinder.Core.Profile;
using JobFinder.Llm.Configuration;
using JobFinder.Llm.Tasks;

namespace JobFinder.Pipeline.Modules.Engagement;

public sealed record CommentPreparationInput
{
    public int Version { get; set; } = 1;
    public CandidateProfile Profile { get; set; } = new();
    public string Author { get; set; } = "";
    public string PostText { get; set; } = "";
    public LlmOperationSnapshot Operation { get; set; } = new();
    public ArtifactRecipe Recipe { get; set; } = new();
    public List<string> ExampleShingles { get; set; } = [];
}

public sealed record CommentPreparationRound
{
    public CommentPlan Plan { get; set; } = new();
    public List<string> Good { get; set; } = [];
    public List<string> Remarks { get; set; } = [];
}

public sealed record CommentPreparationResult
{
    public CommentPlan Plan { get; set; } = new();
    public List<string> Remarks { get; set; } = [];
}

public sealed record DraftComment
{
    public Guid RequestId { get; set; }
    public int Round { get; set; } = 1;
    public int Version { get; set; } = 1;
}

public sealed record CheckComment
{
    public Guid RequestId { get; set; }
    public int Round { get; set; } = 1;
    public int Version { get; set; } = 1;
}

public sealed class UnsupportedCommentPreparationException() : Exception("unsupported comment preparation command or checkpoint");
public sealed class CommentPreparationSupersededException() : Exception("comment preparation recipe changed");

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, UseStringEnumConverter = true)]
[JsonSerializable(typeof(CommentPreparationInput))]
[JsonSerializable(typeof(CommentPreparationRound))]
[JsonSerializable(typeof(CommentPreparationResult))]
[JsonSerializable(typeof(DraftComment))]
[JsonSerializable(typeof(CheckComment))]
public sealed partial class CommentPreparationJsonContext : JsonSerializerContext;
