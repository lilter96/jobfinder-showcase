using JobFinder.Core.Domain;
using JobFinder.Data;
using JobFinder.Pipeline.Jobs;
using JobFinder.Pipeline.Modules.Engagement;
using Microsoft.EntityFrameworkCore;

namespace JobFinder.Pipeline.Workers;

public sealed class CommentPublishWorker(IDbContextFactory<JobFinderDbContext> db, CommentDeliveryRequests requests, JobTriggers triggers,
    TimeProvider time, ILogger<CommentPublishWorker> log) : QueueWorker(triggers, time, log)
{
    public override string Name => JobNames.Comments;

    protected override TimeSpan IdleDelay => TimeSpan.FromMinutes(1);

    protected override async Task<int> ProcessBatchAsync(CancellationToken ct)
    {
        await using var ctx = await db.CreateDbContextAsync(ct);
        var ids = await ctx.CommentDrafts.Where(row => row.Status == CommentStatus.Approved && row.DeliveryRequestId == null)
            .OrderBy(row => row.DecidedAt).Select(row => row.Id).Take(20).ToListAsync(ct);
        var processed = 0;
        foreach (var id in ids)
        {
            if (await requests.RequestAsync(id, ct) is not null
                || !await ctx.CommentDrafts.AnyAsync(row => row.Id == id && row.Status == CommentStatus.Approved, ct)) processed++;
        }
        return processed;
    }
}
