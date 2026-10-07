using System.Collections.Concurrent;
using System.Text.Json;
using JobFinder.Core.Profile;
using JobFinder.Core.Storage;
using JobFinder.Integrations.Telegram;
using JobFinder.Pipeline.Modules.Candidate;
using JobFinder.Pipeline.Profile;

namespace JobFinder.Tests;

/// <summary><see cref="IDocumentStore"/> in memory, with the database's version rules (the first write is 1, every write bumps it).</summary>
public sealed class MemoryDocumentStore : IDocumentStore, ICandidatePublisher
{
    private readonly ConcurrentDictionary<string, StoredDocument> _documents = new(StringComparer.Ordinal);

    public int Reads { get; private set; }

    public Task<StoredDocument?> GetAsync(string key, CancellationToken ct)
    {
        Reads++;
        return Task.FromResult(_documents.GetValueOrDefault(key));
    }

    public Task<long?> VersionAsync(string key, CancellationToken ct) => Task.FromResult(_documents.TryGetValue(key, out var d) ? d.Version : (long?)null);

    public Task<long> SaveAsync(string key, string json, CancellationToken ct) =>
        Task.FromResult(_documents.AddOrUpdate(key, _ => new(json, 1, DateTimeOffset.UtcNow), (_, old) => new(json, old.Version + 1, DateTimeOffset.UtcNow)).Version);

    public Task<bool> AddAsync(string key, string json, CancellationToken ct) => Task.FromResult(_documents.TryAdd(key, new(json, 1, DateTimeOffset.UtcNow)));

    public Task<bool> DeleteAsync(string key, CancellationToken ct) => Task.FromResult(_documents.TryRemove(key, out _));

    public Task<VersionedDocument<CandidateProfile>> PublishProfileAsync(CandidateProfile profile, long expectedVersion, string actor,
        CancellationToken ct, bool keepPrevious = false)
    {
        var json = ProfileProvider.ToJson(profile);
        var version = Publish(ProfileProvider.Key, json, expectedVersion);
        return Task.FromResult(new VersionedDocument<CandidateProfile>(ProfileProvider.FromJson(json), version));
    }

    public Task<VersionedDocument<CandidateFacts>> PublishFactsAsync(CandidateFacts facts, long expectedVersion, string actor, CancellationToken ct)
    {
        var json = FactsProvider.Normalize(JsonSerializer.Serialize(facts, FactsJsonContext.Default.CandidateFacts));
        var version = Publish(FactsProvider.Key, json, expectedVersion);
        return Task.FromResult(new VersionedDocument<CandidateFacts>(JsonSerializer.Deserialize(json, FactsJsonContext.Default.CandidateFacts)!, version));
    }

    public async Task<bool> EnsureFactsAsync(CancellationToken ct)
    {
        if (await GetAsync(ProfileProvider.Key, ct) is not { } profile) return false;
        var facts = CandidateFacts.Draft(ProfileProvider.FromJson(profile.Json));
        return await AddAsync(FactsProvider.Key, JsonSerializer.Serialize(facts, FactsJsonContext.Default.CandidateFacts), ct);
    }

    private long Publish(string key, string json, long expectedVersion)
    {
        var current = _documents.GetValueOrDefault(key);
        var saved = new StoredDocument(json, expectedVersion + 1, DateTimeOffset.UtcNow);
        if ((current?.Version ?? 0) != expectedVersion || !(current is null ? _documents.TryAdd(key, saved) : _documents.TryUpdate(key, saved, current)))
            throw new CandidateConflictException(key, expectedVersion);
        return saved.Version;
    }
}

/// <summary>A Telegram session store that is never logged in (tests do not connect to Telegram).</summary>
public sealed class NoTelegramSession : ITelegramSessionStore
{
    public Task<byte[]?> LoadAsync(CancellationToken ct) => Task.FromResult<byte[]?>(null);

    public Task SaveAsync(byte[] session, CancellationToken ct) => Task.CompletedTask;
}
