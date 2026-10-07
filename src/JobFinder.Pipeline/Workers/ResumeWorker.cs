using JobFinder.Core.Domain;
using JobFinder.Data;
using JobFinder.Pipeline.Documents;
using JobFinder.Pipeline.Jobs;
using JobFinder.Pipeline.Profile;
using Microsoft.EntityFrameworkCore;

namespace JobFinder.Pipeline.Workers;

/// <summary>
/// Prepares a tailored resume for every vacancy that reached the approval or auto-apply zone, so the PDF is ready when the user opens the
/// card or presses "send". Two different vacancies at a time: each is a long LLM call plus a PDF print.
/// </summary>
public sealed class ResumeWorker(IDbContextFactory<JobFinderDbContext> db, ResumeTailor tailor, ProfileProvider profiles, JobTriggers triggers,
    TimeProvider time, ILogger<ResumeWorker> log) : QueueWorker(triggers, time, log)
{
    public override string Name => JobNames.Resumes;

    protected override TimeSpan IdleDelay => TimeSpan.FromMinutes(2);

    /// <summary>
    /// The vacancies a resume should be ready for, best scored first: approved or auto-applied, not a scam, seen since
    /// <paramref name="since"/>, and with no resume yet — or, while still waiting for a decision, none reviewed by the current pipeline
    /// (an old one or one made while the models were unreachable would go out on approval).
    /// </summary>
    internal static IQueryable<long> Waiting(JobFinderDbContext ctx, DateTimeOffset since) => ctx.Vacancies
        .Where(v => (v.Status == VacancyStatus.Scored || v.Status == VacancyStatus.Applied)
                    && (v.Decision == Decision.Approval || v.Decision == Decision.AutoApply)
                    && v.Legitimacy != Legitimacy.Scam
                    && v.FirstSeenAt >= since
                    && (!v.TailoredResumes.Any() || v.Status == VacancyStatus.Scored
                        && !v.TailoredResumes.Any(t => t.ReportJson != null && EF.Functions.JsonContains(t.ReportJson, "{\"ready\":true}"))))
        .OrderByDescending(v => v.Score).Select(v => v.Id);

    protected override async Task<int> ProcessBatchAsync(CancellationToken ct)
    {
        if (await profiles.GetAsync(ct) is not { Reviewed: true } || profiles.FindResume() is null) return 0;
        var since = Time.GetUtcNow().AddDays(-21);
        List<long> ids;
        await using (var ctx = await db.CreateDbContextAsync(ct))
        {
            var now = Time.GetUtcNow();
            var active = await ctx.ResumePreparationRequests.CountAsync(request =>
                (request.Stage == ResumePreparationStage.Drafting || request.Stage == ResumePreparationStage.Printing
                    || request.Stage == ResumePreparationStage.Reviewing || request.Stage == ResumePreparationStage.Finalizing)
                && (request.RetryAt == null || request.RetryAt <= now), ct);
            if (active >= 2) return 0;
            var retrySince = Time.GetUtcNow().AddHours(-1);
            ids = await Waiting(ctx, since).Where(id => !ctx.ResumePreparationRequests.Any(request => request.VacancyId == id
                && (request.Stage == ResumePreparationStage.Drafting || request.Stage == ResumePreparationStage.Printing
                    || request.Stage == ResumePreparationStage.Reviewing || request.Stage == ResumePreparationStage.Finalizing
                    || request.CompletedAt > retrySince))).Take(2 - active).ToListAsync(ct);
        }

        var published = 0;
        foreach (var id in ids) published += await PrepareAsync(id, ct);
        return published;
    }

    private async Task<int> PrepareAsync(long id, CancellationToken ct)
    {
        try
        {
            var ticket = await tailor.RequestAsync(id, ct);
            return ticket.RequestId is null ? 0 : 1;
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            log.Warn($"[{Name}] #{id}: {e.Message}");
        }
        return 0;
    }
}
