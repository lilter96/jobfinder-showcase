using System.Text.Json;
using JobFinder.Core.Domain;
using JobFinder.Data;
using JobFinder.Llm;
using JobFinder.Llm.Configuration;
using Microsoft.EntityFrameworkCore;

namespace JobFinder.Pipeline.Settings;

public sealed class LlmPolicyStore(PgPolicyStore policies, LlmOptions deployment) : ILlmPolicySource
{
    public const string Key = "policy.llm";
    private readonly PgPolicyStore _policies = policies;

    public async Task PreviewAsync(CancellationToken ct)
    {
        var snapshot = await _policies.PreviewAsync(Key, RuntimePolicyJsonContext.Default.LlmRuntimePolicy,
            (_, _) => Task.FromResult(LlmRuntimePolicyCompiler.Import(deployment)), ct);
        LlmRuntimePolicyCompiler.Compile(LlmRuntimePolicyCompiler.Clone(snapshot.Value), deployment);
    }

    public async Task<LlmPolicySnapshot> ReadAsync(CancellationToken ct)
    {
        await EnsureImportedAsync(ct);
        var snapshot = await _policies.ReadAsync(Key, RuntimePolicyJsonContext.Default.LlmRuntimePolicy, ct);
        var value = LlmRuntimePolicyCompiler.Clone(snapshot.Value);
        LlmRuntimePolicyCompiler.Compile(value, deployment);
        return new LlmPolicySnapshot(value, snapshot.Version);
    }

    public async Task<LlmPolicySnapshot> PublishAsync(LlmRuntimePolicy policy, long expectedVersion, string actor, CancellationToken ct)
    {
        var frozen = LlmRuntimePolicyCompiler.Clone(policy);
        var result = await _policies.PublishAsync(Key, frozen, expectedVersion, RuntimePolicyJsonContext.Default.LlmRuntimePolicy,
            value => { LlmRuntimePolicyCompiler.Compile(value, deployment); return null; }, actor, ct);
        return new LlmPolicySnapshot(LlmRuntimePolicyCompiler.Clone(result.Value), result.Version);
    }

    private Task EnsureImportedAsync(CancellationToken ct) => _policies.EnsureImportedAsync(Key, RuntimePolicyJsonContext.Default.LlmRuntimePolicy,
        (_, _) => Task.FromResult(LlmRuntimePolicyCompiler.Import(deployment)), "effective-options-import", ct);
}
