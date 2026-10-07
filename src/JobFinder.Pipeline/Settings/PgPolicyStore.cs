using System.Text.Json;
using System.Collections.Concurrent;
using System.Text.Json.Serialization.Metadata;
using JobFinder.Core.Domain;
using JobFinder.Core.Storage;
using JobFinder.Data;
using JobFinder.Pipeline.Notifications;
using Microsoft.EntityFrameworkCore;
using Wolverine.EntityFrameworkCore;

namespace JobFinder.Pipeline.Settings;

public sealed record PolicySnapshot<T>(T Value, long Version);

public sealed class PolicyConflictException(string key, long expectedVersion) : Exception($"policy {key} no longer has revision {expectedVersion}");

public sealed class PgPolicyStore(IDbContextFactory<JobFinderDbContext> db, TimeProvider time, NotificationDeliveryRequests notifications)
{
    private readonly ConcurrentDictionary<(string Key, Type Type), object> _caches = new();

    public async Task<PolicySnapshot<T>> PreviewAsync<T>(string key, JsonTypeInfo<T> type,
        Func<JobFinderDbContext, CancellationToken, Task<T>> import, CancellationToken ct) where T : class
    {
        await using var context = await db.CreateDbContextAsync(ct);
        if (await context.Documents.AnyAsync(document => document.Key == key, ct)) return await ReadAsync(key, type, ct);
        if (await context.PolicyRevisions.AnyAsync(revision => revision.Key == key, ct))
            throw new JsonException($"policy {key} is missing after a previous publication; restore it rather than reimporting defaults");
        return new PolicySnapshot<T>(await import(context, ct), 0);
    }

    public async Task EnsureImportedAsync<T>(string key, JsonTypeInfo<T> type, Func<JobFinderDbContext, CancellationToken, Task<T>> import,
        string actor, CancellationToken ct) where T : class
    {
        await using var context = await db.CreateDbContextAsync(ct);
        if (await context.Documents.AnyAsync(document => document.Key == key, ct)) return;
        await using var transaction = await context.Database.BeginTransactionAsync(ct);
        await context.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({key}, 0))", ct);
        if (await context.Documents.AnyAsync(document => document.Key == key, ct)) return;
        if (await context.PolicyRevisions.AnyAsync(revision => revision.Key == key, ct))
            throw new JsonException($"policy {key} is missing after a previous publication; restore it rather than reimporting defaults");
        var value = await import(context, ct);
        var json = JsonSerializer.Serialize(value, type);
        var now = time.GetUtcNow();
        context.Documents.Add(new AppDocument { Key = key, Content = json, Version = 1, UpdatedAt = now });
        AddPublication(context, key, 1, json, actor, now);
        await notifications.SaveAndFlushAsync(context, ct);
    }

    public async Task<PolicySnapshot<T>> ReadAsync<T>(string key, JsonTypeInfo<T> type, CancellationToken ct) where T : class
    {
        var cache = (DocumentCache<T>)_caches.GetOrAdd((key, typeof(T)), _ => new DocumentCache<T>(new Storage.PgDocumentStore(db, time),
            key, type, Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance));
        var document = await cache.GetVersionedAsync(ct) ?? throw new JsonException($"policy {key} is missing or null");
        return new PolicySnapshot<T>(document.Value, document.Version);
    }

    public async Task<PolicySnapshot<T>> PublishAsync<T>(string key, T value, long expectedVersion, JsonTypeInfo<T> type,
        Func<T, string?> validate, string actor, CancellationToken ct) where T : class
    {
        await using var context = await db.CreateDbContextAsync(ct);
        await using var transaction = await context.Database.BeginTransactionAsync(ct);
        var published = await PublishCoreAsync(context, key, value, expectedVersion, type, validate, actor, ct);
        await notifications.SaveAndFlushAsync(context, ct);
        return published;
    }

    internal Task<PolicySnapshot<T>> PublishJoinedAsync<T>(JobFinderDbContext context, IDbContextOutbox outbox, string key,
        T value, long expectedVersion, JsonTypeInfo<T> type, Func<T, string?> validate, string actor, CancellationToken ct) where T : class
    {
        if (context.Database.CurrentTransaction is null || !ReferenceEquals(outbox.ActiveContext, context))
            throw new InvalidOperationException("joined policy publication requires its enrolled source transaction");
        return PublishCoreAsync(context, key, value, expectedVersion, type, validate, actor, ct);
    }

    private async Task<PolicySnapshot<T>> PublishCoreAsync<T>(JobFinderDbContext context, string key, T value, long expectedVersion,
        JsonTypeInfo<T> type, Func<T, string?> validate, string actor, CancellationToken ct) where T : class
    {
        if (validate(value) is { } problem) throw new ArgumentException(problem, nameof(value));
        if (string.IsNullOrWhiteSpace(actor)) throw new ArgumentException("actor is required", nameof(actor));
        var json = JsonSerializer.Serialize(value, type);
        var now = time.GetUtcNow();
        var changed = await context.Documents.Where(document => document.Key == key && document.Version == expectedVersion)
            .ExecuteUpdateAsync(update => update.SetProperty(document => document.Content, json)
                .SetProperty(document => document.Version, document => document.Version + 1)
                .SetProperty(document => document.UpdatedAt, now), ct);
        if (changed != 1) throw new PolicyConflictException(key, expectedVersion);
        AddPublication(context, key, expectedVersion + 1, json, actor, now);
        return new PolicySnapshot<T>(value, expectedVersion + 1);
    }

    internal static void AddPublication(JobFinderDbContext context, string key, long version, string json, string actor, DateTimeOffset now)
    {
        context.PolicyRevisions.Add(new PolicyRevision { Key = key, Version = version, Content = json, Actor = actor, PublishedAt = now });
        context.Notifications.Add(new Notification
        {
            Kind = NotificationKind.Info, Status = NotificationStatus.Pending, CreatedAt = now,
            Text = $"⚙️ Настройки обновлены: {Core.Text.TextUtil.EscapeHtml(key)} · версия {version}",
        });
    }
}
