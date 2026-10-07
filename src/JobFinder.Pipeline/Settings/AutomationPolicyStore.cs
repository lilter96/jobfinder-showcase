using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using JobFinder.Core.Domain;
using JobFinder.Core.Options;
using JobFinder.Data;
using Microsoft.EntityFrameworkCore;
using Wolverine.EntityFrameworkCore;

namespace JobFinder.Pipeline.Settings;

public sealed record AutomationPolicy
{
    public int SchemaVersion { get; set; } = 1;

    public int AutoApplyThreshold { get; set; } = 80;

    public int ApprovalThreshold { get; set; } = 60;

    public int DailyAutoApplyLimit { get; set; } = 25;

    public bool AutoApplyPaused { get; set; }

    public bool ApplyDryRun { get; set; }

    public bool CommentsPaused { get; set; }

    public bool ChatAutoReply { get; set; } = true;

    public string? Validate() => SchemaVersion != 1 ? "unknown automation policy schema version"
        : AutoApplyThreshold is < 0 or > 101 ? "auto-apply threshold must be in 0..101"
        : ApprovalThreshold is < 0 or > 100 ? "approval threshold must be in 0..100"
        : DailyAutoApplyLimit is < 0 or > 200 ? "daily auto-apply limit must be in 0..200"
        : null;
}

public sealed class AutomationPolicyStore(PgPolicyStore policies, DecisionOptions defaults)
{
    public const string Key = "policy.automation";

    private readonly PgPolicyStore _policies = policies;

    public static IReadOnlySet<string> LegacyKeys { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        RuntimeSettings.AutoApplyThreshold, RuntimeSettings.ApprovalThreshold, RuntimeSettings.DailyAutoApplyLimit,
        RuntimeSettings.AutoApplyPaused, RuntimeSettings.ApplyDryRun, RuntimeSettings.CommentsPaused, RuntimeSettings.ChatAutoReply,
    };

    public async Task<PolicySnapshot<AutomationPolicy>> ReadAsync(CancellationToken ct)
    {
        await EnsureImportedAsync(ct);
        var snapshot = await _policies.ReadAsync(Key, AutomationPolicyJsonContext.Default.AutomationPolicy, ct);
        if (snapshot.Value.Validate() is { } problem) throw new InvalidOperationException(problem);
        return new PolicySnapshot<AutomationPolicy>(snapshot.Value with { }, snapshot.Version);
    }

    public Task<PolicySnapshot<AutomationPolicy>> PublishAsync(AutomationPolicy policy, long expectedVersion, string actor, CancellationToken ct) =>
        _policies.PublishAsync(Key, policy, expectedVersion, AutomationPolicyJsonContext.Default.AutomationPolicy, value => value.Validate(), actor, ct);

    internal Task<PolicySnapshot<AutomationPolicy>> PublishJoinedAsync(JobFinderDbContext context, IDbContextOutbox outbox,
        AutomationPolicy policy, long expectedVersion, string actor, CancellationToken ct) =>
        _policies.PublishJoinedAsync(context, outbox, Key, policy, expectedVersion,
            AutomationPolicyJsonContext.Default.AutomationPolicy, value => value.Validate(), actor, ct);

    public async Task PreviewAsync(CancellationToken ct)
    {
        var snapshot = await _policies.PreviewAsync(Key, AutomationPolicyJsonContext.Default.AutomationPolicy,
            (context, token) => ImportAsync(context, token, removeLegacy: false), ct);
        if (snapshot.Value.Validate() is { } problem) throw new InvalidOperationException(problem);
    }

    public async Task SetAsync(string key, string value, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            var snapshot = await ReadAsync(ct);
            var edited = snapshot.Value with { };
            Set(edited, key, value);
            try
            {
                await PublishAsync(edited, snapshot.Version, "runtime", ct);
                return;
            }
            catch (PolicyConflictException) when (attempt < 7)
            {
            }
        }
    }

    public string Default(string key) => Get(Defaults(), key);

    public static string Get(AutomationPolicy policy, string key) => key switch
    {
        RuntimeSettings.AutoApplyThreshold => policy.AutoApplyThreshold.ToString(CultureInfo.InvariantCulture),
        RuntimeSettings.ApprovalThreshold => policy.ApprovalThreshold.ToString(CultureInfo.InvariantCulture),
        RuntimeSettings.DailyAutoApplyLimit => policy.DailyAutoApplyLimit.ToString(CultureInfo.InvariantCulture),
        RuntimeSettings.AutoApplyPaused => policy.AutoApplyPaused ? "true" : "false",
        RuntimeSettings.ApplyDryRun => policy.ApplyDryRun ? "true" : "false",
        RuntimeSettings.CommentsPaused => policy.CommentsPaused ? "true" : "false",
        RuntimeSettings.ChatAutoReply => policy.ChatAutoReply ? "true" : "false",
        _ => throw new ArgumentException("unknown automation policy field", nameof(key)),
    };

    private AutomationPolicy Defaults() => new()
    {
        AutoApplyThreshold = defaults.AutoApplyThreshold, ApprovalThreshold = defaults.ApprovalThreshold,
        DailyAutoApplyLimit = defaults.DailyAutoApplyLimit, AutoApplyPaused = defaults.AutoApplyPaused,
    };

    private Task EnsureImportedAsync(CancellationToken ct) => _policies.EnsureImportedAsync(Key, AutomationPolicyJsonContext.Default.AutomationPolicy,
        (context, token) => ImportAsync(context, token, removeLegacy: true), "legacy-import", ct);

    private async Task<AutomationPolicy> ImportAsync(JobFinderDbContext context, CancellationToken ct, bool removeLegacy)
    {
        var legacy = await context.Settings.Where(setting => LegacyKeys.Contains(setting.Key)).ToListAsync(ct);
        var policy = Defaults();
        foreach (var setting in legacy) Set(policy, setting.Key, setting.Value);
        if (policy.Validate() is { } problem) throw new InvalidOperationException("existing automation settings are invalid: " + problem);
        if (removeLegacy) context.Settings.RemoveRange(legacy);
        return policy;
    }

    private static void Set(AutomationPolicy policy, string key, string value)
    {
        switch (key)
        {
            case RuntimeSettings.AutoApplyThreshold: policy.AutoApplyThreshold = int.Parse(value, CultureInfo.InvariantCulture); break;
            case RuntimeSettings.ApprovalThreshold: policy.ApprovalThreshold = int.Parse(value, CultureInfo.InvariantCulture); break;
            case RuntimeSettings.DailyAutoApplyLimit: policy.DailyAutoApplyLimit = int.Parse(value, CultureInfo.InvariantCulture); break;
            case RuntimeSettings.AutoApplyPaused: policy.AutoApplyPaused = bool.Parse(value); break;
            case RuntimeSettings.ApplyDryRun: policy.ApplyDryRun = bool.Parse(value); break;
            case RuntimeSettings.CommentsPaused: policy.CommentsPaused = bool.Parse(value); break;
            case RuntimeSettings.ChatAutoReply: policy.ChatAutoReply = bool.Parse(value); break;
            default: throw new ArgumentException("unknown automation policy field", nameof(key));
        }
    }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(AutomationPolicy))]
internal sealed partial class AutomationPolicyJsonContext : JsonSerializerContext;
