using JobFinder.Core.Domain;
using JobFinder.Core.Filtering;
using JobFinder.Core.Text;
using JobFinder.Data;
using JobFinder.Integrations.Threads;
using JobFinder.Pipeline.Notifications;
using JobFinder.Pipeline.Profile;
using JobFinder.Pipeline.Modules.Discovery;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Wolverine.EntityFrameworkCore;

namespace JobFinder.Pipeline.Modules.Engagement;

public sealed class CommentDeliveryRequests(IDbContextFactory<JobFinderDbContext> db, IServiceScopeFactory scopes,
    EngagementPolicyStore policies, SearchPolicyStore searches, ICommentDeliveryTransport transport, Notifier notifier, TimeProvider time)
{
    public async Task<Guid?> RequestAsync(long draftId, CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var outbox = scope.ServiceProvider.GetRequiredService<IDbContextOutbox>();
        await using var context = await db.CreateDbContextAsync(ct);
        await using var transaction = await context.Database.BeginTransactionAsync(ct);
        outbox.Enroll(context);
        await CommentSources.LockAsync(context, ct);
        var draft = await context.CommentDrafts.SingleOrDefaultAsync(row => row.Id == draftId, ct);
        if (draft is not { Status: CommentStatus.Approved }) return null;
        var id = await QueueAsync(context, outbox, draft, ct, automatic: draft.Revision == 0);
        await notifier.SaveAndFlushAsync(context, outbox, ct);
        return id;
    }

    public async Task<Guid?> QueueAsync(JobFinderDbContext context, IDbContextOutbox outbox, CommentDraft draft, CancellationToken ct, bool automatic = false, DateTimeOffset? expiresAt = null)
    {
        if (context.Database.CurrentTransaction is null || context.Entry(draft).State == EntityState.Detached || draft.Status != CommentStatus.Approved)
            throw new InvalidOperationException("comment delivery handoff requires the tracked approval and its transaction");
        await notifier.SaveJoinedAsync(context, outbox, ct);
        if (draft.DeliveryRequestId is { } existing) return existing;
        await context.Database.ExecuteSqlRawAsync("SELECT 1 FROM app_documents WHERE key IN ('candidate.profile', 'policy.engagement', 'policy.search') FOR SHARE", ct);
        var profile = await context.Documents.SingleOrDefaultAsync(row => row.Key == ProfileProvider.Key, ct);
        var source = CommentDeliverySource.Capture(draft);
        if (draft.Attempts == 0 && (source.Text.Length is 0 or > 500 || string.IsNullOrWhiteSpace(source.PostId) || string.IsNullOrWhiteSpace(source.PostUrl)
            || string.IsNullOrWhiteSpace(source.Author) || CommentStyle.Problems(source.Text, source.Audience == AudienceKind.Tech).Count > 0))
        {
            draft.Status = CommentStatus.Failed;
            draft.Revision++;
            draft.Error = "legacy comment has invalid text or target; delivery refused";
            notifier.Add(context, NotificationKind.Alert, "⚠️ Комментарий #c" + draft.Id + " требует ручной проверки: " + TextUtil.EscapeHtml(draft.Error));
            return null;
        }
        if (draft.Attempts == 0 && (profile is null || ProfileProvider.FromJson(profile.Content) is not { Reviewed: true })) return null;
        var policy = await policies.ReadAsync(ct);
        var search = await searches.ReadAsync(ct);
        var input = new CommentDeliveryInput
        {
            Source = source, Automatic = automatic, LegacyUncertain = draft.Attempts > 0, ProfileVersion = profile?.Version ?? 0,
            ProfileHash = profile is null ? "" : CanonicalJson.Hash(ProfileProvider.ToJson(ProfileProvider.FromJson(profile.Content))),
            Policy = policy.Value, PolicyVersion = policy.Version, Route = transport.CaptureRoute(source.Post()), SpacingSeconds = Random.Shared.Next(60, 181),
            MaxPostAgeHours = search.Value.Threads.MaxPostAgeHours, SearchPolicyVersion = search.Version, ExpiresAt = expiresAt,
        };
        var request = new CommentDeliveryRequest
        {
            Id = Guid.NewGuid(), DraftId = draft.Id, Revision = draft.Revision, InputJson = CommentDeliveryInputs.Serialize(input),
            Stage = draft.Attempts > 0 ? CommentDeliveryStage.OutcomeUnknown : CommentDeliveryStage.Preparing, CreatedAt = time.GetUtcNow(),
        };
        request.InputHash = CanonicalJson.Hash(request.InputJson);
        if (!input.LegacyUncertain) CommentDeliveryInputs.Read(request);
        context.CommentDeliveryRequests.Add(request);
        draft.DeliveryRequestId = request.Id;
        if (draft.Attempts > 0)
        {
            draft.Status = CommentStatus.OutcomeUnknown;
            request.Error = draft.Error = "legacy attempts have no reliable receipt; reconciliation required";
            var attempt = new CommentDeliveryAttempt
            {
                Id = Guid.NewGuid(), RequestId = request.Id, DraftId = draft.Id, Revision = draft.Revision,
                Target = source.PostUrl, Text = source.Text, TextHash = CanonicalJson.Hash(System.Text.Json.JsonSerializer.Serialize(source.Text)),
                InputHash = request.InputHash, Status = DeliveryAttemptStatus.OutcomeUnknown, CreatedAt = time.GetUtcNow(),
                Error = request.Error,
            };
            request.AttemptId = attempt.Id;
            request.CompletedAt = time.GetUtcNow();
            context.CommentDeliveryAttempts.Add(attempt);
            notifier.Add(context, NotificationKind.Alert, "⚠️ Неизвестный исход комментария #c" + draft.Id + " · запрос " + request.Id);
        }
        else
        {
            await outbox.PublishAsync(new PrepareCommentDelivery { RequestId = request.Id });
            notifier.Add(context, NotificationKind.Info, "💬 Комментарий #c" + draft.Id + " в очереди на публикацию · "
                + "<a href=\"" + TextUtil.EscapeHtml(draft.PostUrl) + "\">@" + TextUtil.EscapeHtml(draft.Author) + "</a>",
                [["⛔ Отменить|c:cancel:" + draft.Id + ":0:" + draft.Revision]], draft.Id);
        }
        return request.Id;
    }
}
