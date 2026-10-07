using JobFinder.Core.Domain;
using JobFinder.Core.Text;
using JobFinder.Data;
using Microsoft.EntityFrameworkCore;

namespace JobFinder.Pipeline.Notifications;

public sealed class NotificationDeliveryLedger(IDbContextFactory<JobFinderDbContext> db, TimeProvider time)
{
    public async Task ReconcileAsync(Guid id, bool sent, string evidence, CancellationToken ct)
    {
        if (id == Guid.Empty || string.IsNullOrWhiteSpace(evidence) || evidence.Length > 2000)
            throw new ArgumentException("notification reconciliation requires checked evidence");
        await using var context = await db.CreateDbContextAsync(ct);
        await using var transaction = await context.Database.BeginTransactionAsync(ct);
        await NotificationDeliveryRequests.LockAsync(context, ct);
        var root = await context.NotificationDeliveryRequests.SingleAsync(row => row.Id == id, ct);
        if (root.Stage != NotificationDeliveryStage.OutcomeUnknown)
            throw new InvalidOperationException("only an unknown notification effect can be reconciled");
        var input = NotificationDeliveryInputs.Input(root);
        if (input.LegacySource is not null)
        {
            var raw = await LegacyNotificationImports.RawAsync(context, root.NotificationId, ct);
            var source = System.Text.Json.Nodes.JsonNode.Parse(raw)!.AsObject();
            if (source["delivery_request_id"]?.GetValue<Guid>() != root.Id || source["status"]?.GetValue<string>() != nameof(NotificationStatus.OutcomeUnknown)
                || !input.LegacySource.Matches(raw, root.NotificationId)) throw new UnsupportedNotificationDeliveryException();
            var checkedAt = time.GetUtcNow();
            checkedAt = new DateTimeOffset(checkedAt.UtcTicks - checkedAt.UtcTicks % 10, TimeSpan.Zero);
            var receipt = new LegacyNotificationReconciliation { Sent = sent, Evidence = evidence, At = checkedAt, InputHash = root.InputHash };
            root.LegacyReconciliationJson = System.Text.Json.JsonSerializer.Serialize(receipt, NotificationDeliveryJsonContext.Default.LegacyNotificationReconciliation);
            root.LegacyReconciliationHash = CanonicalJson.Hash(root.LegacyReconciliationJson);
            root.Stage = NotificationDeliveryStage.Reconciled;
            root.CompletedAt = checkedAt;
            root.Error = "legacy aggregate checked by owner; no effect/PDF certification: " + evidence;
            NotificationDeliveryInputs.Input(root);
            await context.Notifications.Where(row => row.Id == root.NotificationId && row.DeliveryRequestId == root.Id && row.Status == NotificationStatus.OutcomeUnknown)
                .ExecuteUpdateAsync(setters => setters.SetProperty(row => row.Status, NotificationStatus.Reconciled), ct);
            await context.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
            return;
        }
        if (root.AttemptId is null) throw new InvalidOperationException("unknown native notification has no captured effect attempt");
        var notice = await context.Notifications.SingleOrDefaultAsync(row => row.Id == root.NotificationId, ct);
        var attempt = await context.NotificationDeliveryAttempts.SingleAsync(row => row.Id == root.AttemptId, ct);
        if (attempt.Status != DeliveryAttemptStatus.OutcomeUnknown || attempt.RequestId != root.Id || attempt.EffectIndex != root.EffectIndex
            || attempt.InputHash != root.InputHash || attempt.PlanHash != root.PlanHash || CanonicalJson.Hash(attempt.EffectJson) != attempt.EffectHash)
            throw new UnsupportedNotificationDeliveryException();
        var effect = System.Text.Json.JsonSerializer.Serialize(NotificationDeliveryInputs.Effect(root), NotificationDeliveryJsonContext.Default.NotificationEffect);
        if (CanonicalJson.Hash(effect) != attempt.EffectHash) throw new UnsupportedNotificationDeliveryException();
        attempt.Status = DeliveryAttemptStatus.Reconciled;
        attempt.ReconciledSent = sent;
        attempt.Error = "checked by owner: " + evidence;
        attempt.CompletedAt = time.GetUtcNow();
        root.Stage = NotificationDeliveryStage.Reconciled;
        root.CompletedAt = time.GetUtcNow();
        root.Error = attempt.Error;
        if (notice is not null && notice.Status == NotificationStatus.OutcomeUnknown && notice.DeliveryRequestId == root.Id && input.Matches(notice))
        {
            notice.Error = attempt.Error;
            notice.Status = NotificationStatus.Reconciled;
        }
        await context.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
    }
}
