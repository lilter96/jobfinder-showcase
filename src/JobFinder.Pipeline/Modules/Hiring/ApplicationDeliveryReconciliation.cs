using JobFinder.Core.Domain;
using JobFinder.Core.Telemetry;
using JobFinder.Core.Text;
using JobFinder.Data;
using JobFinder.Pipeline.Career;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace JobFinder.Pipeline.Modules.Hiring;

public sealed partial class DeliveryLedger
{
    public async Task<bool> ReconcileApplicationAsync(Guid id, bool sent, string evidence, CancellationToken ct)
    {
        using var stage = Telemetry.StartStage("reconcile", "application-delivery").Tag("request.id", id);
        await using var context = await db.CreateDbContextAsync(ct);
        await using var transaction = await context.Database.BeginTransactionAsync(ct);
        using var scope = (scopes ?? throw new InvalidOperationException("application reconciliation scope is required")).CreateScope();
        var recorded = await ReconcileApplicationAsync(context, id, sent, evidence,
            scope.ServiceProvider.GetRequiredService<FunnelService>(), time.GetUtcNow(), ct);
        if (!recorded) return false;
        await notifier.SaveAndFlushAsync(context, ct);
        return true;
    }

    internal static async Task<bool> ReconcileApplicationAsync(JobFinderDbContext context, Guid id, bool sent, string evidence,
        FunnelService funnel, DateTimeOffset now, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(evidence) || evidence.Length > 2000)
            throw new ArgumentException("нужно описать результат сверки (до 2000 символов)", nameof(evidence));
        if (context.Database.CurrentTransaction is null) throw new InvalidOperationException("reconciliation requires a source transaction");
        var known = await context.ApplicationDeliveryRequests.AsNoTracking().SingleOrDefaultAsync(row => row.Id == id, ct);
        if (known is not { Stage: ApplicationDeliveryStage.OutcomeUnknown }) return false;
        var vacancyId = await context.Applications.Where(row => row.Id == known.ApplicationId).Select(row => (long?)row.VacancyId).SingleOrDefaultAsync(ct);
        if (vacancyId is null || await LockVacancyAsync(context, vacancyId.Value, ct) is not { } vacancy) return false;
        var request = await context.ApplicationDeliveryRequests.SingleAsync(row => row.Id == id, ct);
        var application = await context.Applications.SingleAsync(row => row.Id == known.ApplicationId, ct);
        var attempt = await context.DeliveryAttempts.SingleOrDefaultAsync(row => row.ApplicationDeliveryRequestId == id, ct);
        if (request.Stage != ApplicationDeliveryStage.OutcomeUnknown || application.Status != ApplicationStatus.OutcomeUnknown
            || application.DeliveryRequestId != id || attempt is not { Status: DeliveryAttemptStatus.OutcomeUnknown }
            || attempt.ApplicationId != application.Id || attempt.OperationId != application.OperationId
            || attempt.ApplicationRevision != request.ApplicationRevision + 1 || application.Revision != attempt.ApplicationRevision + 1)
            return false;
        var note = "Сверено вручную: " + evidence.Trim() + ". Исходная доставка письма/PDF независимо не подтверждена.";
        request.Stage = sent ? ApplicationDeliveryStage.Completed : ApplicationDeliveryStage.Cancelled;
        request.ClaimEnvelopeId = null;
        request.RetryAt = null;
        request.CompletedAt = now;
        request.Error = note;
        attempt.Status = DeliveryAttemptStatus.Reconciled;
        attempt.CompletedAt = now;
        attempt.Error = note;
        application.Status = sent ? ApplicationStatus.Sent : ApplicationStatus.NeedsManual;
        application.SentAt = sent ? now : null;
        application.TailoredResumeId = null;
        application.Error = note;
        if (sent)
        {
            vacancy.Status = VacancyStatus.Applied;
            vacancy.UpdatedAt = now;
            funnel.Entered(context, application, EventChannel.Manual);
        }
        context.Notifications.Add(new Notification
        {
            Kind = NotificationKind.Info, Status = NotificationStatus.Pending, CreatedAt = now, EntityId = vacancy.Id,
            Text = $"🧾 Сверка отклика #{vacancy.Id}: {(sent ? "отправлено вручную" : "не отправлено; новое решение требуется вручную")}."
                + $"\nID операции: <code>{id}</code>\n{TextUtil.EscapeHtml(note)}\nНичего не отправлялось; автоматического повтора нет.",
        });
        return true;
    }
}
