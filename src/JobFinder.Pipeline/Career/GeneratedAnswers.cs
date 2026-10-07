using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using JobFinder.Core.Domain;
using JobFinder.Core.Profile;
using JobFinder.Data;
using Microsoft.EntityFrameworkCore;

namespace JobFinder.Pipeline.Career;

/// <summary>What is being asked for: the static key, where it belongs, and how to hash the current values of its dependencies.</summary>
public sealed record AnswerRequest(string Key, string Scope, string QuestionId, Func<IReadOnlyList<string>, string> DepsHash);

/// <summary>A freshly generated value with what it depends on and what it cost.</summary>
public sealed record Generated(string Value, IReadOnlyList<string> Deps, string? Model, string Purpose, string PromptVersion, decimal CostUsd,
    Core.Artifacts.ArtifactRecipe? Recipe = null);

/// <summary>A stored answer and the dependencies it was generated with.</summary>
public sealed record StoredAnswer(string Value, IReadOnlyList<string> Deps);

/// <summary>The durable layer: generated answers by key, accepted only while their dependencies hash the same.</summary>
public interface IGeneratedAnswerStore
{
    Task<StoredAnswer?> GetAsync(string key, Func<IReadOnlyList<string>, string> depsHash, CancellationToken ct);

    async Task<StoredAnswer?> GetAsync(string key, Func<IReadOnlyList<string>, string> depsHash, CancellationToken ct, Func<string, bool>? accept)
    {
        var stored = await GetAsync(key, depsHash, ct);
        return stored is not null && (accept is null || accept(stored.Value)) ? stored : null;
    }

    Task PutAsync(AnswerRequest request, Generated generated, string depsHash, CancellationToken ct);

    /// <summary>What reuse saved: the cost of each stored answer times the times it was reused.</summary>
    Task<decimal> SavedUsdAsync(CancellationToken ct);
}

/// <summary>Generated answers in PostgreSQL (<c>generated_answers</c>): survive restarts, part of the daily backup, nothing extra to run.</summary>
public sealed class PgGeneratedAnswerStore(IDbContextFactory<JobFinderDbContext> db, TimeProvider time) : IGeneratedAnswerStore
{
    public async Task<StoredAnswer?> GetAsync(string key, Func<IReadOnlyList<string>, string> depsHash, CancellationToken ct)
        => await GetAsync(key, depsHash, ct, null);

    public async Task<StoredAnswer?> GetAsync(string key, Func<IReadOnlyList<string>, string> depsHash, CancellationToken ct, Func<string, bool>? accept)
    {
        await using var ctx = await db.CreateDbContextAsync(ct);
        var row = await ctx.GeneratedAnswers.FirstOrDefaultAsync(e => e.Key == key, ct);
        if (row is null || row.DepsHash != depsHash(row.Deps) || accept is not null && !accept(row.Value)) return null;
        row.Hits++;
        row.LastUsedAt = time.GetUtcNow();
        await ctx.SaveChangesAsync(ct);
        return new StoredAnswer(row.Value, row.Deps);
    }

    public async Task PutAsync(AnswerRequest request, Generated generated, string depsHash, CancellationToken ct)
    {
        await using var ctx = await db.CreateDbContextAsync(ct);
        var now = time.GetUtcNow();
        var row = await ctx.GeneratedAnswers.FirstOrDefaultAsync(e => e.Key == request.Key, ct);
        if (row is null)
        {
            row = new GeneratedAnswer { Key = request.Key, Value = generated.Value };
            ctx.GeneratedAnswers.Add(row);
        }

        row.Scope = request.Scope;
        row.QuestionId = request.QuestionId;
        row.Value = generated.Value;
        row.Deps = [.. generated.Deps];
        row.DepsHash = depsHash;
        row.Model = generated.Model;
        row.Purpose = generated.Purpose;
        row.PromptVersion = generated.PromptVersion;
        row.Recipe = generated.Recipe;
        row.CostUsd = generated.CostUsd;
        row.CreatedAt = now;
        row.LastUsedAt = now;
        await ctx.SaveChangesAsync(ct);
    }

