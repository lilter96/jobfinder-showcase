using JobFinder.Core.Domain;
using JobFinder.Data;
using JobFinder.Pipeline.Actions;
using JobFinder.Pipeline.Modules.Preparation;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace JobFinder.Pipeline.Career;

/// <summary>
/// The hiring funnel of sent applications. Automatic signals (hh statuses, replies) only move an application forward — a late
/// "viewed" after an invitation changes nothing — while outcomes (rejection, offer, hired) always apply; the user's own choice applies
/// as given. Every change is an event, and reaching an interview stage opens an <see cref="Interview"/> (whose pack is then prepared).
/// </summary>
public sealed class FunnelService(IServiceScopeFactory scopes, TimeProvider time, ILogger<FunnelService> log)
{
    private static readonly ApplicationStage[] Order =
    [
        ApplicationStage.Sent, ApplicationStage.Viewed, ApplicationStage.Replied, ApplicationStage.HrScreen, ApplicationStage.TechInterview,
        ApplicationStage.Final, ApplicationStage.Offer, ApplicationStage.Accepted,
    ];

    public static bool IsTerminal(ApplicationStage s) =>
        s is ApplicationStage.Accepted or ApplicationStage.Rejected or ApplicationStage.NoResponse or ApplicationStage.Withdrawn;

    public static bool IsInterview(ApplicationStage s) => s is ApplicationStage.HrScreen or ApplicationStage.TechInterview or ApplicationStage.Final;

    public static string Label(ApplicationStage? s) => s switch
    {
        ApplicationStage.Sent => "отправлен",
        ApplicationStage.Viewed => "просмотрен",
        ApplicationStage.Replied => "ответили",
        ApplicationStage.HrScreen => "HR-скрининг",
        ApplicationStage.TechInterview => "тех. интервью",
        ApplicationStage.Final => "финал",
        ApplicationStage.Offer => "оффер",
        ApplicationStage.Accepted => "принят",
        ApplicationStage.Rejected => "отказ",
        ApplicationStage.NoResponse => "без ответа",
        ApplicationStage.Withdrawn => "отозван",
        _ => "—",
    };

    /// <summary>Applies a stage change inside the caller's unit of work. Returns false when an automatic change is not a move forward.</summary>
    public bool Advance(JobFinderDbContext ctx, Application app, ApplicationStage to, EventChannel channel, string? note, bool manual,
        DateTimeOffset? interviewAt = null, InterviewKind? interviewKind = null)
    {
        if (IsInterview(to)) throw new InvalidOperationException("interview scheduling belongs to InterviewTransitions");
        return ApplyStage(ctx, app, to, channel, note, manual, interviewAt);
    }

    internal bool ApplyStage(JobFinderDbContext ctx, Application app, ApplicationStage to, EventChannel channel, string? note, bool manual,
        DateTimeOffset? interviewAt = null)
    {
        var from = app.Stage;
        if (!manual && from is { } current)
        {
            // Closed applications stay closed for automatic signals, except that a rejected one can still turn into an offer.
            if (IsTerminal(current))
            {
                if (!(current == ApplicationStage.Rejected && to is ApplicationStage.Offer)) return false;
            }
            else if (!IsTerminal(to) && Rank(to) <= Rank(current) && !(IsInterview(to) && interviewAt is not null))
            {
                return false;
            }
        }

        var now = time.GetUtcNow();
        if (from != to)
        {
            app.Stage = to;
            app.StageChangedAt = now;
            ctx.ApplicationEvents.Add(new ApplicationEvent
            {
                ApplicationId = app.Id, At = now, Kind = EventKind.StageChanged, Channel = channel, Stage = to, Text = note,
            });
            log.Info($"[funnel] application {app.Id}: {Label(from)} → {Label(to)}{(note is null ? "" : " (" + note + ")")}");
        }

        return true;
    }

    /// <summary>The user's choice from the bot or the dashboard.</summary>
    public async Task<ActionResult> SetStageAsync(long applicationId, ApplicationStage stage, string? note, CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<InterviewTransitions>().SetStageAsync(applicationId, stage, note, ct);
    }

    /// <summary>Marks an application sent (the funnel's entry) and logs the outgoing letter.</summary>
    public void Entered(JobFinderDbContext ctx, Application app, EventChannel channel)
    {
        var now = time.GetUtcNow();
        app.Stage ??= ApplicationStage.Sent;
        app.StageChangedAt ??= now;
        app.LastOutboundAt = now;
        ctx.ApplicationEvents.Add(new ApplicationEvent
        {
            // The navigation, not the id: a manual application is new and has no id before the save.
            Application = app, At = now, Kind = EventKind.MessageOut, Channel = channel, Stage = ApplicationStage.Sent, Text = app.CoverLetter,
        });
    }

    public static InterviewKind KindFor(ApplicationStage stage) => stage switch
    {
        ApplicationStage.TechInterview => InterviewKind.Technical,
        ApplicationStage.Final => InterviewKind.Final,
        _ => InterviewKind.HrScreen,
    };

    public static ApplicationStage StageFor(InterviewKind kind) => kind switch
    {
        InterviewKind.HrScreen => ApplicationStage.HrScreen,
        InterviewKind.Final => ApplicationStage.Final,
        _ => ApplicationStage.TechInterview,
    };

    private static int Rank(ApplicationStage s)
    {
        var i = Array.IndexOf(Order, s);
        return i < 0 ? 100 : i;
    }
}
