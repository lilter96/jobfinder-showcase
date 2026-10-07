using System.Text.Json;
using System.Text.Json.Serialization;
using JobFinder.Core.Domain;
using JobFinder.Core.Text;
using Wolverine.Attributes;

namespace JobFinder.Pipeline.Modules.Discovery;

[MessageIdentity("jobfinder.discovery.channel-membership.v1")]
public sealed record ProcessChannelMembership
{
    public int Version { get; set; } = 1;
    public Guid RequestId { get; set; }
    public ChannelMembershipStage Stage { get; set; }
}

public sealed record ChannelMembershipInput
{
    public int Version { get; set; } = 1;
    public ChannelDecisionSnapshot Source { get; set; } = new();
    public long? AccessHash { get; set; }
    public string Folder { get; set; } = "Jobs";
    public int MinSpacingSeconds { get; set; } = 40;
    public int MaxSpacingSeconds { get; set; } = 120;
    public int JoinsPerTenMinutes { get; set; } = 8;
}

public sealed record ChannelMembershipChat
{
    public int Version { get; set; } = 1;
    public long Id { get; set; }
    public long AccessHash { get; set; }
    public string Title { get; set; } = "";
    public string? Username { get; set; }
    public ChannelKind Kind { get; set; }
    public bool IsMember { get; set; }
}

public sealed record ChannelMembershipHistory
{
    public int Version { get; set; } = 1;
    public List<RawPost> Posts { get; set; } = [];
}

public sealed class UnsupportedChannelMembershipException() : Exception("unsupported or damaged Telegram membership operation");
public sealed class ChannelSessionUnavailableException() : Exception("captured Telegram account is not connected");
public sealed class ChannelMembershipRpcException(string message, TimeSpan? retryAfter = null, bool inviteRequested = false) : Exception(message)
{
    public TimeSpan? RetryAfter { get; } = retryAfter;
    public bool InviteRequested { get; } = inviteRequested;
}

internal static class ChannelMembershipInputs
{
    public static ChannelMembershipInput Input(ChannelMembershipRequest root)
    {
        if (CanonicalJson.Hash(root.InputJson) != root.InputHash) throw new UnsupportedChannelMembershipException();
        var input = Read(root.InputJson, ChannelMembershipJsonContext.Default.ChannelMembershipInput);
        if (root.Id == Guid.Empty || root.CreatedAt == default || !Enum.IsDefined(root.Stage)
            || root.AccountId is <= 0 || root.ClaimEnvelopeId == Guid.Empty || root.AttemptId == Guid.Empty
            || root.CompletedAt < root.CreatedAt || Terminal(root.Stage) && root.Stage != ChannelMembershipStage.OutcomeUnknown && root.CompletedAt is null
            || input is not { Version: 1, Source: { Version: 1, Id: > 0, Revision: >= 0, Status: ChannelStatus.Approved } }
            || input.Source.Id != root.ChannelId || input.Source.Revision != root.DecisionRevision || !Enum.IsDefined(input.Source.Kind)
            || input.Source.AddedAt == default || input.Source.PeerId is <= 0
            || string.IsNullOrWhiteSpace(input.Source.Username) && (input.Source.PeerId is null || input.AccessHash is null)
            || string.IsNullOrWhiteSpace(input.Folder) || input.Folder.Length > 64 || input.MinSpacingSeconds < 1
            || input.MaxSpacingSeconds < input.MinSpacingSeconds || input.MaxSpacingSeconds > 3600 || input.JoinsPerTenMinutes is < 1 or > 100)
            throw new UnsupportedChannelMembershipException();
        return input;
    }

    public static ChannelMembershipChat Chat(ChannelMembershipRequest root)
    {
        if (root.ChatJson is null || CanonicalJson.Hash(root.ChatJson) != root.ChatHash) throw new UnsupportedChannelMembershipException();
        var chat = Read(root.ChatJson, ChannelMembershipJsonContext.Default.ChannelMembershipChat);
        var source = Input(root).Source;
        if (chat is not { Version: 1, Id: > 0, Title: not null } || !Enum.IsDefined(chat.Kind)
            || source.PeerId is { } id && chat.Id != id
            || source.PeerId is null && !string.Equals(source.Username, chat.Username, StringComparison.OrdinalIgnoreCase))
            throw new UnsupportedChannelMembershipException();
        return chat;
    }

