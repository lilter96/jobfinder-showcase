using JobFinder.Core.Domain;
using JobFinder.Core.Telemetry;
using JobFinder.Data;
using JobFinder.Pipeline.Notifications;
using Microsoft.EntityFrameworkCore;

namespace JobFinder.Pipeline.Modules.Hiring;

/// <summary>Owns conservative legacy application retirement and dry-run source handoff.</summary>
internal static class ApplicationImportSources
{
    public const int DryRunHoldDays = 7;
    private static bool MissingInput(ApplicationChannel channel, string? target, string? text) =>
        string.IsNullOrWhiteSpace(Core.Text.HumanText.Clean(text)) || channel != ApplicationChannel.HeadHunter && string.IsNullOrWhiteSpace(target);

    public static async Task<bool> RetireInvalidAsync(IDbContextFactory<JobFinderDbContext> db, Notifier notifier, PendingApplication expected, CancellationToken ct)
    {
        using var stage = Telemetry.StartStage("import-reject", "application-delivery").Tag("application.id", expected.Id);
        await using var context = await db.CreateDbContextAsync(ct);
        await using var transaction = await context.Database.BeginTransactionAsync(ct);
        var vacancy = await DeliveryLedger.LockVacancyAsync(context, expected.VacancyId, ct);
        if (vacancy is null) return false;
        var application = await context.Applications.SingleOrDefaultAsync(row => row.Id == expected.Id, ct);
        if (application is not { Status: ApplicationStatus.Pending } || application.Revision != expected.Revision
            || application.DeliveryRequestId != expected.RequestId || application.Channel == ApplicationChannel.Manual || application.Mode == ApplicationMode.External
            || !MissingInput(application.Channel, application.Target, application.CoverLetter ?? vacancy.CoverLetter)) return false;
        if (await context.ApplicationDeliveryRequests.AnyAsync(request => request.ApplicationId == application.Id
                && (request.Stage == ApplicationDeliveryStage.Sending || request.Stage == ApplicationDeliveryStage.OutcomeUnknown), ct)
            || await context.DeliveryAttempts.AnyAsync(attempt => attempt.ApplicationId == application.Id
                && (attempt.Status == DeliveryAttemptStatus.Submitting || attempt.Status == DeliveryAttemptStatus.OutcomeUnknown), ct)) return false;
        var reason = string.IsNullOrWhiteSpace(Core.Text.HumanText.Clean(application.CoverLetter ?? vacancy.CoverLetter))
            ? "в старом отклике нет текста письма; требуется новое решение" : "в старом отклике не указан адресат; требуется новое решение";
        application.Status = ApplicationStatus.NeedsManual;
        application.Revision++;
        application.Error = reason;
        notifier.Add(context, NotificationKind.ApplyFailed, Cards.ApplyProblem(vacancy, "нужно вручную", reason), Cards.ManualRow(vacancy.Id), vacancy.Id);
        await notifier.SaveAndFlushAsync(context, ct);
        return true;
    }

    internal sealed record PendingApplication(long Id, long VacancyId, long Revision, Guid? RequestId, ApplicationChannel Channel, string? Target, string? Text);

    public static async Task<(int Released, int Expired)> ReleaseHeldAsync(JobFinderDbContext context, Notifier notifier, DateTimeOffset now,
        CancellationToken ct, ApplicationDeliveryRequests? deliveries = null)
    {
        await using var transaction = await context.Database.BeginTransactionAsync(ct);
        var cutoff = now.AddDays(-DryRunHoldDays);
        var captured = await context.Applications.AsNoTracking().Where(application => application.Status == ApplicationStatus.DryRun)
            .OrderBy(application => application.VacancyId).ThenBy(application => application.Id)
            .Select(application => new { application.Id, application.VacancyId, application.Revision, application.CreatedAt, application.DeliveryRequestId }).ToListAsync(ct);
        foreach (var vacancyId in captured.Select(row => row.VacancyId).Distinct())
            await DeliveryLedger.LockVacancyAsync(context, vacancyId, ct);
        var ready = new List<Application>();
        foreach (var expected in captured)
        {
            var current = await context.Applications.SingleOrDefaultAsync(row => row.Id == expected.Id, ct);
            if (current is not { Status: ApplicationStatus.DryRun } || current.Revision != expected.Revision
                || current.CreatedAt != expected.CreatedAt || current.DeliveryRequestId != expected.DeliveryRequestId) continue;
            if (await context.ApplicationDeliveryRequests.AnyAsync(row => row.ApplicationId == current.Id
                    && (row.Stage == ApplicationDeliveryStage.Sending || row.Stage == ApplicationDeliveryStage.OutcomeUnknown), ct)
                || await context.DeliveryAttempts.AnyAsync(row => row.ApplicationId == current.Id
                    && (row.Status == DeliveryAttemptStatus.Submitting || row.Status == DeliveryAttemptStatus.OutcomeUnknown), ct)) continue;
            ready.Add(current);
        }
        if (deliveries is null && ready.Any(row => row.CreatedAt >= cutoff && row.DeliveryRequestId != null))
            throw new InvalidOperationException("native dry-run release requires the native source handoff");
        var expired = 0;
        var applications = new List<Application>();
        foreach (var application in ready)
        {
            application.Revision = checked(application.Revision + 1);
            if (application.CreatedAt < cutoff)
            {
                application.Status = ApplicationStatus.Cancelled;
                application.Error = $"dry-run: не отправлен, прошло больше {DryRunHoldDays} дн.";
                expired++;
            }
            else
            {
                application.Status = ApplicationStatus.Pending;
                application.Error = null;
                applications.Add(application);
            }
        }
        var released = applications.Count;
        if (expired > 0)
            notifier.Add(context, NotificationKind.Info,
                $"🧪 Dry-run выключен. Не отправлены проверенные отклики старше {DryRunHoldDays} дн. (вакансии могли устареть): {expired}, они в истории на дашборде.");
        if (deliveries is not null && released > 0)
        {
            await deliveries.SaveAsync(context, applications, ct);
        }
        else
        {
            await notifier.SaveAndFlushAsync(context, ct);
        }
        return (released, expired);
    }

}
