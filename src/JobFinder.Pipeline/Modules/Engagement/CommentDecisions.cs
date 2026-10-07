using JobFinder.Core.Domain;
using JobFinder.Core.Filtering;
using JobFinder.Data;
using JobFinder.Pipeline.Actions;
using JobFinder.Pipeline.Notifications;
using Microsoft.Extensions.DependencyInjection;
using Wolverine.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace JobFinder.Pipeline.Modules.Engagement;

public sealed class CommentDecisions(IDbContextFactory<JobFinderDbContext> db, IServiceScopeFactory scopes, CommentDeliveryRequests deliveries, Notifier notifier, TimeProvider time)
{
    public static CommentDecisionSnapshot Snapshot(CommentDraft draft) => new() { Id = draft.Id, Revision = draft.Revision, Status = draft.Status };

    public Task<ActionResult> PickAsync(CommentDecisionSnapshot snapshot, int variant, CancellationToken ct) =>
        ChangeAsync(snapshot, draft => variant >= 0 && variant < draft.Variants.Count ? draft.Variants[variant] : null, false, ct);

    public Task<ActionResult> SetAsync(CommentDecisionSnapshot snapshot, string text, CancellationToken ct) =>
        ChangeAsync(snapshot, _ => text.Trim(), false, ct);

    public Task<ActionResult> RejectAsync(CommentDecisionSnapshot snapshot, CancellationToken ct) =>
        ChangeAsync(snapshot, _ => null, true, ct);

    public Task<ActionResult> CancelAsync(CommentDecisionSnapshot snapshot, CancellationToken ct) =>
        ChangeAsync(snapshot, _ => null, true, ct, cancel: true);

    private async Task<ActionResult> ChangeAsync(CommentDecisionSnapshot snapshot, Func<CommentDraft, string?> chosen, bool reject, CancellationToken ct, bool cancel = false)
    {
        snapshot = snapshot with { };
        await using var scope = scopes.CreateAsyncScope();
        var outbox = scope.ServiceProvider.GetRequiredService<IDbContextOutbox>();
        await using var context = await db.CreateDbContextAsync(ct);
        await using var transaction = await context.Database.BeginTransactionAsync(ct);
        outbox.Enroll(context);
        await CommentSources.LockAsync(context, ct);
        var draft = await context.CommentDrafts.SingleOrDefaultAsync(row => row.Id == snapshot.Id, ct);
        if (draft is null) return ActionResult.Fail("черновик не найден");
        if (draft.Revision != snapshot.Revision || draft.Status != snapshot.Status) return ActionResult.Fail("черновик изменился: обновите карточку");
        if (draft.Status != (cancel ? CommentStatus.Approved : CommentStatus.Pending)) return ActionResult.Fail($"уже {draft.Status}");
        if (cancel && draft.DeliveryRequestId is { } root)
        {
            var request = await context.CommentDeliveryRequests.SingleOrDefaultAsync(row => row.Id == root, ct);
            if (request is null || request.Stage is not (CommentDeliveryStage.Preparing or CommentDeliveryStage.Ready)) return ActionResult.Fail("доставка уже началась или требует сверки");
            request.Stage = CommentDeliveryStage.Cancelled;
            request.CompletedAt = time.GetUtcNow();
            request.ClaimEnvelopeId = null;
            request.RetryAt = null;
            request.Error = "cancelled by captured approval revision";
        }
        if (!reject)
        {
            var text = chosen(draft);
            if (text is null || text.Length is 0 or > 500) return ActionResult.Fail("нет варианта или текст пустой/длиннее 500 символов");
            var problems = CommentStyle.Problems(text, draft.Audience == AudienceKind.Tech);
            if (problems.Count > 0) return ActionResult.Fail("текст не прошёл проверку: " + string.Join("; ", problems));
            draft.ChosenText = text;
            draft.Attempts = 0;
        }
        draft.Status = reject ? CommentStatus.Rejected : CommentStatus.Approved;
        draft.Revision++;
        draft.DecidedAt = time.GetUtcNow();
        if (!reject)
        {
            if (await deliveries.QueueAsync(context, outbox, draft, ct) is null)
                notifier.Add(context, NotificationKind.Info, "💬 Комментарий #c" + draft.Id + " одобрен; публикация ждёт проверенного профиля.", entityId: draft.Id);
        }
        else notifier.Add(context, NotificationKind.Info, "Комментарий #c" + draft.Id + (cancel ? ": публикация отменена до отправки." : ": пропущен."), entityId: draft.Id);
        await notifier.SaveAndFlushAsync(context, outbox, ct);
        return ActionResult.Done(reject ? cancel ? "публикация отменена" : "пропущено"
            : draft.DeliveryRequestId is null ? "одобрено; публикация ждёт проверенного профиля" : "комментарий в очереди на публикацию");
    }
}