    public static ChannelMembershipHistory History(ChannelMembershipRequest root)
    {
        if (root.HistoryJson is null || CanonicalJson.Hash(root.HistoryJson) != root.HistoryHash) throw new UnsupportedChannelMembershipException();
        var history = Read(root.HistoryJson, ChannelMembershipJsonContext.Default.ChannelMembershipHistory);
        var chat = Chat(root);
        if (history is not { Version: 1, Posts: not null } || history.Posts.Count > 20
            || history.Posts.Any(post => post is null || post.Source != SourceKind.Telegram || post.ChatRef != chat.Id.ToString(System.Globalization.CultureInfo.InvariantCulture)
                || !int.TryParse(post.ExternalId, out var id) || id <= 0 || post.ExternalId != id.ToString(System.Globalization.CultureInfo.InvariantCulture)
                || string.IsNullOrWhiteSpace(post.Text) || post.Status != RawPostStatus.New || post.Links is null
                || post.Links.Any(link => link is null || string.IsNullOrWhiteSpace(link.Url))
                || post.Id != 0 || post.ProcessingRequestId is not null || post.ProcessingRevision != 0 || post.PublishedAt == default || post.FetchedAt == default)
            || history.Posts.Select(post => post.ExternalId).Distinct().Count() != history.Posts.Count)
            throw new UnsupportedChannelMembershipException();
        return history;
    }

    public static bool Effect(ChannelMembershipStage stage) => stage is ChannelMembershipStage.Joining or ChannelMembershipStage.Muting
        or ChannelMembershipStage.Archiving or ChannelMembershipStage.Filing;

    public static void Attempt(ChannelMembershipRequest root, ChannelMembershipAttempt attempt)
    {
        var chat = Chat(root);
        if (attempt.Id == Guid.Empty || attempt.RequestId != root.Id || attempt.EnvelopeId == Guid.Empty
            || attempt.AccountId <= 0 || attempt.AccountId != root.AccountId || attempt.PeerId != chat.Id
            || attempt.InputHash != root.InputHash || attempt.ChatHash != root.ChatHash || !Effect(attempt.Stage)
            || !Enum.IsDefined(attempt.Outcome) || attempt.StartedAt < root.CreatedAt
            || attempt.Outcome == ChannelMembershipOutcome.Sending && (attempt.CompletedAt is not null || attempt.Evidence is not null || attempt.CheckedApplied is not null)
            || attempt.Outcome != ChannelMembershipOutcome.Sending && (attempt.CompletedAt is null || attempt.CompletedAt < attempt.StartedAt || string.IsNullOrWhiteSpace(attempt.Evidence))
            || attempt.Outcome == ChannelMembershipOutcome.Reconciled != (attempt.CheckedApplied is not null)
            || attempt.Outcome == ChannelMembershipOutcome.InviteRequested && attempt.Stage != ChannelMembershipStage.Joining)
            throw new UnsupportedChannelMembershipException();
    }

    public static void Publication(ChannelMembershipRequest root, IReadOnlyList<ChannelMembershipAttempt> attempts)
    {
        var chat = Chat(root);
        History(root);
        if (root.AccountId is not > 0 || root.AttemptId is not null
            || root.Stage == ChannelMembershipStage.Publishing && root.ClaimEnvelopeId is null
            || root.Stage == ChannelMembershipStage.Completed && (root.CompletedAt is null || root.ClaimEnvelopeId is not null))
            throw new UnsupportedChannelMembershipException();
        foreach (var attempt in attempts) Attempt(root, attempt);
        if (attempts.Select(attempt => attempt.Id).Distinct().Count() != attempts.Count
            || attempts.Select(attempt => attempt.EnvelopeId).Distinct().Count() != attempts.Count
            || root.CompletedAt is { } finished && attempts.Any(attempt => attempt.CompletedAt > finished))
            throw new UnsupportedChannelMembershipException();
        if (attempts.Any(attempt => attempt.Outcome is not (ChannelMembershipOutcome.Confirmed or ChannelMembershipOutcome.KnownNotApplied)))
            throw new UnsupportedChannelMembershipException();
        DateTimeOffset previous = root.CreatedAt;
        var stages = chat.IsMember
            ? new[] { ChannelMembershipStage.Muting, ChannelMembershipStage.Archiving, ChannelMembershipStage.Filing }
            : new[] { ChannelMembershipStage.Joining, ChannelMembershipStage.Muting, ChannelMembershipStage.Archiving, ChannelMembershipStage.Filing };
        foreach (var stage in stages)
        {
            var completed = attempts.Where(attempt => attempt.Stage == stage && attempt.Outcome == ChannelMembershipOutcome.Confirmed).ToArray();
            if (completed.Length != 1 || completed[0].StartedAt < previous) throw new UnsupportedChannelMembershipException();
            previous = completed[0].CompletedAt!.Value;
        }
        if (chat.IsMember && attempts.Any(attempt => attempt.Stage == ChannelMembershipStage.Joining))
            throw new UnsupportedChannelMembershipException();
    }

