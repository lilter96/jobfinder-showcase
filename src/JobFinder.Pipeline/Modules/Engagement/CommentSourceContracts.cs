using System.Text.Json;
using System.Text.Json.Serialization;
using JobFinder.Core.Domain;
using JobFinder.Core.Policies;
using JobFinder.Core.Text;
using JobFinder.Integrations.Threads;
using JobFinder.Pipeline.Profile;

namespace JobFinder.Pipeline.Modules.Engagement;

public sealed record CommentSourceInput
{
    public int Version { get; set; } = 1;
    public CommentPreparationInput Preparation { get; set; } = new();
    public long ProfileVersion { get; set; }
    public string ProfileHash { get; set; } = "";
    public string PostId { get; set; } = "";
    public string PostUrl { get; set; } = "";
    public DateTimeOffset? PublishedAt { get; set; }
    public DateTimeOffset? ExpiresAt { get; set; }
    public EngagementPolicy Policy { get; set; } = new();
    public long PolicyVersion { get; set; }
    public CommentSourceSnapshot? Replaces { get; set; }
}

public sealed record CommentSourceSnapshot
{
    public long Id { get; set; }
    public long Revision { get; set; }
    public CommentStatus Status { get; set; }
    public string Hash { get; set; } = "";
}

public sealed record PrepareCommentSource
{
    public Guid RequestId { get; set; }
    public int Version { get; set; } = 1;
}

public sealed record CommentDecisionSnapshot
{
    public long Id { get; set; }
    public long Revision { get; set; }
    public CommentStatus Status { get; set; } = CommentStatus.Pending;
}

public sealed class UnsupportedCommentSourceException() : Exception("unsupported comment source command or checkpoint");

internal static class CommentSourceInputs
{
    public static CommentSourceInput Read(CommentSourceRequest request)
    {
        if (CanonicalJson.Hash(request.InputJson) != request.InputHash) throw new UnsupportedCommentSourceException();
        var input = JsonSerializer.Deserialize(request.InputJson, CommentSourceJsonContext.Default.CommentSourceInput) ?? throw new UnsupportedCommentSourceException();
        if (input.Version != 1 || input.ProfileVersion <= 0 || input.PostId != request.PostId || input.Preparation is null
            || input.Preparation.Author != request.Author || input.Preparation.Profile is not { Reviewed: true }
            || input.Policy is null || input.Policy.Validate() is not null
            || input.Replaces?.Id != request.ReplacesDraftId
            || CanonicalJson.Hash(ProfileProvider.ToJson(input.Preparation.Profile)) != input.ProfileHash) throw new UnsupportedCommentSourceException();
        return input;
    }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, UseStringEnumConverter = true)]
[JsonSerializable(typeof(CommentSourceInput))]
[JsonSerializable(typeof(CommentSourceSnapshot))]
[JsonSerializable(typeof(PrepareCommentSource))]
[JsonSerializable(typeof(CommentDraft))]
[JsonSerializable(typeof(CommentDecisionSnapshot))]
public sealed partial class CommentSourceJsonContext : JsonSerializerContext;
