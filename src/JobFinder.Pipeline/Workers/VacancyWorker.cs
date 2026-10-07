using JobFinder.Core.Domain;
using JobFinder.Data;
using JobFinder.Pipeline.Jobs;
using JobFinder.Pipeline.Modules.Discovery;
using JobFinder.Pipeline.Profile;
using Microsoft.EntityFrameworkCore;

namespace JobFinder.Pipeline.Workers;

public sealed class VacancyWorker(IDbContextFactory<JobFinderDbContext> db, VacancyAssessmentCapture captures, ProfileProvider profiles,
    JobTriggers triggers, TimeProvider time, ILogger<VacancyWorker> log) : QueueWorker(triggers, time, log)
{
    public const int MaxApprovalCardsPerHour = VacancyAssessmentWorkflow.MaxApprovalCardsPerHour;
    private bool _profileWarning;
    public override string Name => JobNames.Vacancies;

    protected override async Task<int> ProcessBatchAsync(CancellationToken ct)
    {
        if (await profiles.GetSnapshotAsync(ct) is not { Value.Reviewed: true })
        {
            if (!_profileWarning) log.Warn($"[{Name}] scoring waits for a reviewed candidate profile");
            _profileWarning = true;
            return 0;
        }
        _profileWarning = false;
        List<VacancyAssessmentCapture.PendingVacancy> pending;
        await using (var context = await db.CreateDbContextAsync(ct))
        {
            var now = Time.GetUtcNow();
            var active = await context.VacancyAssessmentRequests.CountAsync(request =>
                (request.Stage == VacancyAssessmentStage.Scoring || request.Stage == VacancyAssessmentStage.Lettering || request.Stage == VacancyAssessmentStage.Finishing)
                && (request.RetryAt == null || request.RetryAt <= now), ct);
            if (active >= 2) return 0;
            var retryBefore = now.AddMinutes(-2);
            pending = await context.Vacancies.Where(vacancy => vacancy.Status == VacancyStatus.New && vacancy.AssessmentCaptureId == null
                && (vacancy.Attempts == 0 || vacancy.UpdatedAt <= retryBefore)
                && !context.VacancyAssessmentRequests.Any(request => request.VacancyId == vacancy.Id
                    && (request.Stage == VacancyAssessmentStage.Scoring || request.Stage == VacancyAssessmentStage.Lettering || request.Stage == VacancyAssessmentStage.Finishing))
                && !vacancy.Applications.Any(application => application.Status == ApplicationStatus.Pending || application.Status == ApplicationStatus.Sending
                    || application.Status == ApplicationStatus.OutcomeUnknown || application.Status == ApplicationStatus.DryRun))
                .OrderBy(vacancy => vacancy.Id).Select(vacancy => new VacancyAssessmentCapture.PendingVacancy(vacancy.Id, vacancy.AssessmentRevision, vacancy.AssessmentCaptureId, vacancy.Attempts, vacancy.UpdatedAt)).Take(2 - active).ToListAsync(ct);
        }
        var published = 0;
        foreach (var expected in pending)
        {
            try { if (await captures.RequestAsync(expected.Id, ct) is not null) published++; }
            catch (Exception error) when (error is not OperationCanceledException) { await captures.FailPublishAsync(expected, error, ct); }
        }
        return published;
    }

}
