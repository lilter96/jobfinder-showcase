using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace JobFinder.Core.Storage;

/// <summary>A document as stored: its JSON text, its version (every write bumps it, the first one is 1) and when it was written.</summary>
public sealed record StoredDocument(string Json, long Version, DateTimeOffset UpdatedAt);

public sealed record VersionedDocument<T>(T Value, long Version);

/// <summary>
/// Named JSON documents in the database: the state that is one piece rather than rows — the candidate profile and facts, a site's browser
/// login, the Telegram updates state. Every process (the server, a CLI command on the host or in a container) sees the same state, and the
/// daily database backup carries it. Keys are dotted names owned by one class each (<c>candidate.profile</c>, <c>browser.hh</c>).
/// </summary>
public interface IDocumentStore
{
    Task<StoredDocument?> GetAsync(string key, CancellationToken ct);

    /// <summary>The version alone (null = no document): the cheap check behind every cached read.</summary>
    Task<long?> VersionAsync(string key, CancellationToken ct);

    /// <summary>Writes the document (creates or replaces it) and returns its new version.</summary>
    Task<long> SaveAsync(string key, string json, CancellationToken ct);

    /// <summary>Writes the document only when the key is free: an import never overwrites what the database already holds. True = written.</summary>
    Task<bool> AddAsync(string key, string json, CancellationToken ct);

    Task<bool> DeleteAsync(string key, CancellationToken ct);
}

/// <summary>
/// A typed document kept in memory while its version stands: a read costs one small query (the version), and the text is fetched and
/// parsed again only after a write — from this process or another one (a CLI command, the dashboard). A version that does not parse
/// (edited by hand in SQL) keeps the last good value and is reported once.
/// </summary>
/// <param name="read">How the stored text is read.</param>
/// <param name="write">How a value is written (indentation, readable Cyrillic); defaults to <paramref name="read"/>.</param>
/// <param name="loaded">Applied once to every value read from the store (defaults filled in), not on each access.</param>
public sealed class DocumentCache<T>(IDocumentStore store, string key, JsonTypeInfo<T> read, ILogger log, JsonTypeInfo<T>? write = null,
    Func<T, T>? loaded = null) where T : class
{
    private sealed record Entry(long Version, T? Value, bool Valid = true);

    // Version 0: nothing read yet (stored versions start at 1).
    private Entry _entry = new(0, null);

    public string Key => key;

    public async Task<T?> GetAsync(CancellationToken ct) => (await ReadEntryAsync(ct))?.Value;

    public async Task<VersionedDocument<T>?> GetVersionedAsync(CancellationToken ct)
    {
        var entry = await ReadEntryAsync(ct);
        if (entry is { Valid: false }) throw new JsonException($"document {key} v{entry.Version} does not parse");
        return entry?.Value is { } value ? new VersionedDocument<T>(value, entry.Version) : null;
    }

    private async Task<Entry?> ReadEntryAsync(CancellationToken ct)
    {
        var entry = Volatile.Read(ref _entry);
        var version = await store.VersionAsync(key, ct);
        if (version is null)
        {
            Volatile.Write(ref _entry, new Entry(0, null));
            return null;
        }

        if (version == entry.Version) return entry;
        var doc = await store.GetAsync(key, ct);
        if (doc is null) return null;
        try
        {
            var value = JsonSerializer.Deserialize(doc.Json, read);
            if (value is not null && loaded is not null) value = loaded(value);
            var refreshed = new Entry(doc.Version, value);
            Volatile.Write(ref _entry, refreshed);
            return refreshed;
        }
        catch (JsonException e)
        {
            // Remember the bad version with the last good value: the error is logged once, not on every read.
            log.Error($"[store] {key} v{doc.Version} does not parse, keeping v{entry.Version}: {e.Message}");
            var invalid = entry with { Version = doc.Version, Valid = false };
            Volatile.Write(ref _entry, invalid);
            return invalid;
        }
    }

    public async Task SaveAsync(T value, CancellationToken ct)
    {
        var json = Serialize(value);
        var frozen = JsonSerializer.Deserialize(json, read);
        var version = await store.SaveAsync(key, json, ct);
        Volatile.Write(ref _entry, new Entry(version, frozen));
    }

    /// <summary>Stores <paramref name="value"/> only when there is no document yet (a draft must not replace what someone saved meanwhile).</summary>
    public async Task<bool> AddAsync(T value, CancellationToken ct) => await store.AddAsync(key, Serialize(value), ct);

    public string Serialize(T value) => JsonSerializer.Serialize(value, write ?? read);
}
