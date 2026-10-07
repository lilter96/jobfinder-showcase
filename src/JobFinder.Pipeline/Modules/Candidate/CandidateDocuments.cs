using System.Text.Json;
using JobFinder.Core.Domain;
using JobFinder.Core.Profile;
using JobFinder.Core.Storage;
using JobFinder.Core.Telemetry;
using JobFinder.Data;
using JobFinder.Pipeline.Notifications;
using JobFinder.Pipeline.Profile;
using Microsoft.EntityFrameworkCore;
using Wolverine.EntityFrameworkCore;

namespace JobFinder.Pipeline.Modules.Candidate;

public sealed class CandidateConflictException(string key, long expectedVersion)
    : Exception($"документ {key} уже изменён: ожидалась версия {expectedVersion}");

public interface ICandidatePublisher
{
    Task<VersionedDocument<CandidateProfile>> PublishProfileAsync(CandidateProfile profile, long expectedVersion, string actor,
        CancellationToken ct, bool keepPrevious = false);

    Task<VersionedDocument<CandidateFacts>> PublishFactsAsync(CandidateFacts facts, long expectedVersion, string actor, CancellationToken ct);

    Task<bool> EnsureFactsAsync(CancellationToken ct);
}

public sealed class CandidateDocuments(IDbContextFactory<JobFinderDbContext> db, TimeProvider time,
    NotificationDeliveryRequests notifications) : ICandidatePublisher
{
    public async Task<VersionedDocument<CandidateProfile>> PublishProfileAsync(CandidateProfile profile, long expectedVersion, string actor,
        CancellationToken ct, bool keepPrevious = false)
    {
        var json = ProfileProvider.ToJson(profile);
        var frozen = ProfileProvider.FromJson(json);
        if (frozen.Skills is null || frozen.Domains is null || frozen.Achievements is null || frozen.Experience is null
            || frozen.WorkFormats is null || frozen.DealBreakers is null || frozen.Preferences is null || frozen.Contacts is null)
            throw new ArgumentException("коллекции и контакты профиля не могут быть null", nameof(profile));
        var version = await PublishAsync(ProfileProvider.Key, json, expectedVersion, actor, keepPrevious, ct);
        return new VersionedDocument<CandidateProfile>(frozen, version);
    }

    public async Task<VersionedDocument<CandidateFacts>> PublishFactsAsync(CandidateFacts facts, long expectedVersion, string actor, CancellationToken ct)
    {
        var (json, frozen) = FactsDocument(facts);
        var version = await PublishAsync(FactsProvider.Key, json, expectedVersion, actor, false, ct);
        return new VersionedDocument<CandidateFacts>(frozen.WithStandardQuestions(), version);
    }

    internal async Task<VersionedDocument<CandidateFacts>> PublishFactsAsync(JobFinderDbContext context, IDbContextOutbox outbox, CandidateFacts facts,
        long expectedVersion, string actor, CancellationToken ct)
    {
        if (context.Database.CurrentTransaction is null || !ReferenceEquals(outbox.ActiveContext, context))
            throw new InvalidOperationException("candidate publication requires the enrolled source transaction");
        var (json, frozen) = FactsDocument(facts);
        var version = await StageAsync(context, FactsProvider.Key, json, expectedVersion, actor, false, ct);
        await notifications.SaveJoinedAsync(context, outbox, ct);
        return new VersionedDocument<CandidateFacts>(frozen.WithStandardQuestions(), version);
    }

    private static (string Json, CandidateFacts Frozen) FactsDocument(CandidateFacts facts)
    {
        var json = FactsProvider.Normalize(JsonSerializer.Serialize(facts, FactsJsonContext.Default.CandidateFacts));
        var frozen = JsonSerializer.Deserialize(json, FactsJsonContext.Default.CandidateFacts)!;
        if (frozen.Facts is null || frozen.Facts.Any(fact => fact is null || string.IsNullOrWhiteSpace(fact.Key)
                || string.IsNullOrWhiteSpace(fact.Question) || fact.Confirmed && string.IsNullOrWhiteSpace(fact.Answer))
            || frozen.Facts.Select(fact => fact.Key).Distinct(StringComparer.Ordinal).Count() != frozen.Facts.Count)
            throw new ArgumentException("факты требуют уникальных ключей, вопросов и непустых подтверждённых ответов", nameof(facts));
        return (json, frozen);
    }

    public async Task<bool> EnsureFactsAsync(CancellationToken ct)
    {
        await using var context = await db.CreateDbContextAsync(ct);
        await using var transaction = await context.Database.BeginTransactionAsync(ct);
        await LockAsync(context, ProfileProvider.Key, ct);
        await LockAsync(context, FactsProvider.Key, ct);
        if (await context.Documents.AnyAsync(document => document.Key == FactsProvider.Key, ct)) return false;
        await RefuseMissingHistoryAsync(context, FactsProvider.Key, ct);
        var profile = await context.Documents.AsNoTracking().SingleOrDefaultAsync(document => document.Key == ProfileProvider.Key, ct);
        if (profile is null) return false;
        var draft = CandidateFacts.Draft(ProfileProvider.FromJson(profile.Content));
        var json = FactsProvider.Normalize(JsonSerializer.Serialize(draft, FactsJsonContext.Default.CandidateFacts));
        var now = time.GetUtcNow();
        context.Documents.Add(new AppDocument { Key = FactsProvider.Key, Content = json, Version = 1, UpdatedAt = now });
        Record(context, FactsProvider.Key, json, 1, "profile-draft", now);
        await notifications.SaveAndFlushAsync(context, ct);
        return true;
    }

    private async Task<long> PublishAsync(string key, string json, long expectedVersion, string actor, bool keepPrevious, CancellationToken ct)
    {
        await using var context = await db.CreateDbContextAsync(ct);
        await using var transaction = await context.Database.BeginTransactionAsync(ct);
        var version = await StageAsync(context, key, json, expectedVersion, actor, keepPrevious, ct);
        try
        {
            await notifications.SaveAndFlushAsync(context, ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            throw new CandidateConflictException(key, expectedVersion);
        }
        return version;
    }

    private async Task<long> StageAsync(JobFinderDbContext context, string key, string json, long expectedVersion, string actor,
        bool keepPrevious, CancellationToken ct)
    {
        if (context.Database.CurrentTransaction is null) throw new InvalidOperationException("candidate publication requires a transaction");
        if (expectedVersion is < 0 or long.MaxValue) throw new ArgumentOutOfRangeException(nameof(expectedVersion));
        if (string.IsNullOrWhiteSpace(actor) || actor.Length > 128) throw new ArgumentException("actor must contain 1..128 characters", nameof(actor));
        using var stage = Telemetry.StartStage("publish", "candidate").Tag("candidate.document", key).Tag("candidate.expected_version", expectedVersion);
        await LockAsync(context, key, ct);
        var document = await context.Documents.SingleOrDefaultAsync(document => document.Key == key, ct);
        if ((document?.Version ?? 0) != expectedVersion) throw new CandidateConflictException(key, expectedVersion);
        if (document is null) await RefuseMissingHistoryAsync(context, key, ct);
        else
        {
            await context.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO candidate_revisions (key, version, content, actor, published_at)
                VALUES ({key}, {document.Version}, {document.Content}::json, 'legacy-import', {document.UpdatedAt})
                ON CONFLICT (key, version) DO NOTHING
                """, ct);
            if (keepPrevious)
                await context.Database.ExecuteSqlInterpolatedAsync($"""
                    INSERT INTO app_documents (key, content, version, updated_at)
                    VALUES ({ProfileProvider.PreviousKey}, {document.Content}::json, 1, {time.GetUtcNow()})
                    ON CONFLICT (key) DO UPDATE SET content = excluded.content, version = app_documents.version + 1, updated_at = excluded.updated_at
                    """, ct);
        }
        var now = time.GetUtcNow();
        if (document is null)
        {
            document = new AppDocument { Key = key, Content = json, Version = 1, UpdatedAt = now };
            context.Documents.Add(document);
        }
        else
        {
            document.Content = json;
            document.Version++;
            document.UpdatedAt = now;
        }
        Record(context, key, json, document.Version, actor, now);
        return document.Version;
    }

    private static Task LockAsync(JobFinderDbContext context, string key, CancellationToken ct) =>
        context.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({key}, 0))", ct);

    private static async Task RefuseMissingHistoryAsync(JobFinderDbContext context, string key, CancellationToken ct)
    {
        if (await context.CandidateRevisions.AnyAsync(revision => revision.Key == key, ct))
            throw new JsonException($"документ {key} удалён после публикации: восстановите его, повторный импорт остановлен");
    }

    private static void Record(JobFinderDbContext context, string key, string json, long version, string actor, DateTimeOffset now)
    {
        context.CandidateRevisions.Add(new CandidateRevision { Key = key, Content = json, Version = version, Actor = actor, PublishedAt = now });
        context.Notifications.Add(new Notification
        {
            Kind = NotificationKind.Info, Status = NotificationStatus.Pending, CreatedAt = now,
            Text = $"👤 Данные кандидата обновлены: {key} · версия {version}",
        });
    }
}
