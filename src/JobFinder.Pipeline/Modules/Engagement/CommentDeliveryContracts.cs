using System.Text.Json;
using System.Text.Json.Serialization;
using JobFinder.Core.Domain;
using JobFinder.Core.Filtering;
using JobFinder.Core.Policies;
using JobFinder.Core.Text;
using JobFinder.Integrations.Threads;

namespace JobFinder.Pipeline.Modules.Engagement;

public sealed record CommentDeliverySource
{
    public long Id { get; set; }
    public long Revision { get; set; }
    public string PostId { get; set; } = "";
    public string PostUrl { get; set; } = "";
    public string PostText { get; set; } = "";
    public string Author { get; set; } = "";
    public string Text { get; set; } = "";
    public AudienceKind Audience { get; set; }
    public Guid? SourceRequestId { get; set; }

    public ThreadsPost Post() => new(PostId, Author, PostText, null, PostUrl);
    public static CommentDeliverySource Capture(CommentDraft draft) => new()
    {
        Id = draft.Id, Revision = draft.Revision, PostId = draft.PostId, PostUrl = draft.PostUrl, PostText = draft.PostText,
        Author = draft.Author, Text = HumanText.Clean(draft.ChosenText), Audience = draft.Audience, SourceRequestId = draft.SourceRequestId,
    };

    public string Hash() => CanonicalJson.Hash(JsonSerializer.Serialize(this, CommentDeliveryJsonContext.Default.CommentDeliverySource));
}

public sealed record CommentDeliveryInput
{
    public int Version { get; set; } = 1;
    public CommentDeliverySource Source { get; set; } = new();
    public bool Automatic { get; set; }
    public bool LegacyUncertain { get; set; }
    public long ProfileVersion { get; set; }
    public string ProfileHash { get; set; } = "";
    public EngagementPolicy Policy { get; set; } = new();
    public long PolicyVersion { get; set; }
    public ThreadsPublicationRoute Route { get; set; } = new();
    public int SpacingSeconds { get; set; } = 60;
    public int MaxPostAgeHours { get; set; } = 48;
    public long SearchPolicyVersion { get; set; }
    public DateTimeOffset? ExpiresAt { get; set; }
}

public sealed record PrepareCommentDelivery
{
    public Guid RequestId { get; set; }
    public int Version { get; set; } = 1;
}

public sealed record DispatchCommentDelivery
{
    public Guid RequestId { get; set; }
    public int Version { get; set; } = 1;
}

public sealed class UnsupportedCommentDeliveryException() : Exception("unsupported comment delivery command or checkpoint");

public static class CommentDeliveryInputs
{
    public static string Serialize(CommentDeliveryInput input) => JsonSerializer.Serialize(input, CommentDeliveryJsonContext.Default.CommentDeliveryInput);
    public static CommentDeliveryInput Read(CommentDeliveryRequest request)
    {
        var input = Witness(request);
        if (input.LegacyUncertain || input.ProfileVersion <= 0 || input.ProfileHash is not { Length: 64 } || input.PolicyVersion <= 0 || input.Policy is null || input.Policy.Validate() is not null
            || !Enum.IsDefined(input.Source.Audience)
            || input.Route is null || !Enum.IsDefined(input.Route.Mode) || input.SpacingSeconds is < 60 or > 180
            || input.MaxPostAgeHours <= 0
            || input.Source.Text.Length is 0 or > 500 || string.IsNullOrWhiteSpace(input.Source.PostId)
            || string.IsNullOrWhiteSpace(input.Source.PostUrl) || string.IsNullOrWhiteSpace(input.Source.Author)
            || CommentStyle.Problems(input.Source.Text, input.Source.Audience == AudienceKind.Tech).Count > 0)
            throw new UnsupportedCommentDeliveryException();
        return input;
    }

    public static CommentDeliveryInput Witness(CommentDeliveryRequest request)
    {
        if (CanonicalJson.Hash(request.InputJson) != request.InputHash) throw new UnsupportedCommentDeliveryException();
        var input = JsonSerializer.Deserialize(request.InputJson, CommentDeliveryJsonContext.Default.CommentDeliveryInput) ?? throw new UnsupportedCommentDeliveryException();
        if (input.Version != 1 || input.Source is null || input.Source.Id <= 0 || input.Source.Revision < 0 || input.Source.Id != request.DraftId || input.Source.Revision != request.Revision
            || input.Source.Text is null || input.Source.PostUrl is null || input.Source.Author is null || input.Source.PostId is null)
            throw new UnsupportedCommentDeliveryException();
        return input;
    }

    public static bool Matches(CommentDraft draft, CommentDeliveryRequest request, CommentDeliveryInput input, CommentStatus status) =>
        draft.DeliveryRequestId == request.Id && draft.Status == status && CommentDeliverySource.Capture(draft).Hash() == input.Source.Hash();
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, UseStringEnumConverter = true)]
[JsonSerializable(typeof(CommentDeliveryInput))]
[JsonSerializable(typeof(CommentDeliverySource))]
[JsonSerializable(typeof(PrepareCommentDelivery))]
[JsonSerializable(typeof(DispatchCommentDelivery))]
[JsonSerializable(typeof(ThreadsPreparedPublication))]
public sealed partial class CommentDeliveryJsonContext : JsonSerializerContext;