    public async Task<decimal> SavedUsdAsync(CancellationToken ct)
    {
        await using var ctx = await db.CreateDbContextAsync(ct);
        return await ctx.GeneratedAnswers.SumAsync(e => e.CostUsd * e.Hits, ct);
    }
}

/// <summary>
/// Durable memoization of expensive LLM work. L1: this process's memory for 30 minutes (dependencies re-checked on every read) and a
/// single flight per key, so the same answer asked for twice at once costs one call. L2: <see cref="IGeneratedAnswerStore"/>. Miss:
/// the generator runs and the result is stored with the dependencies it actually used.
/// </summary>
public sealed class GeneratedAnswers(IGeneratedAnswerStore store, TimeProvider time)
{
    private static readonly TimeSpan L1Ttl = TimeSpan.FromMinutes(30);

    private readonly ConcurrentDictionary<string, (string Value, IReadOnlyList<string> Deps, string DepsHash, DateTimeOffset At)> _l1 = new();
    private readonly ConcurrentDictionary<string, Lazy<Task<(string Value, bool Reused)>>> _inflight = new();

    public async Task<(string Value, bool Reused)> GetOrCreateAsync(AnswerRequest request, Func<CancellationToken, Task<Generated>> generate,
        CancellationToken ct, Func<string, bool>? accept = null)
    {
        var flight = _inflight.GetOrAdd(request.Key, _ => new Lazy<Task<(string, bool)>>(() => ResolveAsync(request, generate, ct, accept)));
        try
        {
            var result = await flight.Value;
            if (accept is not null && !accept(result.Value)) throw new InvalidOperationException("shared generated answer failed the caller's persisted-content check");
            return result;
        }
        finally
        {
            _inflight.TryRemove(new KeyValuePair<string, Lazy<Task<(string Value, bool Reused)>>>(request.Key, flight));
        }
    }

    private async Task<(string Value, bool Reused)> ResolveAsync(AnswerRequest request, Func<CancellationToken, Task<Generated>> generate,
        CancellationToken ct, Func<string, bool>? accept)
    {
        var now = time.GetUtcNow();
        if (_l1.TryGetValue(request.Key, out var l1) && now - l1.At < L1Ttl && l1.DepsHash == request.DepsHash(l1.Deps)
            && (accept is null || accept(l1.Value)))
        {
            Count(request.Scope, "memory");
            return (l1.Value, true);
        }

        if (await store.GetAsync(request.Key, request.DepsHash, ct, accept) is { } stored)
        {
            _l1[request.Key] = (stored.Value, stored.Deps, request.DepsHash(stored.Deps), now);
            Count(request.Scope, "store");
            return (stored.Value, true);
        }

        var generated = await generate(ct);
        if (accept is not null && !accept(generated.Value)) throw new InvalidOperationException("generated answer failed its persisted-content check");
        var depsHash = request.DepsHash(generated.Deps);
        await store.PutAsync(request, generated, depsHash, ct);
        _l1[request.Key] = (generated.Value, generated.Deps, depsHash, now);
        Count(request.Scope, "generated");
        return (generated.Value, false);
    }

    private static void Count(string scope, string source) =>
        Core.Telemetry.Telemetry.Answers.Add(1, new TagList { { "scope", scope }, { "source", source } });
}

/// <summary>Fingerprints: stable hashes of versioned generation inputs.</summary>
public static class Fingerprint
{
    /// <summary>A short stable hash of the parts (null and empty differ from each other).</summary>
    public static string Of(params string?[] parts)
    {
        var sb = new StringBuilder();
        foreach (var p in parts)
            sb.Append(p is null ? "~" : p.Length.ToString(System.Globalization.CultureInfo.InvariantCulture)).Append(':').Append(p).Append('\u001f');
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString())))[..32];
    }

    /// <summary>
    /// The current values of the fact keys an answer depends on ("funnel" = the state of the search): a change of any of them, and
    /// only of them, changes the hash.
    /// </summary>
    public static string OfFacts(CandidateFacts facts, IEnumerable<string> deps, string? funnel = null) =>
        Of([.. deps.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).Select(k => k == "funnel"
            ? $"funnel={funnel}"
            : facts.Get(k) is { } f ? $"{k}={f.Answer}|{f.Confirmed}" : $"{k}=-")]);
}
