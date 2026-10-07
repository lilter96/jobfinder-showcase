using System.Globalization;
using JobFinder.Core.Domain;
using JobFinder.Data;
using JobFinder.Pipeline.Ingest;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Wolverine.EntityFrameworkCore;

namespace JobFinder.Pipeline.Modules.Discovery;

public sealed class HhLinkRequests(IDbContextFactory<JobFinderDbContext> db, IServiceScopeFactory scopes, TimeProvider time)
{
    internal static Task LockAsync(JobFinderDbContext context, CancellationToken ct) =>
        context.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(hashtextextended('jobfinder.discovery.hh-links', 0))", ct);

    public async Task EnqueueAsync(IReadOnlyList<PendingLink> links, CancellationToken ct, bool resumePaused = false)
    {
        if (links.Any(link => link.Id <= 0)) throw new ArgumentException("vacancy IDs must be positive", nameof(links));
        await using var scope = scopes.CreateAsyncScope();
        var outbox = scope.ServiceProvider.GetRequiredService<IDbContextOutbox>();
        await using var context = await db.CreateDbContextAsync(ct);
        await using var transaction = await context.Database.BeginTransactionAsync(ct);
        outbox.Enroll(context);
        await LockAsync(context, ct);
        await AddAsync(context, outbox, links, ct, resumePaused);
        await outbox.SaveChangesAndFlushMessagesAsync(ct);
    }

    public async Task<int> ImportLegacyAsync(CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var outbox = scope.ServiceProvider.GetRequiredService<IDbContextOutbox>();
        await using var context = await db.CreateDbContextAsync(ct);
        await using var transaction = await context.Database.BeginTransactionAsync(ct);
        outbox.Enroll(context);
        await LockAsync(context, ct);
        var legacy = await context.Settings.SingleOrDefaultAsync(setting => setting.Key == HhLinkQueue.Key, ct);
        if (legacy is null) return 0;
        var links = ParseLegacy(legacy.Value);
        await AddAsync(context, outbox, links, ct);
        context.Settings.Remove(legacy);
        await outbox.SaveChangesAndFlushMessagesAsync(ct);
        return links.Count;
    }

    internal static List<PendingLink> ParseLegacy(string value) => value.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Select(line => line.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        .Where(parts => long.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var id) && id > 0)
        .GroupBy(parts => long.Parse(parts[0], CultureInfo.InvariantCulture))
        .Select(group => new PendingLink(group.Key, group.SelectMany(parts => parts.Skip(1)).Where(part => part.StartsWith('@'))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList())).ToList();

    private async Task AddAsync(JobFinderDbContext context, IDbContextOutbox outbox, IReadOnlyList<PendingLink> links, CancellationToken ct,
        bool resumePaused = false)
    {
        foreach (var link in links)
        {
            var request = context.HhLinkRequests.Local.FirstOrDefault(row => row.HhId == link.Id && row.Stage is HhLinkStage.Reading or HhLinkStage.Linking)
                ?? await context.HhLinkRequests.SingleOrDefaultAsync(row => row.HhId == link.Id &&
                    (row.Stage == HhLinkStage.Reading || row.Stage == HhLinkStage.Linking), ct);
            if (request is null)
            {
                request = new HhLinkRequest { Id = Guid.NewGuid(), HhId = link.Id, CreatedAt = time.GetUtcNow(), Stage = HhLinkStage.Reading };
                context.HhLinkRequests.Add(request);
                await outbox.PublishAsync(new ReadHhLink { RequestId = request.Id });
            }
            else if (resumePaused && request.Stage == HhLinkStage.Reading && request.RetryAt > time.GetUtcNow())
            {
                request.RetryAt = null;
                request.LastError = null;
                await outbox.PublishAsync(new ReadHhLink { RequestId = request.Id });
            }
            request.Contacts = request.Contacts.Concat(link.Contacts).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }
    }

    public async Task<List<PendingLink>> PendingAsync(CancellationToken ct)
    {
        await using var context = await db.CreateDbContextAsync(ct);
        return await context.HhLinkRequests.AsNoTracking().Where(row => row.Stage == HhLinkStage.Reading || row.Stage == HhLinkStage.Linking)
            .OrderBy(row => row.CreatedAt).Select(row => new PendingLink(row.HhId, row.Contacts)).ToListAsync(ct);
    }

    public async Task<bool> CancelAsync(Guid requestId, CancellationToken ct)
    {
        await using var context = await db.CreateDbContextAsync(ct);
        await using var transaction = await context.Database.BeginTransactionAsync(ct);
        await LockAsync(context, ct);
        var request = await context.HhLinkRequests.SingleOrDefaultAsync(row => row.Id == requestId, ct);
        if (request is null || request.Stage is not (HhLinkStage.Reading or HhLinkStage.Linking)) return false;
        request.Stage = HhLinkStage.Cancelled;
        request.CompletedAt = time.GetUtcNow();
        await context.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return true;
    }
}
