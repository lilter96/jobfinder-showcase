namespace JobFinder.Core.Domain;

/// <summary>What a Telegram @username is. Only a <see cref="Person"/> is someone to write to about a vacancy.</summary>
public enum TelegramPeerKind
{
    Person,
    Channel,
    Group,
    Bot,

    /// <summary>Nobody has it (a typo, a deleted account, a handle of another network).</summary>
    Missing,
}

/// <summary>
/// A resolved @username, kept across restarts: Telegram rate-limits resolving hard, and a channel's footer names the same sibling
/// channels in every post — each one is asked once.
/// </summary>
public sealed class TelegramUsername
{
    /// <summary>Lower case, without "@".</summary>
    public required string Username { get; set; }

    public TelegramPeerKind Kind { get; set; }

    /// <summary>The person's name or the channel's title.</summary>
    public string? Name { get; set; }

    public DateTimeOffset ResolvedAt { get; set; }
}
