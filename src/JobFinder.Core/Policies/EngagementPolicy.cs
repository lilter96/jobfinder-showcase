using System.Text.Json.Serialization;

namespace JobFinder.Core.Policies;

public sealed record EngagementPolicy
{
    [JsonRequired] public int SchemaVersion { get; set; } = 1;
    [JsonRequired] public bool AutoPublish { get; set; } = true;
    [JsonRequired] public int MaxDraftsPerDay { get; set; } = 10;
    [JsonRequired] public int AuthorCooldownDays { get; set; } = 7;

    public string? Validate() => SchemaVersion != 1 ? "unknown engagement policy schema version"
        : MaxDraftsPerDay is < 0 or > 100 ? "comment draft limit must be in 0..100"
        : AuthorCooldownDays is < 0 or > 365 ? "author cooldown must be in 0..365 days"
        : null;
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(EngagementPolicy))]
public sealed partial class EngagementPolicyJsonContext : JsonSerializerContext;
