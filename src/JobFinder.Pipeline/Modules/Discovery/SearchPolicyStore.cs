using System.Text.Json;
using JobFinder.Core.Domain;
using JobFinder.Core.Filtering;
using JobFinder.Core.Options;
using JobFinder.Core.Policies;
using JobFinder.Data;
using JobFinder.Integrations.Habr;
using JobFinder.Integrations.HeadHunter;
using JobFinder.Integrations.Threads;
using JobFinder.Pipeline.Settings;
using Microsoft.EntityFrameworkCore;

namespace JobFinder.Pipeline.Modules.Discovery;

public sealed class SearchPolicyStore(PgPolicyStore policies, FilterOptions filter, PrefilterOptions prefilter,
    HhOptions hh, HabrOptions habr, ThreadsOptions threads)
{
    public const string Key = "policy.search";
    private readonly PgPolicyStore _policies = policies;

    public async Task PreviewAsync(CancellationToken ct)
    {
        var snapshot = await _policies.PreviewAsync(Key, SearchPolicyJsonContext.Default.SearchPolicy,
            (_, _) => Task.FromResult(SearchPolicyCompiler.Import(filter, prefilter, hh, habr, threads)), ct);
        SearchPolicyCompiler.Validate(SearchPolicyCompiler.Clone(snapshot.Value));
    }

    public async Task<SearchPolicySnapshot> ReadAsync(CancellationToken ct)
    {
        await EnsureImportedAsync(ct);
        var snapshot = await _policies.ReadAsync(Key, SearchPolicyJsonContext.Default.SearchPolicy, ct);
        var value = SearchPolicyCompiler.Clone(snapshot.Value);
        SearchPolicyCompiler.Validate(value);
        return new SearchPolicySnapshot(value, snapshot.Version);
    }

    public async Task<SearchPolicySnapshot> PublishAsync(SearchPolicy policy, long expectedVersion, string actor, CancellationToken ct)
    {
        var frozen = SearchPolicyCompiler.Clone(policy);
        var result = await _policies.PublishAsync(Key, frozen, expectedVersion, SearchPolicyJsonContext.Default.SearchPolicy,
            value => { SearchPolicyCompiler.Validate(value); return null; }, actor, ct);
        return new SearchPolicySnapshot(SearchPolicyCompiler.Clone(result.Value), result.Version);
    }

    private Task EnsureImportedAsync(CancellationToken ct) => _policies.EnsureImportedAsync(Key, SearchPolicyJsonContext.Default.SearchPolicy,
        (_, _) => Task.FromResult(SearchPolicyCompiler.Import(filter, prefilter, hh, habr, threads)), "effective-options-import", ct);
}
