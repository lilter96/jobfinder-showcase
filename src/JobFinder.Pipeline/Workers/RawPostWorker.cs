using JobFinder.Core.Domain;
using JobFinder.Data;
using JobFinder.Pipeline.Jobs;
using JobFinder.Pipeline.Modules.Discovery;
using Microsoft.EntityFrameworkCore;

namespace JobFinder.Pipeline.Workers;

public sealed class RawPostWorker(IDbContextFactory<JobFinderDbContext> db, PostProcessingRequests requests,
    JobTriggers triggers, TimeProvider time, ILogger<RawPostWorker> log) : QueueWorker(triggers, time, log)
{
    public override string Name => JobNames.Posts;

    protected override async Task<int> ProcessBatchAsync(CancellationToken ct)
    {
        await using var context = await db.CreateDbContextAsync(ct);
        var ids = await context.RawPosts.Where(post => post.Status == RawPostStatus.New && (post.ProcessingRequestId == null
                || !context.PostProcessingRequests.Any(root => root.Id == post.ProcessingRequestId && root.PostId == post.Id && root.Revision == post.ProcessingRevision)))
            .OrderBy(post => post.Id).Select(post => post.Id).Take(20).ToListAsync(ct);
        var admitted = 0;
        foreach (var id in ids)
        {
            ct.ThrowIfCancellationRequested();
            if (await requests.RequestAsync(id, ct) is not null
                || !await context.RawPosts.AnyAsync(post => post.Id == id && post.Status == RawPostStatus.New && post.ProcessingRequestId == null, ct)) admitted++;
        }
        return admitted;
    }
}
