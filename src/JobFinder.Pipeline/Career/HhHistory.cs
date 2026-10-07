using System.Globalization;
using JobFinder.Core.Domain;
using JobFinder.Data;
using JobFinder.Integrations.HeadHunter;

namespace JobFinder.Pipeline.Career;

/// <summary>
/// The user's responses made on hh / rabota.by (the "Отклики" page, <see cref="HhResponse"/>) joined to collected vacancies: an External
/// application with the hh stage and dates, so the funnel, the analytics and the rejection review see them like the system's own.
/// </summary>
public static class HhHistory
{
    /// <summary>The funnel stage of an hh response state; null for a state nobody mapped yet.</summary>
    public static ApplicationStage? StageOf(string state, bool viewedByEmployer) => HhNegotiations.Direction(state) switch
    {
        "sent" => viewedByEmployer ? ApplicationStage.Viewed : ApplicationStage.Sent,
        "viewed" => ApplicationStage.Viewed,
        "invitation" => ApplicationStage.HrScreen,
        "offer" => ApplicationStage.Offer,
        "rejected" => ApplicationStage.Rejected,
        "hired" => ApplicationStage.Accepted,
        _ => null,
    };

    /// <summary>
    /// The response as the vacancy's application (its <see cref="Vacancy.Applications"/> must be loaded). A response of ours still
    /// waiting in the queue would be a duplicate and is cancelled; the vacancy leaves the approval queue.
    /// </summary>
    public static Application Link(JobFinderDbContext ctx, Vacancy v, HhResponse r, ApplicationStage stage, DateTimeOffset now)
    {
        foreach (var queued in v.Applications.Where(a => a.Status is ApplicationStatus.Pending or ApplicationStatus.NeedsManual or ApplicationStatus.DryRun))
        {
            queued.Status = ApplicationStatus.Cancelled;
            queued.Error = "вы уже откликнулись на сайте";
        }

        var app = new Application
        {
            VacancyId = v.Id, Channel = ApplicationChannel.HeadHunter, Mode = ApplicationMode.External, Status = ApplicationStatus.Sent,
            CreatedAt = r.RespondedAt ?? now, SentAt = r.RespondedAt ?? now, Stage = stage, StageChangedAt = r.UpdatedAt ?? now,
        };
        ctx.Applications.Add(app);
        v.Applications.Add(app);
        ctx.ApplicationEvents.Add(new ApplicationEvent
        {
            Application = app, At = r.RespondedAt ?? now, Kind = EventKind.Note, Channel = EventChannel.HeadHunter, Stage = stage,
            Text = $"отклик сделан на сайте {r.RespondedAt?.ToString("dd.MM.yyyy", CultureInfo.InvariantCulture)}, статус hh: {r.State}" +
                   (r.ViewedByEmployer ? ", резюме просмотрено" : ""),
        });
        v.Status = VacancyStatus.Applied;
        v.UpdatedAt = now;
        return app;
    }
}
