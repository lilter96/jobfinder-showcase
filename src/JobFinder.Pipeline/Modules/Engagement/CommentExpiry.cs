using System.Globalization;
using System.Text.Json;
using JobFinder.Core.Domain;
using JobFinder.Core.Telemetry;
using JobFinder.Data;
using JobFinder.Pipeline.Messaging;
using JobFinder.Pipeline.Modules.Discovery;
using JobFinder.Pipeline.Notifications;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Wolverine.EntityFrameworkCore;

namespace JobFinder.Pipeline.Modules.Engagement;

public sealed record CommentExpirySnapshot
{
    public CommentSourceSnapshot Source { get; set; } = new();
    public Guid? SourceRequestId { get; set; }
    public Guid? DeliveryRequestId { get; set; }

    public static CommentExpirySnapshot Capture(CommentDraft draft) => new()
    {
        Source = CommentSources.Snapshot(draft), SourceRequestId = draft.SourceRequestId, DeliveryRequestId = draft.DeliveryRequestId,
    };

    public bool Matches(CommentDraft draft) => SourceRequestId == draft.SourceRequestId && DeliveryRequestId == draft.DeliveryRequestId
        && CommentSources.Matches(draft, Source);
}

public sealed class CommentExpiry(IDbContextFactory<JobFinderDbContext> db, NativePublicationScopes scopes,
    SearchPolicyStore policies, Notifier notifier, TimeProvider time)
{
    internal const string CursorKey = "engagement.expiry.scan";

    public async Task<bool> ExpireAsync(CommentExpirySnapshot snapshot, CancellationToken ct)
    {
        snapshot = snapshot with { Source = snapshot.Source with { } };
        await using var context = await db.CreateDbContextAsync(ct);
        await using var transaction = await context.Database.BeginTransactionAsync(ct);
        await CommentSources.LockAsync(context, ct);
        var draft = await context.CommentDrafts.SingleOrDefaultAsync(row => row.Id == snapshot.Source.Id, ct);
        if (draft is null || !snapshot.Matches(draft)) return false;
        await using var scope = scopes.CreateAsyncScope();
        var outbox = scope.ServiceProvider.GetRequiredService<IDbContextOutbox>();
        outbox.Enroll(context);
        var age = (await policies.ReadAsync(ct)).Value.Threads.MaxPostAgeHours;
        if (!await ExpireTrackedAsync(context, draft, age, ct)) return false;
        await notifier.SaveAndFlushAsync(context, outbox, ct);
        return true;
    }

    public async Task<int> ExpireBatchAsync(int limit, CancellationToken ct)
    {
        if (limit is < 1 or > 100) throw new ArgumentOutOfRangeException(nameof(limit));
        using var stage = Telemetry.StartStage("expire-pending", "comments").Tag("batch.limit", limit);
        await using var context = await db.CreateDbContextAsync(ct);
        await using var transaction = await context.Database.BeginTransactionAsync(ct);
        await CommentSources.LockAsync(context, ct);
        await using var scope = scopes.CreateAsyncScope();
        var outbox = scope.ServiceProvider.GetRequiredService<IDbContextOutbox>();
        outbox.Enroll(context);
        var setting = await context.Settings.SingleOrDefaultAsync(row => row.Key == CursorKey, ct);
        var after = 0L;
        if (setting is not null && (!long.TryParse(setting.Value, NumberStyles.None, CultureInfo.InvariantCulture, out after) || after < 0))
            throw new InvalidOperationException("unsupported comment expiry scan cursor");
        var pending = context.CommentDrafts.Where(draft => draft.Status == CommentStatus.Pending).OrderBy(draft => draft.Id);
        var batch = await pending.Where(draft => draft.Id > after).Take(limit).ToListAsync(ct);
        if (batch.Count == 0) batch = await pending.Take(limit).ToListAsync(ct);
        var age = (await policies.ReadAsync(ct)).Value.Threads.MaxPostAgeHours;
        var expired = 0;
        foreach (var draft in batch) if (await ExpireTrackedAsync(context, draft, age, ct)) expired++;
        if (setting is null)
        {
            setting = new AppSetting { Key = CursorKey, Value = "0" };
            context.Settings.Add(setting);
        }
        setting.Value = (batch.LastOrDefault()?.Id ?? 0).ToString(CultureInfo.InvariantCulture);
        setting.UpdatedAt = time.GetUtcNow();
        stage.Tag("drafts.scanned", batch.Count).Tag("drafts.expired", expired);
        await notifier.SaveAndFlushAsync(context, outbox, ct);
        return expired;
    }

    private async Task<bool> ExpireTrackedAsync(JobFinderDbContext context, CommentDraft draft, int maxAgeHours, CancellationToken ct)
    {
        if (draft.Status != CommentStatus.Pending || draft.DeliveryRequestId is not null
            || await context.CommentDeliveryRequests.AnyAsync(root => root.DraftId == draft.Id
                && (root.Stage == CommentDeliveryStage.Preparing || root.Stage == CommentDeliveryStage.Ready
                    || root.Stage == CommentDeliveryStage.Sending || root.Stage == CommentDeliveryStage.OutcomeUnknown), ct)) return false;
        var deadline = (draft.PostPublishedAt ?? draft.CreatedAt).AddHours(maxAgeHours);
        if (draft.SourceRequestId is { } sourceId)
        {
            var root = await context.CommentSourceRequests.SingleOrDefaultAsync(row => row.Id == sourceId, ct);
            if (root is null || root.Stage != CommentSourceStage.Completed || root.DraftId != draft.Id) return false;
            CommentSourceInput input;
            try { input = CommentSourceInputs.Read(root); }
            catch (Exception error) when (error is JsonException or UnsupportedCommentSourceException) { return false; }
            if (input.PostId != draft.PostId || input.ExpiresAt is not { } captured) return false;
            if (captured < deadline) deadline = captured;
        }
        if (deadline > time.GetUtcNow()) return false;
        using var stage = Telemetry.StartStage("expire", "comment").Tag("comment.id", draft.Id).Tag("comment.revision", draft.Revision);
        draft.Status = CommentStatus.Expired;
        draft.Revision++;
        draft.DecidedAt = time.GetUtcNow();
        draft.Error = "пост устарел до одобрения";
        var cards = await context.Notifications.Where(notice => notice.Kind == NotificationKind.CommentDraft && notice.EntityId == draft.Id).ToListAsync(ct);
        foreach (var card in cards)
        {
            if (card.TelegramMessageId is not { } messageId) continue;
            if (await context.Notifications.AnyAsync(notice => notice.EditParentNotificationId == card.Id && notice.EditKeyboardMessageId == messageId
                && notice.Status != NotificationStatus.Failed, ct)) continue;
            context.Notifications.Add(new Notification
            {
                Kind = NotificationKind.Info, Text = "Устаревшая карточка комментария", EntityId = draft.Id, Status = NotificationStatus.Pending,
                CreatedAt = time.GetUtcNow(), EditKeyboardMessageId = messageId, EditParentNotificationId = card.Id,
                Buttons = [["♻️ Пост устарел|noop:0:0"]],
            });
        }
        notifier.Add(context, NotificationKind.Info, "Комментарий #c" + draft.Id + " пропущен: пост устарел до одобрения.", entityId: draft.Id);
        return true;
    }
}
