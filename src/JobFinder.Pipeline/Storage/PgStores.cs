using JobFinder.Core.Storage;
using JobFinder.Data;
using JobFinder.Integrations.Telegram;
using JobFinder.Pipeline.Profile;
using Microsoft.EntityFrameworkCore;

namespace JobFinder.Pipeline.Storage;

/// <summary><see cref="IDocumentStore"/> over <c>app_documents</c>. A write is one statement, so two writers never lose a version.</summary>
public sealed class PgDocumentStore(IDbContextFactory<JobFinderDbContext> db, TimeProvider time) : IDocumentStore
{
    public async Task<StoredDocument?> GetAsync(string key, CancellationToken ct)
    {
        await using var ctx = await db.CreateDbContextAsync(ct);
        return await ctx.Documents.AsNoTracking().Where(d => d.Key == key)
            .Select(d => new StoredDocument(d.Content, d.Version, d.UpdatedAt)).FirstOrDefaultAsync(ct);
    }

    public async Task<long?> VersionAsync(string key, CancellationToken ct)
    {
        await using var ctx = await db.CreateDbContextAsync(ct);
        return await ctx.Documents.Where(d => d.Key == key).Select(d => (long?)d.Version).FirstOrDefaultAsync(ct);
    }

    public async Task<long> SaveAsync(string key, string json, CancellationToken ct)
    {
        RefuseOwnedWrite(key);
        await using var ctx = await db.CreateDbContextAsync(ct);
        var now = time.GetUtcNow();
        // Not composed (no LINQ on top): Postgres does not take an INSERT … RETURNING as a subquery.
        var versions = await ctx.Database.SqlQuery<long>($"""
            INSERT INTO app_documents (key, content, version, updated_at) VALUES ({key}, {json}::json, 1, {now})
            ON CONFLICT (key) DO UPDATE SET content = excluded.content, version = app_documents.version + 1, updated_at = excluded.updated_at
            RETURNING version AS "Value"
            """).ToListAsync(ct);
        return versions.Single();
    }

    public async Task<bool> AddAsync(string key, string json, CancellationToken ct)
    {
        RefuseOwnedWrite(key);
        await using var ctx = await db.CreateDbContextAsync(ct);
        var now = time.GetUtcNow();
        return await ctx.Database.ExecuteSqlAsync($"""
            INSERT INTO app_documents (key, content, version, updated_at) VALUES ({key}, {json}::json, 1, {now}) ON CONFLICT (key) DO NOTHING
            """, ct) == 1;
    }

    public async Task<bool> DeleteAsync(string key, CancellationToken ct)
    {
        RefuseOwnedWrite(key);
        await using var ctx = await db.CreateDbContextAsync(ct);
        return await ctx.Documents.Where(d => d.Key == key).ExecuteDeleteAsync(ct) > 0;
    }

    private static void RefuseOwnedWrite(string key)
    {
        if (key is ProfileProvider.Key or FactsProvider.Key)
            throw new InvalidOperationException("candidate documents must be published through their versioned Candidate owner");
        if (key.StartsWith("policy.", StringComparison.Ordinal))
            throw new InvalidOperationException("policy documents must be published through their versioned policy owner");
        if (key.StartsWith("bot.edit.", StringComparison.Ordinal))
            throw new InvalidOperationException("bot edit prompts must be published through their captured prompt owner");
    }
}

/// <summary><see cref="ITelegramSessionStore"/> over <c>telegram_sessions</c> (one row: the user account).</summary>
public sealed class PgTelegramSessionStore(IDbContextFactory<JobFinderDbContext> db, TimeProvider time) : ITelegramSessionStore
{
    public const string Name = "user";

    public async Task<byte[]?> LoadAsync(CancellationToken ct)
    {
        await using var ctx = await db.CreateDbContextAsync(ct);
        return await ctx.TelegramSessions.AsNoTracking().Where(s => s.Name == Name).Select(s => s.Data).FirstOrDefaultAsync(ct);
    }

    public async Task SaveAsync(byte[] session, CancellationToken ct)
    {
        await using var ctx = await db.CreateDbContextAsync(ct);
        var now = time.GetUtcNow();
        await ctx.Database.ExecuteSqlAsync($"""
            INSERT INTO telegram_sessions (name, data, updated_at) VALUES ({Name}, {session}, {now})
            ON CONFLICT (name) DO UPDATE SET data = excluded.data, updated_at = excluded.updated_at
            """, ct);
    }

    /// <summary>The first session only (the import of the old file): a session already in the database is never replaced by it.</summary>
    public async Task<bool> AddAsync(byte[] session, CancellationToken ct)
    {
        await using var ctx = await db.CreateDbContextAsync(ct);
        return await ctx.Database.ExecuteSqlAsync($"""
            INSERT INTO telegram_sessions (name, data, updated_at) VALUES ({Name}, {session}, {time.GetUtcNow()}) ON CONFLICT (name) DO NOTHING
            """, ct) == 1;
    }
}
