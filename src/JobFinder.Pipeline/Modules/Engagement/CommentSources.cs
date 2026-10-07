using System.Text.Json;
using System.Text.Json.Nodes;
using JobFinder.Core.Domain;
using JobFinder.Core.Filtering;
using JobFinder.Core.Policies;
using JobFinder.Core.Text;
using JobFinder.Core.Time;
using JobFinder.Data;
using JobFinder.Integrations.Threads;
using JobFinder.Llm.Client;
using JobFinder.Pipeline.Career;
using JobFinder.Pipeline.Jobs;
using JobFinder.Pipeline.Profile;
using JobFinder.Pipeline.Settings;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Wolverine.EntityFrameworkCore;

namespace JobFinder.Pipeline.Modules.Engagement;

public sealed class CommentSources(IDbContextFactory<JobFinderDbContext> db, IServiceScopeFactory scopes,
    CommentPreparationRequests preparations, ProfileProvider profiles, RuntimeSettings settings, LocalClock clock, TimeProvider time)
{
    public static Task LockAsync(JobFinderDbContext context, CancellationToken ct) =>
        context.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(hashtextextended('jobfinder.engagement.sources', 0))", ct);

    public static CommentSourceSnapshot Snapshot(CommentDraft draft)
    {
        var witness = JsonNode.Parse(JsonSerializer.Serialize(draft, CommentSourceJsonContext.Default.CommentDraft))!.AsObject();
        witness.Remove("deliveryRequestId");
        witness["createdAt"] = Microseconds(draft.CreatedAt);
        witness["postPublishedAt"] = Microseconds(draft.PostPublishedAt);
        witness["decidedAt"] = Microseconds(draft.DecidedAt);
        witness["publishedAt"] = Microseconds(draft.PublishedAt);
        return new() { Id = draft.Id, Revision = draft.Revision, Status = draft.Status, Hash = CanonicalJson.Hash(witness.ToJsonString()) };
    }

    private static long? Microseconds(DateTimeOffset? value) => value?.UtcDateTime.Ticks / TimeSpan.TicksPerMicrosecond;

    internal static bool Matches(CommentDraft draft, CommentSourceSnapshot snapshot) =>
        draft.Id == snapshot.Id && draft.Revision == snapshot.Revision && draft.Status == snapshot.Status && Snapshot(draft).Hash == snapshot.Hash;

    public async Task<bool> MayCollectAsync(CancellationToken ct) => !await settings.GetBoolAsync(RuntimeSettings.CommentsPaused, false, ct)
        && (await profiles.GetSnapshotAsync(ct))?.Value is { Reviewed: true };

    public async Task<Guid?> CollectAsync(ThreadsPost post, PolicySnapshot<EngagementPolicy> policy, int maxAgeHours, CancellationToken ct)
    {
        if (maxAgeHours <= 0) return null;
        policy = new PolicySnapshot<EngagementPolicy>(policy.Value with { }, policy.Version);
        var input = await CaptureAsync(post, policy, ct);
        if (input is null) return null;
        input.ExpiresAt = (post.PublishedAt ?? time.GetUtcNow()).AddHours(maxAgeHours);
        return await SaveAsync(input, ct);
    }

    public async Task<Guid?> RefreshAsync(CommentSourceSnapshot snapshot, CancellationToken ct)
    {
        snapshot = snapshot with { };
        await using var context = await db.CreateDbContextAsync(ct);
        var draft = await context.CommentDrafts.AsNoTracking().SingleOrDefaultAsync(row => row.Id == snapshot.Id, ct);
        if (draft is null || snapshot.Status != CommentStatus.Pending || !Matches(draft, snapshot)) return null;
        var policy = await settings.Automation.ReadAsync(ct);
        if (policy.Value.CommentsPaused) return null;
        var input = await CaptureAsync(new ThreadsPost(draft.PostId, draft.Author, draft.PostText, draft.PostPublishedAt, draft.PostUrl),
            new PolicySnapshot<EngagementPolicy>(new EngagementPolicy { AutoPublish = false }, 0), ct);
        if (input is null) return null;
        input.Replaces = snapshot with { };
        return await SaveAsync(input, ct);
    }

    private async Task<CommentSourceInput?> CaptureAsync(ThreadsPost post, PolicySnapshot<EngagementPolicy> policy, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(post.Id) || string.IsNullOrWhiteSpace(post.Username) || string.IsNullOrWhiteSpace(post.Url) || policy.Value.Validate() is not null
            || !Workers.CommentPlanner.IsCandidate(post.Text)) return null;
        if (await settings.GetBoolAsync(RuntimeSettings.CommentsPaused, false, ct)) return null;
        var profile = await profiles.GetSnapshotAsync(ct);
        if (profile?.Value is not { Reviewed: true }) return null;
        return new()
        {
            Preparation = await preparations.CaptureAsync(profile.Value, post.Username, post.Text, ct),
            ProfileVersion = profile.Version, ProfileHash = CanonicalJson.Hash(ProfileProvider.ToJson(profile.Value)),
            PostId = post.Id, PostUrl = post.Url, PublishedAt = post.PublishedAt, Policy = policy.Value with { }, PolicyVersion = policy.Version,
        };
    }

    private async Task<Guid?> SaveAsync(CommentSourceInput input, CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var outbox = scope.ServiceProvider.GetRequiredService<IDbContextOutbox>();
        await using var context = await db.CreateDbContextAsync(ct);
        await using var transaction = await context.Database.BeginTransactionAsync(ct);
        outbox.Enroll(context);
        await LockAsync(context, ct);
        var sourceKey = Fingerprint.Of(input.Replaces is { } old ? "refresh:" + old.Id : "collect:" + input.PostId);
        if (input.Replaces is { } replaces)
        {
            var draft = await context.CommentDrafts.SingleOrDefaultAsync(row => row.Id == replaces.Id, ct);
            if (draft is null || draft.Status != CommentStatus.Pending || !Matches(draft, replaces)) return null;
            var active = await context.CommentSourceRequests.SingleOrDefaultAsync(row => row.SourceKey == sourceKey
                && (row.Stage == CommentSourceStage.Queued || row.Stage == CommentSourceStage.Waiting), ct);
            if (active is not null)
            {
                active.Stage = CommentSourceStage.Cancelled;
                active.CompletedAt = time.GetUtcNow();
                active.ClaimEnvelopeId = null;
                active.Error = "explicit regeneration superseded this source request";
                await context.SaveChangesAsync(ct);
            }
        }
        else
        {
            if (input.ExpiresAt <= time.GetUtcNow() || await context.CommentDrafts.AnyAsync(row => row.PostId == input.PostId, ct)
                || await context.CommentSourceRequests.AnyAsync(row => row.SourceKey == sourceKey && row.Stage != CommentSourceStage.Cancelled && row.Stage != CommentSourceStage.Failed, ct)) return null;
            var since = clock.StartOfTodayUtc();
            var today = await context.CommentDrafts.CountAsync(CommentPublicationRules.CountsTowardDailyLimit(since, input.Policy.AutoPublish), ct);
            var reserved = await context.CommentSourceRequests.CountAsync(row => row.CreatedAt >= since
                && row.ReplacesDraftId == null && (row.Stage == CommentSourceStage.Queued || row.Stage == CommentSourceStage.Waiting), ct);
            if (today + reserved >= input.Policy.MaxDraftsPerDay) return null;
            var cooldown = time.GetUtcNow().AddDays(-input.Policy.AuthorCooldownDays);
            var author = input.Preparation.Author.ToLowerInvariant();
            if (await context.CommentDrafts.AnyAsync(row => row.Author.ToLower() == author && row.CreatedAt >= cooldown && row.Status != CommentStatus.Rejected, ct)
                || await context.CommentSourceRequests.AnyAsync(row => row.Author.ToLower() == author
                    && (row.Stage == CommentSourceStage.Queued || row.Stage == CommentSourceStage.Waiting), ct)) return null;
        }
        var json = JsonSerializer.Serialize(input, CommentSourceJsonContext.Default.CommentSourceInput);
        var request = new CommentSourceRequest
        {
            Id = Guid.NewGuid(), SourceKey = sourceKey, PostId = input.PostId, Author = input.Preparation.Author,
            InputJson = json, InputHash = CanonicalJson.Hash(json), Stage = CommentSourceStage.Queued,
            AutoPublish = input.Policy.AutoPublish, CreatedAt = time.GetUtcNow(),
            ReplacesDraftId = input.Replaces?.Id,
        };
        context.CommentSourceRequests.Add(request);
        await outbox.PublishAsync(new PrepareCommentSource { RequestId = request.Id });
        await outbox.SaveChangesAndFlushMessagesAsync(ct);
        return request.Id;
    }

    public async Task<CommentDraft?> WaitAsync(Guid id, CancellationToken ct)
    {
        while (true)
        {
            await using var context = await db.CreateDbContextAsync(ct);
            var request = await context.CommentSourceRequests.AsNoTracking().SingleAsync(row => row.Id == id, ct);
            if (request.Stage is CommentSourceStage.Cancelled or CommentSourceStage.Failed) throw new LlmUnavailableException(request.Error ?? "comment source request stopped");
            if (request.Stage == CommentSourceStage.Completed) return request.DraftId is { } draftId
                ? await context.CommentDrafts.AsNoTracking().SingleAsync(row => row.Id == draftId, ct) : null;
            if (request.RetryAt > time.GetUtcNow().AddSeconds(10)) throw new LlmUnavailableException("запрос комментария сохранён и будет продолжен после паузы");
            if (request.PreparationRequestId is { } preparationId
                && await context.CommentPreparationRequests.AnyAsync(row => row.Id == preparationId && row.RetryAt > time.GetUtcNow(), ct))
                throw new LlmUnavailableException("подготовка комментария сохранена и будет продолжена после восстановления модели");
            await Task.Delay(TimeSpan.FromMilliseconds(250), time, ct);
        }
    }
}
