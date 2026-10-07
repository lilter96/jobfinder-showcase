using System.Text.Json;
using JobFinder.Core.Domain;
using JobFinder.Core.Telemetry;
using JobFinder.Core.Text;
using JobFinder.Data;
using JobFinder.Pipeline.Career;
using JobFinder.Pipeline.Messaging;
using JobFinder.Pipeline.Modules.Preparation;
using JobFinder.Pipeline.Notifications;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Wolverine.EntityFrameworkCore;

namespace JobFinder.Pipeline.Modules.Hiring;

public sealed record FollowUpSnapshot
{
    public long ApplicationId { get; set; }
    public long VacancyId { get; set; }
    public long Revision { get; set; }
    public Guid OperationId { get; set; }
    public Guid? DeliveryRequestId { get; set; }
    public ApplicationMode Mode { get; set; }
    public ApplicationStatus Status { get; set; }
    public ApplicationStage? Stage { get; set; }
    public ApplicationChannel Channel { get; set; }
    public string? Target { get; set; }
    public long? TargetPeerId { get; set; }
    public string? EmailMessageId { get; set; }
    public string? CoverLetter { get; set; }
    public DateTimeOffset? SentAt { get; set; }
    public DateTimeOffset? StageChangedAt { get; set; }
    public DateTimeOffset? LastInboundAt { get; set; }
    public DateTimeOffset? LastOutboundAt { get; set; }
    public DateTimeOffset? ProposedAt { get; set; }
    public string VacancyHash { get; set; } = "";

    public static FollowUpSnapshot Capture(Application application) => new()
    {
        ApplicationId = application.Id, VacancyId = application.VacancyId, Revision = application.Revision,
        OperationId = application.OperationId, DeliveryRequestId = application.DeliveryRequestId, Mode = application.Mode,
        Status = application.Status, Stage = application.Stage, Channel = application.Channel, Target = application.Target,
        TargetPeerId = application.TargetPeerId, EmailMessageId = application.EmailMessageId, CoverLetter = application.CoverLetter,
        SentAt = application.SentAt, StageChangedAt = application.StageChangedAt, LastInboundAt = application.LastInboundAt,
        LastOutboundAt = application.LastOutboundAt, ProposedAt = application.FollowUpSentAt,
        VacancyHash = CanonicalJson.Hash(JsonSerializer.Serialize(AssessmentVacancy.From(application.Vacancy
            ?? throw new ArgumentException("follow-up capture requires the loaded vacancy", nameof(application))),
            LetterPreparationJsonContext.Default.AssessmentVacancy)),
    };

    public bool Matches(Application application) => this == Capture(application);
}

public sealed class FollowUpSources(IDbContextFactory<JobFinderDbContext> db, NativePublicationScopes scopes,
    InterviewTransitions transitions, Notifier notifier, TimeProvider time)
{
    public async Task<bool> DraftAsync(FollowUpSnapshot snapshot, LetterDraft draft, int days, CancellationToken ct)
    {
        if (days < 0) throw new ArgumentOutOfRangeException(nameof(days));
        if (draft.Problems.Count != 0 || string.IsNullOrWhiteSpace(draft.Text)) return false;
        using var span = Telemetry.StartStage("publish-draft", "follow-up").Tag("application.id", snapshot.ApplicationId);
        await using var context = await db.CreateDbContextAsync(ct);
        await using var transaction = await context.Database.BeginTransactionAsync(ct);
        var application = await CurrentAsync(context, snapshot, ct);
        if (application is null || application.Channel is not (ApplicationChannel.HeadHunter or ApplicationChannel.TelegramDm or ApplicationChannel.Email)
            || application.FollowUpSentAt is not null) return false;
        await using var scope = scopes.CreateAsyncScope();
        var outbox = scope.ServiceProvider.GetRequiredService<IDbContextOutbox>();
        outbox.Enroll(context);
        var now = time.GetUtcNow();
        var source = new ApplicationEvent
        {
            ApplicationId = application.Id, At = now, Kind = EventKind.FollowUp,
            Channel = application.Channel switch
            {
                ApplicationChannel.TelegramDm => EventChannel.Telegram, ApplicationChannel.Email => EventChannel.Email,
                ApplicationChannel.HeadHunter => EventChannel.HeadHunter, _ => EventChannel.Manual,
            },
            Author = application.Channel == ApplicationChannel.HeadHunter ? null : application.Target,
            Draft = draft.Text, DraftStatus = DraftStatus.Pending,
            ExternalId = application.Channel == ApplicationChannel.Email ? application.EmailMessageId : null,
        };
        context.ApplicationEvents.Add(source);
        application.FollowUpSentAt = now;
        await notifier.SaveJoinedAsync(context, outbox, ct);
        var (card, buttons) = CareerCards.FollowUp(application, source, days);
        notifier.Add(context, NotificationKind.Info, card, buttons, application.Id);
        await notifier.SaveAndFlushAsync(context, outbox, ct);
        return true;
    }

    public async Task<bool> CloseAsync(FollowUpSnapshot snapshot, string reason, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(reason)) throw new ArgumentException("closure requires a reason", nameof(reason));
        using var span = Telemetry.StartStage("close-source", "follow-up").Tag("application.id", snapshot.ApplicationId);
        await using var context = await db.CreateDbContextAsync(ct);
        await using var transaction = await context.Database.BeginTransactionAsync(ct);
        var application = await CurrentAsync(context, snapshot, ct);
        if (application is null) return false;
        await using var scope = scopes.CreateAsyncScope();
        var outbox = scope.ServiceProvider.GetRequiredService<IDbContextOutbox>();
        outbox.Enroll(context);
        if (!await transitions.AdvanceAsync(context, outbox, application, ApplicationStage.NoResponse, EventChannel.System,
                reason, manual: false, ct)) return false;
        notifier.Add(context, NotificationKind.Info, "Стадия отклика #" + application.Id + ": " + FunnelService.Label(ApplicationStage.NoResponse),
            [CareerCards.StageRow(application.Id)], application.Id);
        await notifier.SaveAndFlushAsync(context, outbox, ct);
        return true;
    }

    private static async Task<Application?> CurrentAsync(JobFinderDbContext context, FollowUpSnapshot snapshot, CancellationToken ct)
    {
        await InterviewPreparationCapture.LockCandidateAsync(context, ct);
        if (await DeliveryLedger.LockVacancyAsync(context, snapshot.VacancyId, ct) is null) return null;
        var application = await context.Applications.FromSqlInterpolated($"SELECT * FROM applications WHERE id = {snapshot.ApplicationId} FOR UPDATE")
            .Include(row => row.Vacancy).ThenInclude(row => row!.Company).Include(row => row.Vacancy).ThenInclude(row => row!.Sources)
            .SingleOrDefaultAsync(ct);
        if (application is not { Status: ApplicationStatus.Sent, SentAt: not null, LastInboundAt: null,
                Stage: ApplicationStage.Sent or ApplicationStage.Viewed }
            || !snapshot.Matches(application) || await context.Applications.AnyAsync(row => row.VacancyId == snapshot.VacancyId
                && (row.Status == ApplicationStatus.Sending || row.Status == ApplicationStatus.OutcomeUnknown), ct)
            || await context.EmployerMessages.AnyAsync(row => row.VacancyId == snapshot.VacancyId
                && (row.Status == EmployerMessageStatus.Sending || row.Status == EmployerMessageStatus.OutcomeUnknown), ct)) return null;
        return application;
    }
}
