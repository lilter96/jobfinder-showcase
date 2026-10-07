using JobFinder.Core.Policies;
using JobFinder.Data;
using JobFinder.Integrations.Threads;
using JobFinder.Pipeline.Settings;
using Microsoft.EntityFrameworkCore;

namespace JobFinder.Pipeline.Modules.Engagement;

public sealed class EngagementPolicyStore(PgPolicyStore policies, ThreadsOptions bootstrap)
{
    public const string Key = "policy.engagement";
    private readonly PgPolicyStore _policies = policies;

    public async Task PreviewAsync(CancellationToken ct)
    {
        var snapshot = await _policies.PreviewAsync(Key, EngagementPolicyJsonContext.Default.EngagementPolicy,
            (_, _) => Task.FromResult(Import()), ct);
        if (snapshot.Value.Validate() is { } problem) throw new InvalidOperationException(problem);
    }

    public async Task<PolicySnapshot<EngagementPolicy>> ReadAsync(CancellationToken ct)
    {
        await _policies.EnsureImportedAsync(Key, EngagementPolicyJsonContext.Default.EngagementPolicy,
            (_, _) => Task.FromResult(Import()), "effective-options-import", ct);
        var snapshot = await _policies.ReadAsync(Key, EngagementPolicyJsonContext.Default.EngagementPolicy, ct);
        if (snapshot.Value.Validate() is { } problem) throw new InvalidOperationException(problem);
        return new PolicySnapshot<EngagementPolicy>(snapshot.Value with { }, snapshot.Version);
    }

    public async Task<PolicySnapshot<EngagementPolicy>> PublishAsync(EngagementPolicy policy, long expectedVersion, string actor, CancellationToken ct)
    {
        var snapshot = await _policies.PublishAsync(Key, policy with { }, expectedVersion, EngagementPolicyJsonContext.Default.EngagementPolicy,
            value => value.Validate(), actor, ct);
        return new PolicySnapshot<EngagementPolicy>(snapshot.Value with { }, snapshot.Version);
    }

    private EngagementPolicy Import()
    {
        var policy = new EngagementPolicy
        {
            AutoPublish = bootstrap.AutoPublish, MaxDraftsPerDay = bootstrap.MaxDraftsPerDay, AuthorCooldownDays = bootstrap.AuthorCooldownDays,
        };
        if (policy.Validate() is { } error) throw new InvalidOperationException("existing engagement preferences are invalid: " + error);
        return policy;
    }
}
