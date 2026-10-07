using JobFinder.Core.Domain;
using JobFinder.Core.Text;
using JobFinder.Data;
using JobFinder.Pipeline.Actions;
using JobFinder.Pipeline.Notifications;
using Microsoft.EntityFrameworkCore;

namespace JobFinder.Pipeline.Modules.Engagement;

public sealed class CommentDeliveryLedger(IDbContextFactory<JobFinderDbContext> db, Notifier notifier, TimeProvider time)
{
    public async Task<ActionResult> ReconcileAsync(Guid id, bool sent, string evidence, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(evidence) || evidence.Length > 2000) return ActionResult.Fail("нужна проверенная причина сверки, до 2000 символов");
        await using var context = await db.CreateDbContextAsync(ct);
        await using var transaction = await context.Database.BeginTransactionAsync(ct);
        await CommentSources.LockAsync(context, ct);
        var request = await context.CommentDeliveryRequests.SingleOrDefaultAsync(row => row.Id == id, ct);
        if (request is not { Stage: CommentDeliveryStage.OutcomeUnknown }) return ActionResult.Fail("нет запроса с неизвестным исходом");
        CommentDeliveryInput input;
        try { input = CommentDeliveryInputs.Witness(request); }
        catch (Exception error) when (error is UnsupportedCommentDeliveryException or System.Text.Json.JsonException)
        {
            return ActionResult.Fail("сохранённый снимок повреждён: сверка не может подменить исходные данные");
        }
        var draft = await context.CommentDrafts.SingleOrDefaultAsync(row => row.Id == request.DraftId, ct);
        if (draft is null || !CommentDeliveryInputs.Matches(draft, request, input, CommentStatus.OutcomeUnknown))
            return ActionResult.Fail("исходный комментарий или его владелец изменился");
        if (request.AttemptId is not { } attemptId) return ActionResult.Fail("нет сохранённой попытки публикации");
        var attempt = await context.CommentDeliveryAttempts.SingleOrDefaultAsync(row => row.Id == attemptId, ct);
        if (attempt is not { Status: DeliveryAttemptStatus.OutcomeUnknown } || !CommentDeliveryWorkflow.AttemptMatches(attempt, request, input))
            return ActionResult.Fail("попытка не совпадает с исходным запросом");
        attempt.Status = DeliveryAttemptStatus.Reconciled;
        attempt.ReconciledSent = sent;
        attempt.CompletedAt = time.GetUtcNow();
        attempt.Error = "manual reconciliation: " + evidence.Trim();
        request.Stage = sent ? CommentDeliveryStage.Completed : CommentDeliveryStage.Cancelled;
        request.CompletedAt = time.GetUtcNow();
        request.Error = attempt.Error;
        draft.Status = sent ? CommentStatus.Posted : CommentStatus.Rejected;
        draft.Error = attempt.Error;
        draft.Revision++;
        notifier.Add(context, NotificationKind.Info, "Сверка комментария #c" + draft.Id + ": "
            + (sent ? "пользователь подтвердил публикацию" : "пользователь подтвердил, что публикации нет")
            + ". Это ручная сверка, не подтверждение транспорта. Повтор не отправляется. " + TextUtil.EscapeHtml(evidence.Trim()), entityId: draft.Id);
        await notifier.SaveAndFlushAsync(context, ct);
        return ActionResult.Done("сверка сохранена; публикация не выполнялась");
    }

    public static async Task<int> RecoverAsync(JobFinderDbContext context, Notifier notifier, DateTimeOffset now, CancellationToken ct)
    {
        await using var transaction = await context.Database.BeginTransactionAsync(ct);
        await CommentSources.LockAsync(context, ct);
        var requests = await context.CommentDeliveryRequests.Where(row => row.Stage == CommentDeliveryStage.Sending).ToListAsync(ct);
        foreach (var request in requests)
        {
            request.Stage = CommentDeliveryStage.OutcomeUnknown;
            request.ClaimEnvelopeId = null;
            request.RetryAt = null;
            request.CompletedAt = now;
            request.Error = "process stopped before a reliable publication receipt; reconciliation required";
            var attempt = await context.CommentDeliveryAttempts.SingleOrDefaultAsync(row => row.Id == request.AttemptId, ct);
            if (attempt is { Status: DeliveryAttemptStatus.Submitting })
            {
                attempt.Status = DeliveryAttemptStatus.OutcomeUnknown;
                attempt.CompletedAt = now;
                attempt.Error = request.Error;
            }
            var draft = await context.CommentDrafts.SingleOrDefaultAsync(row => row.Id == request.DraftId, ct);
            if (draft is not null && draft.DeliveryRequestId == request.Id && draft.Revision == request.Revision)
            {
                draft.Status = CommentStatus.OutcomeUnknown;
                draft.Error = request.Error;
            }
            context.Notifications.Add(new()
            {
                Kind = NotificationKind.Alert, Status = NotificationStatus.Pending, CreatedAt = now, EntityId = request.DraftId,
                Text = "⚠️ Неизвестный исход комментария #c" + request.DraftId + ". Повтор остановлен; проверьте Threads и выполните --comment-reconcile " + request.Id,
            });
        }
        if (requests.Count > 0)
            await context.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO settings (key, value, updated_at) VALUES ({CommentDeliveryWorkflow.PacingKey}, {now.AddSeconds(180).ToString("O")}, {now})
                ON CONFLICT (key) DO UPDATE SET value = EXCLUDED.value, updated_at = EXCLUDED.updated_at
                """, ct);
        await notifier.SaveAndFlushAsync(context, ct);
        return requests.Count;
    }
}
