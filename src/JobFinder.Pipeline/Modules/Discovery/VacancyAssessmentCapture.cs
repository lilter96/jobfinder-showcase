using JobFinder.Core.Domain;
using JobFinder.Data;
using JobFinder.Pipeline.Notifications;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Wolverine.EntityFrameworkCore;

namespace JobFinder.Pipeline.Modules.Discovery;

public sealed class VacancyAssessmentCapture(IDbContextFactory<JobFinderDbContext> db, IServiceScopeFactory scopes, Notifier notifier, TimeProvider time)
{
    public async Task SaveAsync(JobFinderDbContext context, Vacancy vacancy, CancellationToken ct)
    {
        if (context.Database.CurrentTransaction is null) throw new InvalidOperationException("ingest handoff requires the source transaction");
        if (context.Entry(vacancy).State == EntityState.Detached) throw new InvalidOperationException("ingest handoff requires the source context's tracked vacancy");
        await using var scope = scopes.CreateAsyncScope();
        var outbox = scope.ServiceProvider.GetRequiredService<IDbContextOutbox>();
        outbox.Enroll(context);
        await context.SaveChangesAsync(ct);
        await QueueAsync(context, outbox, vacancy, ct);
        await outbox.SaveChangesAndFlushMessagesAsync(ct);
    }

    public async Task<Guid?> RequestAsync(long id, CancellationToken ct)
    {
        await using var context = await db.CreateDbContextAsync(ct);
        await using var transaction = await context.Database.BeginTransactionAsync(ct);
        var vacancy = await Modules.Hiring.DeliveryLedger.LockVacancyAsync(context, id, ct);
        if (vacancy is not { Status: VacancyStatus.New }) return null;
        await using var scope = scopes.CreateAsyncScope();
        var outbox = scope.ServiceProvider.GetRequiredService<IDbContextOutbox>();
        outbox.Enroll(context);
        await QueueAsync(context, outbox, vacancy, ct);
        await outbox.SaveChangesAndFlushMessagesAsync(ct);
        return vacancy.AssessmentCaptureId;
    }

    public sealed record PendingVacancy(long Id, long Revision, Guid? CaptureId, int Attempts, DateTimeOffset UpdatedAt);

    public async Task<bool> FailPublishAsync(PendingVacancy expected, Exception error, CancellationToken ct)
    {
        await using var context = await db.CreateDbContextAsync(ct);
        await using var transaction = await context.Database.BeginTransactionAsync(ct);
        var vacancy = await Modules.Hiring.DeliveryLedger.LockVacancyAsync(context, expected.Id, ct);
        if (vacancy is not { Status: VacancyStatus.New } || vacancy.AssessmentRevision != expected.Revision
            || vacancy.AssessmentCaptureId != expected.CaptureId || vacancy.Attempts != expected.Attempts || vacancy.UpdatedAt != expected.UpdatedAt
            || await context.VacancyAssessmentRequests.AnyAsync(request => request.VacancyId == expected.Id
                && (request.Stage == VacancyAssessmentStage.Scoring || request.Stage == VacancyAssessmentStage.Lettering || request.Stage == VacancyAssessmentStage.Finishing), ct)) return false;
        vacancy.Attempts++;
        vacancy.LastError = Core.Text.TextUtil.Truncate(error.Message, 900);
        vacancy.UpdatedAt = time.GetUtcNow();
        if (vacancy.Attempts >= 4)
        {
            vacancy.Status = VacancyStatus.Failed;
            notifier.Add(context, NotificationKind.Alert, $"⚠️ Не удалось подготовить оценку вакансии #{expected.Id}: " + Core.Text.TextUtil.EscapeHtml(vacancy.LastError), entityId: expected.Id);
        }
        await notifier.SaveAndFlushAsync(context, ct);
        return true;
    }

    private static async Task QueueAsync(JobFinderDbContext context, IDbContextOutbox outbox, Vacancy vacancy, CancellationToken ct)
    {
        if (vacancy.Status != VacancyStatus.New || vacancy.AssessmentCaptureId is not null
            || await context.Applications.AnyAsync(application => application.VacancyId == vacancy.Id
                && (application.Status == ApplicationStatus.Pending || application.Status == ApplicationStatus.Sending
                    || application.Status == ApplicationStatus.OutcomeUnknown || application.Status == ApplicationStatus.DryRun), ct)) return;
        vacancy.AssessmentCaptureId = Guid.NewGuid();
        await outbox.PublishAsync(new CaptureVacancyAssessment { VacancyId = vacancy.Id, CaptureId = vacancy.AssessmentCaptureId.Value, Revision = vacancy.AssessmentRevision });
    }
}
