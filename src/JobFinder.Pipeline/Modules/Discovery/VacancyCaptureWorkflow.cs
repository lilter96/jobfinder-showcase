using JobFinder.Core.Domain;
using JobFinder.Core.Telemetry;
using JobFinder.Core.Text;
using JobFinder.Data;
using JobFinder.Pipeline.Notifications;
using JobFinder.Pipeline.Profile;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Wolverine;
using Wolverine.Attributes;
using Wolverine.EntityFrameworkCore;

namespace JobFinder.Pipeline.Modules.Discovery;

public sealed class VacancyCaptureWorkflow(IDbContextFactory<JobFinderDbContext> db, IServiceScopeFactory scopes,
    VacancyAssessments assessments, ProfileProvider profiles, ServerOwnership ownership, Notifier notifier,
    TimeProvider time, ILogger<VacancyCaptureWorkflow> log)
{
    public async Task RunAsync(CaptureVacancyAssessment command, CancellationToken ct)
    {
        if (command.VacancyId <= 0 || command.CaptureId == Guid.Empty || command.Revision < 0) throw new UnsupportedVacancyAssessmentException();
        if (command.Version != 1)
        {
            await ChangeAsync(command, (context, _, vacancy) =>
            {
                vacancy.AssessmentCaptureId = null;
                notifier.Add(context, NotificationKind.Alert, $"⚠️ Неизвестная версия запроса оценки вакансии #{vacancy.Id}; запрос изолирован.", entityId: vacancy.Id);
                return Task.CompletedTask;
            }, ct);
            throw new UnsupportedVacancyAssessmentException();
        }
        ownership.EnsureOwned();
        try
        {
            await using var read = await db.CreateDbContextAsync(ct);
            var known = await read.Vacancies.AsNoTracking().SingleOrDefaultAsync(row => row.Id == command.VacancyId, ct);
            if (known is null || known.AssessmentCaptureId != command.CaptureId || known.AssessmentRevision != command.Revision) return;
            if (known.Status != VacancyStatus.New || await read.Applications.AnyAsync(application => application.VacancyId == known.Id
                && (application.Status == ApplicationStatus.Pending || application.Status == ApplicationStatus.Sending
                    || application.Status == ApplicationStatus.OutcomeUnknown || application.Status == ApplicationStatus.DryRun), ct))
            {
                await ChangeAsync(command, (_, _, vacancy) => { vacancy.AssessmentCaptureId = null; return Task.CompletedTask; }, ct);
                return;
            }
            using var stage = Telemetry.StartStage("capture", "vacancy-assessment").Tag("vacancy.id", known.Id).Tag("capture.id", command.CaptureId);
            if (await profiles.GetSnapshotAsync(ct) is not { Value.Reviewed: true })
            {
                await DeferAsync(command, TimeSpan.FromMinutes(2), ct);
                return;
            }
            ownership.EnsureOwned();
            var request = await assessments.RequestAsync(known.Id, ct, command.CaptureId);
            if (request is null) await DeferAsync(command, TimeSpan.FromSeconds(5), ct);
            else log.Info($"[vacancies] #{known.Id}: native capture handed off to {request}");
        }
        catch (Exception error) when (error is not OperationCanceledException && ownership.IsOwned)
        {
            await ChangeAsync(command, async (context, outbox, vacancy) =>
            {
                vacancy.Attempts++;
                vacancy.LastError = TextUtil.Truncate(error.Message, 900);
                vacancy.UpdatedAt = time.GetUtcNow();
                if (vacancy.Attempts >= 4)
                {
                    vacancy.Status = VacancyStatus.Failed;
                    vacancy.AssessmentCaptureId = null;
                    notifier.Add(context, NotificationKind.Alert, $"⚠️ Не удалось подготовить оценку вакансии #{vacancy.Id}: " + TextUtil.EscapeHtml(vacancy.LastError), entityId: vacancy.Id);
                }
                else await PublishAsync(outbox, command, time.GetUtcNow().AddMinutes(2));
            }, ct);
        }
    }

    private Task DeferAsync(CaptureVacancyAssessment command, TimeSpan delay, CancellationToken ct) => ChangeAsync(command,
        async (_, outbox, _) => await PublishAsync(outbox, command, time.GetUtcNow() + delay), ct);

    private async Task ChangeAsync(CaptureVacancyAssessment command,
        Func<JobFinderDbContext, IDbContextOutbox, Vacancy, Task> change, CancellationToken ct)
    {
        ownership.EnsureOwned();
        await using var scope = scopes.CreateAsyncScope();
        var outbox = scope.ServiceProvider.GetRequiredService<IDbContextOutbox>();
        await using var context = await db.CreateDbContextAsync(ct);
        await using var transaction = await context.Database.BeginTransactionAsync(ct);
        outbox.Enroll(context);
        var vacancy = await Modules.Hiring.DeliveryLedger.LockVacancyAsync(context, command.VacancyId, ct);
        if (vacancy is null || vacancy.AssessmentCaptureId != command.CaptureId || vacancy.AssessmentRevision != command.Revision) return;
        ownership.EnsureOwned();
        await change(context, outbox, vacancy);
        await notifier.SaveAndFlushAsync(context, outbox, ct);
    }

    private static ValueTask PublishAsync(IDbContextOutbox outbox, CaptureVacancyAssessment command, DateTimeOffset scheduled) =>
        outbox.PublishAsync(command, new DeliveryOptions { ScheduledTime = scheduled });
}

[NonTransactional]
public static class VacancyCaptureHandler
{
    public static Task Handle(CaptureVacancyAssessment command, VacancyCaptureWorkflow workflow, CancellationToken ct) => workflow.RunAsync(command, ct);
}
