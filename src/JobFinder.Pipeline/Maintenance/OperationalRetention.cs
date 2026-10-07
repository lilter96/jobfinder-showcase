using JobFinder.Core.Telemetry;
using JobFinder.Data;
using JobFinder.Pipeline.Jobs;
using Microsoft.EntityFrameworkCore;

namespace JobFinder.Pipeline.Maintenance;

public sealed record RetentionSweep
{
    public int Posts { get; set; }
    public int Runs { get; set; }
    public int Total => Posts + Runs;
}

public sealed class OperationalRetention(IDbContextFactory<JobFinderDbContext> db, ServerOwnership ownership, TimeProvider time)
{
    public async Task<RetentionSweep> SweepAsync(int limit, CancellationToken ct)
    {
        if (limit is < 1 or > 1000) throw new ArgumentOutOfRangeException(nameof(limit));
        ownership.EnsureOwned();
        var now = time.GetUtcNow();
        var noise = now.AddDays(-30);
        await using var context = await db.CreateDbContextAsync(ct);
        await using var transaction = await context.Database.BeginTransactionAsync(ct);
        await context.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(hashtextextended('maintenance.retention',0))", ct);
        using var span = Telemetry.StartStage("sweep", "retention").Tag("batch.limit", limit);
        var posts = await context.Database.SqlQuery<long>($"""
            SELECT post.id AS "Value" FROM raw_posts post
            WHERE post.status IN ('Skipped','NotVacancy') AND post.fetched_at < {noise}
              AND post.error IS NULL AND post.processing_request_id IS NULL AND post.processing_revision = 0 AND post.vacancy_id IS NULL
              AND NOT EXISTS (SELECT 1 FROM post_processing_requests root WHERE root.post_id = post.id)
            ORDER BY post.fetched_at, post.id LIMIT {limit} FOR UPDATE OF post SKIP LOCKED
            """).ToListAsync(ct);
        var runs = await context.Database.SqlQuery<long>($"""
            SELECT run.id AS "Value" FROM job_runs run
            WHERE run.status IN ('Ok','Failed') AND run.started_at < {noise} AND run.job <> {JobNames.Digest}
              AND run.weekly_report_request_id IS NULL
              AND NOT EXISTS (SELECT 1 FROM weekly_report_requests root WHERE root.job_run_id = run.id)
              AND NOT EXISTS (SELECT 1 FROM notifications notice WHERE notice.entity_id = run.id)
            ORDER BY run.started_at, run.id LIMIT {limit} FOR UPDATE OF run SKIP LOCKED
            """).ToListAsync(ct);
        var result = new RetentionSweep
        {
            Posts = await context.RawPosts.Where(post => posts.Contains(post.Id)).ExecuteDeleteAsync(ct),
            Runs = await context.JobRuns.Where(run => runs.Contains(run.Id)).ExecuteDeleteAsync(ct),
        };
        ownership.EnsureOwned();
        await transaction.CommitAsync(ct);
        span.Tag("posts.deleted", result.Posts).Tag("runs.deleted", result.Runs);
        return result;
    }
}
