using JobFinder.Core.Domain;
using JobFinder.Core.Policies;
using JobFinder.Core.Time;
using JobFinder.Data;
using JobFinder.Integrations.Threads;
using JobFinder.Llm.Client;
using JobFinder.Llm.Tasks;
using JobFinder.Pipeline.Notifications;
using JobFinder.Pipeline.Modules.Discovery;
using JobFinder.Pipeline.Modules.Engagement;
using JobFinder.Pipeline.Profile;
using JobFinder.Pipeline.Settings;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace JobFinder.Pipeline.Jobs;

/// <summary>
/// Threads, two passes: vacancy searches feed <c>raw_posts</c> (the same extraction as Telegram), audience searches feed comments —
/// published by themselves with <see cref="EngagementPolicy.AutoPublish"/>, approved in the bot otherwise. Comments are rate-limited per
/// day and per author, and old posts are ignored.
/// </summary>
public sealed class ThreadsJob(SearchPolicyStore policies, EngagementPolicyStore engagement, ThreadsGateway threads, CommentSources sources, PostProcessingRequests posts,
    JobTriggers triggers, IDbContextFactory<JobFinderDbContext> db, Notifier notifier, TimeProvider time, ILogger<ThreadsJob> log)
    : SearchScheduledJob(policies, db, triggers, notifier, time, log)
{
    private readonly IDbContextFactory<JobFinderDbContext> _db = db;
    private readonly JobTriggers _triggers = triggers;

    public override string Name => JobNames.Threads;

    protected override bool Enabled => Snapshot.Value.Threads.Enabled;

    protected override DateTimeOffset NextRunAfter(DateTimeOffset finishedAt) => finishedAt + Jittered(Snapshot.Value.Threads.IntervalMinutes, Snapshot.Value.Threads.JitterMinutes);

    protected override async Task<JobOutcome> RunOnceAsync(CancellationToken ct)
    {
        if (!await threads.IsConfiguredAsync(ct)) return new JobOutcome(0, "Threads не настроен: нужен Threads:AccessToken или --login threads");

        var search = Snapshot.Value.Threads;
        var publication = await engagement.ReadAsync(ct);
        var minDate = Time.GetUtcNow().AddHours(-search.MaxPostAgeHours);
        var posts = 0;
        foreach (var query in search.VacancyQueries)
        {
            foreach (var post in await threads.SearchRecentAsync(query, ct, search.ResultsPerQuery))
            {
                if (post.PublishedAt is { } at && at < minDate) continue;
                if (await StorePostAsync(post, ct)) posts++;
            }
        }

        if (posts > 0) _triggers.Trigger(JobNames.Posts);

        var drafts = await CollectDraftsAsync(minDate, search, publication, ct);
        return new JobOutcome(posts + drafts, $"{posts} vacancy posts, {drafts} comment requests");
    }

    private async Task<int> CollectDraftsAsync(DateTimeOffset minDate, ThreadsSearchPolicy search, PolicySnapshot<EngagementPolicy> publication, CancellationToken ct)
    {
        if (search.AudienceQueries.Count == 0 || !await sources.MayCollectAsync(ct)) return 0;
        var created = 0;
        foreach (var query in search.AudienceQueries.OrderBy(_ => Random.Shared.Next()))
        {
            if (created >= publication.Value.MaxDraftsPerDay) break;
            foreach (var post in await threads.SearchRecentAsync(query, ct, search.ResultsPerQuery))
            {
                if (created >= publication.Value.MaxDraftsPerDay) break;
                if (post.PublishedAt is { } at && at < minDate) continue;
                if (!Workers.CommentPlanner.IsCandidate(post.Text)) continue;
                try
                {
                    if (await sources.CollectAsync(post, publication, search.MaxPostAgeHours, ct) is not null) created++;
                }
                catch (LlmUnavailableException e)
                {
                    log.Warn($"[{Name}] comment plan skipped: {e.Message}");
                    return created;
                }

            }
        }

        return created;
    }

    /// <summary>
    /// What uses up the day's comments. Auto-publish: only what is published or on its way — a draft left pending from an approval-mode
    /// day must not stop the day (seen 2026-09-28: 8 unanswered cards from 00:48 held the limit, no comment all day). Approval mode:
    /// every card offered today, so the chat is not flooded.
    /// </summary>
    public static System.Linq.Expressions.Expression<Func<CommentDraft, bool>> CountsTowardDailyLimit(DateTimeOffset since, bool autoPublish) =>
        JobFinder.Core.Filtering.CommentPublicationRules.CountsTowardDailyLimit(since, autoPublish);

    /// <summary>
    /// Worth commenting and a clean variant within Threads' 500 characters: approved at once with auto-publish (the first variant — the
    /// planner keeps only those that passed the style checks), a card for the user otherwise. Anything else is rejected.
    /// </summary>
    public static void Decide(CommentDraft draft, bool worthCommenting, bool autoPublish, DateTimeOffset now) =>
        JobFinder.Core.Filtering.CommentPublicationRules.Decide(draft, worthCommenting, autoPublish, now);

    private async Task<bool> StorePostAsync(ThreadsPost post, CancellationToken ct)
    {
        await using var ctx = await _db.CreateDbContextAsync(ct);
        if (await ctx.RawPosts.AnyAsync(p => p.Source == SourceKind.Threads && p.ChatRef == post.Username && p.ExternalId == post.Id, ct))
            return false;
        var raw = new RawPost
        {
            Source = SourceKind.Threads,
            ChatRef = post.Username,
            ChatTitle = "Threads @" + post.Username,
            ExternalId = post.Id,
            Url = post.Url,
            Author = "@" + post.Username,
            Text = post.Text,
            PublishedAt = post.PublishedAt,
            FetchedAt = Time.GetUtcNow(),
            Status = RawPostStatus.New,
        };
        try
        {
            await posts.StoreAsync(ctx, raw, ct);
            return true;
        }
        catch (DbUpdateException e) when (e.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            return false;
        }
    }

    public static AudienceKind Audience(string raw) => JobFinder.Core.Filtering.CommentPublicationRules.Audience(raw);
}