    public static void Progress(ChannelMembershipRequest root, IReadOnlyList<ChannelMembershipAttempt> attempts)
    {
        Input(root);
        if (root.Stage == ChannelMembershipStage.Resolving)
        {
            if (attempts.Count != 0 || root.ChatJson is not null || root.ChatHash is not null || root.HistoryJson is not null || root.HistoryHash is not null)
                throw new UnsupportedChannelMembershipException();
            return;
        }
        var chat = Chat(root);
        var stages = chat.IsMember
            ? new[] { ChannelMembershipStage.Muting, ChannelMembershipStage.Archiving, ChannelMembershipStage.Filing }
            : new[] { ChannelMembershipStage.Joining, ChannelMembershipStage.Muting, ChannelMembershipStage.Archiving, ChannelMembershipStage.Filing };
        var count = Array.IndexOf(stages, root.Stage);
        if (root.Stage is ChannelMembershipStage.ReadingHistory or ChannelMembershipStage.Publishing) count = stages.Length;
        if (count < 0 || root.AccountId is not > 0 || attempts.Select(attempt => attempt.Id).Distinct().Count() != attempts.Count
            || attempts.Select(attempt => attempt.EnvelopeId).Distinct().Count() != attempts.Count) throw new UnsupportedChannelMembershipException();
        DateTimeOffset previous = root.CreatedAt;
        foreach (var stage in stages.Take(count))
        {
            var confirmed = attempts.Where(attempt => attempt.Stage == stage && attempt.Outcome == ChannelMembershipOutcome.Confirmed).ToArray();
            if (confirmed.Length != 1 || confirmed[0].StartedAt < previous || confirmed[0].CompletedAt is null)
                throw new UnsupportedChannelMembershipException();
            previous = confirmed[0].CompletedAt!.Value;
        }
        foreach (var attempt in attempts)
        {
            Attempt(root, attempt);
            var index = Array.IndexOf(stages, attempt.Stage);
            if (index < 0 || index > count || index == count && attempt.Outcome == ChannelMembershipOutcome.Confirmed
                || attempt.Outcome is ChannelMembershipOutcome.OutcomeUnknown or ChannelMembershipOutcome.Reconciled or ChannelMembershipOutcome.InviteRequested
                || attempt.Outcome == ChannelMembershipOutcome.Sending && (attempt.Id != root.AttemptId || attempt.EnvelopeId != root.ClaimEnvelopeId || attempt.Stage != root.Stage)
                || attempt.Stage == root.Stage && attempt.StartedAt < previous)
                throw new UnsupportedChannelMembershipException();
        }
        if (root.Stage == ChannelMembershipStage.Publishing) History(root);
        else if (root.HistoryJson is not null || root.HistoryHash is not null) throw new UnsupportedChannelMembershipException();
    }

    public static bool Terminal(ChannelMembershipStage stage) => stage is ChannelMembershipStage.Completed or ChannelMembershipStage.Cancelled
        or ChannelMembershipStage.Failed or ChannelMembershipStage.OutcomeUnknown or ChannelMembershipStage.Reconciled;

    private static T Read<T>(string json, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> info)
    {
        try { return JsonSerializer.Deserialize(json, info) ?? throw new UnsupportedChannelMembershipException(); }
        catch (JsonException) { throw new UnsupportedChannelMembershipException(); }
    }
}

[JsonSerializable(typeof(ProcessChannelMembership))]
[JsonSerializable(typeof(ChannelMembershipInput))]
[JsonSerializable(typeof(ChannelMembershipChat))]
[JsonSerializable(typeof(ChannelMembershipHistory))]
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
internal sealed partial class ChannelMembershipJsonContext : JsonSerializerContext;
