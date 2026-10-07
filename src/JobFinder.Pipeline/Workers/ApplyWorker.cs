using System.Linq.Expressions;
using JobFinder.Core.Domain;
using JobFinder.Core.Telemetry;
using JobFinder.Data;
using JobFinder.Pipeline.Jobs;
using JobFinder.Pipeline.Modules.Hiring;
using JobFinder.Pipeline.Notifications;
using JobFinder.Pipeline.Settings;
using Microsoft.EntityFrameworkCore;

namespace JobFinder.Pipeline.Workers;

public sealed class ApplyWorker(IDbContextFactory<JobFinderDbContext> db, RuntimeSettings settings, Notifier notifier,
    JobTriggers triggers, TimeProvider time, ILogger<ApplyWorker> log, ApplicationDeliveryRequests deliveries) : QueueWorker(triggers, time, log)
{
    public const int DryRunHoldDays = ApplicationImportSources.DryRunHoldDays;

    public static Expression<Func<Application, bool>> CountsTowardAutoLimit(DateTimeOffset since) =>
        application => application.Mode == ApplicationMode.Auto && application.CreatedAt >= since
            && application.Status != ApplicationStatus.Cancelled && application.Status != ApplicationStatus.Failed;

    public override string Name => JobNames.Apply;

    protected override TimeSpan IdleDelay => TimeSpan.FromMinutes(1);

    internal static string LetterOf(Application application) => Core.Text.HumanText.Clean(application.CoverLetter ?? application.Vacancy?.CoverLetter ?? "");

    protected override Task<int> ProcessBatchAsync(CancellationToken ct) => ReconcileAsync(ct);

    internal async Task<int> ReconcileAsync(CancellationToken ct)
    {
        var dryRun = await settings.GetBoolAsync(RuntimeSettings.ApplyDryRun, false, ct);
        List<ApplicationImportSources.PendingApplication> pending;
        await using (var context = await db.CreateDbContextAsync(ct))
        {
            if (!dryRun) await ReleaseHeldAsync(context, notifier, Time.GetUtcNow(), ct, deliveries);
            pending = await context.Applications.AsNoTracking().Where(application => application.Status == ApplicationStatus.Pending
                && application.Channel != ApplicationChannel.Manual && application.Mode != ApplicationMode.External
                && !context.ApplicationDeliveryRequests.Any(request => request.ApplicationId == application.Id
                    && (request.Stage == ApplicationDeliveryStage.Sending || request.Stage == ApplicationDeliveryStage.OutcomeUnknown))
                && !context.DeliveryAttempts.Any(attempt => attempt.ApplicationId == application.Id
                    && (attempt.Status == DeliveryAttemptStatus.Submitting || attempt.Status == DeliveryAttemptStatus.OutcomeUnknown))
                && (application.DeliveryRequestId == null || !context.ApplicationDeliveryRequests.Any(request => request.Id == application.DeliveryRequestId
                    && request.ApplicationRevision == application.Revision)))
                .OrderBy(application => application.CreatedAt).Select(application => new ApplicationImportSources.PendingApplication(application.Id, application.VacancyId,
                    application.Revision, application.DeliveryRequestId, application.Channel, application.Target,
                    application.CoverLetter ?? application.Vacancy!.CoverLetter)).Take(25).ToListAsync(ct);
        }
        var queued = 0;
        foreach (var application in pending)
        {
            if (MissingInput(application.Channel, application.Target, application.Text) && await ApplicationImportSources.RetireInvalidAsync(db, notifier, application, ct)) { queued++; continue; }
            try { if (await deliveries.RequestAsync(application.Id, ct) is not null) queued++; }
            catch (ApplicationDeliverySupersededException) { }
        }
        return queued;
    }

    private static bool MissingInput(ApplicationChannel channel, string? target, string? text) =>
        string.IsNullOrWhiteSpace(Core.Text.HumanText.Clean(text)) || channel != ApplicationChannel.HeadHunter && string.IsNullOrWhiteSpace(target);

    internal static Task<(int Released, int Expired)> ReleaseHeldAsync(JobFinderDbContext context, Notifier notifier, DateTimeOffset now,
        CancellationToken ct, ApplicationDeliveryRequests? deliveries = null) =>
        ApplicationImportSources.ReleaseHeldAsync(context, notifier, now, ct, deliveries);

    internal static string UserText(string message) => message switch
    {
        "no apply button" => "на странице нет кнопки «Откликнуться»",
        "the employer asks questions or a test" => "работодатель просит ответить на вопросы или пройти тест",
        "vacancy is closed or archived" => "вакансия закрыта или в архиве",
        "no success marker after the response" => "после отклика сайт не показал подтверждение: проверьте «Отклики» на сайте",
        _ => message,
    };

    internal static string? Note(string? message) => message switch
    {
        "already applied" => "уже был отклик",
        "already applied, letter added" => "уже был отклик — письмо добавлено",
        { } note when note.StartsWith("already applied; letter: ", StringComparison.Ordinal) => "уже был отклик; письмо не добавилось: " + note["already applied; letter: ".Length..],
        { } note when note.StartsWith("sent; letter: ", StringComparison.Ordinal) => "письмо не добавилось: " + note["sent; letter: ".Length..] + " — допишите его в чате hh",
        _ => null,
    };
}
