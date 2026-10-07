using System.Text.Json;
using JobFinder.Core.Domain;
using JobFinder.Core.Profile;
using JobFinder.Core.Text;
using JobFinder.Core.Time;
using JobFinder.Data;
using JobFinder.Llm.Tasks;
using JobFinder.Pipeline.Actions;
using JobFinder.Pipeline.Career;
using JobFinder.Pipeline.Documents;
using JobFinder.Pipeline.Modules.Preparation;
using JobFinder.Pipeline.Notifications;
using JobFinder.Pipeline.Profile;
using Microsoft.EntityFrameworkCore;

namespace JobFinder.Pipeline.Modules.Hiring;

public sealed record ReplyDraftSnapshot
{
    public long EventId { get; set; }
    public long ApplicationId { get; set; }
    public long VacancyId { get; set; }
    public string SourceHash { get; set; } = "";
    public string ContextHash { get; set; } = "";
    public long ProfileVersion { get; set; }
    public long FactsVersion { get; set; }
    public string StoriesHash { get; set; } = "";
}

public sealed record CapturedReplyDraft(ReplyDraftSnapshot Snapshot, ApplicationEvent Event, Application Application,
    CandidateProfile Profile, CandidateFacts Facts, string Stories)
{
    public string[] Interviews { get; set; } = [];
}

public sealed record ReplyEditSnapshot
{
    public long EventId { get; set; }
    public long ApplicationId { get; set; }
    public long VacancyId { get; set; }
    public string SourceHash { get; set; } = "";

    public int Version => ReplyService.DraftVersion(SourceHash);

    public static ReplyEditSnapshot Capture(ApplicationEvent source, Application application) => new()
    {
        EventId = source.Id, ApplicationId = application.Id, VacancyId = application.VacancyId,
        SourceHash = CanonicalJson.Hash(JsonSerializer.Serialize(new
        {
            source.Id, source.ApplicationId, source.Revision, source.Kind, source.Channel, source.Author, source.Text,
            source.ExternalId, source.Stage, source.Classification, source.Draft, source.DraftStatus,
            AtMicroseconds = source.At.UtcTicks / 10,
            Application = new
            {
                application.Id, application.VacancyId, application.Revision, application.Status, application.Channel,
                application.Target, application.TargetPeerId, application.EmailMessageId, application.Stage,
            },
        })),
    };

    public bool Matches(ApplicationEvent source, Application application) => this == Capture(source, application);
}

public sealed class ReplyDraftSources(IDbContextFactory<JobFinderDbContext> db, Notifier notifier, LocalClock clock, TimeProvider time)
{
    public async Task<ReplyEditSnapshot?> CaptureEditAsync(long eventId, int? version, CancellationToken ct)
    {
        if (version <= 0) return null;
        await using var context = await db.CreateDbContextAsync(ct);
        var source = await context.ApplicationEvents.AsNoTracking().Include(row => row.Application).ThenInclude(row => row!.Vacancy)
            .SingleOrDefaultAsync(row => row.Id == eventId, ct);
        if (source?.Application?.Vacancy is not { } vacancy || !Editable(vacancy, source) || source.DraftStatus != DraftStatus.Pending
            || source.Application.Status != ApplicationStatus.Sent) return null;
        var captured = ReplyEditSnapshot.Capture(source, source.Application);
        return version is null || captured.Version == version ? captured : null;
    }

    public async Task<ActionResult> DismissAsync(ReplyEditSnapshot snapshot, CancellationToken ct)
    {
        await using var context = await db.CreateDbContextAsync(ct);
        await using var transaction = await context.Database.BeginTransactionAsync(ct);
        var vacancy = await DeliveryLedger.LockVacancyAsync(context, snapshot.VacancyId, ct);
        var application = await context.Applications.FromSqlInterpolated($"SELECT * FROM applications WHERE id = {snapshot.ApplicationId} FOR UPDATE")
            .AsNoTracking().SingleOrDefaultAsync(ct);
        var source = await context.ApplicationEvents.FromSqlInterpolated($"SELECT * FROM application_events WHERE id = {snapshot.EventId} FOR UPDATE")
            .AsNoTracking().SingleOrDefaultAsync(ct);
        if (vacancy is null || application is null || source is null || !Editable(vacancy, source)
            || source.DraftStatus != DraftStatus.Pending || application.Status != ApplicationStatus.Sent
            || !snapshot.Matches(source, application)
            || await EmployerMessageSources.UnresolvedDeliveryAsync(context, snapshot.VacancyId, ct)) return Conflict();
        var pending = await context.EmployerMessages.Where(row => row.VacancyId == snapshot.VacancyId
            && (row.Status == EmployerMessageStatus.Pending || row.Status == EmployerMessageStatus.Ready)).ToListAsync(ct);
        var cancel = new List<EmployerMessage>();
        try
        {
            foreach (var message in pending)
                if (EmployerMessageSources.Read(message).EventId == snapshot.EventId) cancel.Add(message);
        }
        catch (Exception error) when (error is JsonException or UnsupportedEmployerMessageException)
        {
            return ActionResult.Fail("переданная на отправку операция требует проверки; действие не применено");
        }
        foreach (var message in cancel)
        {
            message.Status = EmployerMessageStatus.Cancelled;
            message.CompletedAt = time.GetUtcNow();
            message.Error = "reply dismissed by a captured source decision";
        }
        context.Entry(source).State = EntityState.Unchanged;
        source.DraftStatus = DraftStatus.Dismissed;
        source.Revision++;
        notifier.Add(context, NotificationKind.Info, $"Ответ на сообщение #{source.Id}: без ответа", null, application.Id);
        await notifier.SaveAndFlushAsync(context, ct);
        return ActionResult.Done("без ответа");
    }

    public async Task<CapturedReplyDraft?> CaptureAsync(long eventId, CancellationToken ct)
    {
        await using var context = await db.CreateDbContextAsync(ct);
        var vacancyId = await context.ApplicationEvents.Where(row => row.Id == eventId)
            .Select(row => (long?)row.Application!.VacancyId).SingleOrDefaultAsync(ct);
        if (vacancyId is null) return null;
        await using var transaction = await context.Database.BeginTransactionAsync(ct);
        await InterviewPreparationCapture.LockCandidateAsync(context, ct);
        if (await DeliveryLedger.LockVacancyAsync(context, vacancyId.Value, ct) is null) return null;
        var vacancy = await EmployerMessageSources.VacancyAsync(context, vacancyId.Value, ct);
        var application = vacancy?.Applications.SingleOrDefault(row => row.Events.Any(ev => ev.Id == eventId));
        var source = application?.Events.Single(row => row.Id == eventId);
        if (vacancy is null || application is null || source is null || !Editable(vacancy, source)
            || await EmployerMessageSources.DeliveryPendingAsync(context, vacancy.Id, ct)) return null;
        application.Vacancy = vacancy;
        var documents = await context.Documents.AsNoTracking().Where(row => row.Key == ProfileProvider.Key || row.Key == FactsProvider.Key).ToListAsync(ct);
        var profile = documents.SingleOrDefault(row => row.Key == ProfileProvider.Key);
        var facts = documents.SingleOrDefault(row => row.Key == FactsProvider.Key);
        var stories = await StoriesAsync(context, ct);
        var interviews = await context.Interviews.AsNoTracking().Where(row => row.ApplicationId == application.Id && row.Status == InterviewStatus.Scheduled)
            .OrderBy(row => row.Id).ToListAsync(ct);
        var snapshot = new ReplyDraftSnapshot
        {
            EventId = source.Id, ApplicationId = application.Id, VacancyId = vacancy.Id,
            SourceHash = SourceHash(source), ContextHash = ContextHash(vacancy), ProfileVersion = profile?.Version ?? 0,
            FactsVersion = facts?.Version ?? 0, StoriesHash = Fingerprint.Of(stories),
        };
        await transaction.CommitAsync(ct);
        return new CapturedReplyDraft(snapshot, source, application,
            profile is null ? new CandidateProfile() : ProfileProvider.FromJson(profile.Content),
            (facts is null ? new CandidateFacts() : JsonSerializer.Deserialize(facts.Content, FactsJsonContext.Default.CandidateFacts)
                ?? throw new JsonException("пустые факты")).WithStandardQuestions(), stories)
        { Interviews = interviews.Select(row => InterviewEditSnapshot.Capture(row).Source.Hash()).ToArray() };
    }

    public async Task<ActionResult> PublishAsync(ReplyDraftSnapshot snapshot, ReplyAnalysis analysis, IReadOnlyList<string> problems, CancellationToken ct)
    {
        await using var context = await db.CreateDbContextAsync(ct);
        await using var transaction = await context.Database.BeginTransactionAsync(ct);
        var result = await PublishJoinedAsync(context, snapshot, analysis, problems, ct);
        if (result.Ok) await notifier.SaveAndFlushAsync(context, ct);
        return result;
    }

    internal async Task<ActionResult> PublishJoinedAsync(JobFinderDbContext context, ReplyDraftSnapshot snapshot, ReplyAnalysis analysis,
        IReadOnlyList<string> problems, CancellationToken ct)
    {
        if (context.Database.CurrentTransaction is null) throw new InvalidOperationException("reply publication requires the source transaction");
        var draft = HumanText.Clean(analysis.Draft?.Trim() ?? "");
        if (string.IsNullOrWhiteSpace(draft)) return ActionResult.Fail("пустой черновик ответа");
        await InterviewPreparationCapture.LockCandidateAsync(context, ct);
        if (await DeliveryLedger.LockVacancyAsync(context, snapshot.VacancyId, ct) is null) return Conflict();
        await context.ApplicationEvents.FromSqlInterpolated($"SELECT * FROM application_events WHERE id = {snapshot.EventId} FOR UPDATE")
            .AsNoTracking().SingleOrDefaultAsync(ct);
        var vacancy = await EmployerMessageSources.VacancyAsync(context, snapshot.VacancyId, ct);
        var application = vacancy?.Applications.SingleOrDefault(row => row.Id == snapshot.ApplicationId);
        var source = application?.Events.SingleOrDefault(row => row.Id == snapshot.EventId);
        if (vacancy is null || application is null || source is null || !Editable(vacancy, source)
            || SourceHash(source) != snapshot.SourceHash || ContextHash(vacancy) != snapshot.ContextHash
            || await EmployerMessageSources.DeliveryPendingAsync(context, snapshot.VacancyId, ct)) return Conflict();
        var documents = await context.Documents.AsNoTracking().Where(row => row.Key == ProfileProvider.Key || row.Key == FactsProvider.Key).ToListAsync(ct);
        var facts = documents.SingleOrDefault(row => row.Key == FactsProvider.Key);
        if ((documents.SingleOrDefault(row => row.Key == ProfileProvider.Key)?.Version ?? 0) != snapshot.ProfileVersion
            || (facts?.Version ?? 0) != snapshot.FactsVersion
            || Fingerprint.Of(await StoriesAsync(context, ct)) != snapshot.StoriesHash) return Conflict();
        var sheet = (facts is null ? new CandidateFacts() : JsonSerializer.Deserialize(facts.Content, FactsJsonContext.Default.CandidateFacts)
            ?? throw new JsonException("пустые факты")).WithStandardQuestions();
        application.Vacancy = vacancy;
        source.Application = null;
        context.Entry(source).State = EntityState.Unchanged;
        source.Draft = draft;
        source.DraftStatus = DraftStatus.Pending;
        source.Revision++;
        var decision = DialoguePolicy.Decide(analysis, sheet, enabled: true, source.Channel, time.GetUtcNow(), time.GetUtcNow(), clock.Now, 0);
        var (card, buttons) = CareerCards.Reply(application, source, analysis, null, decision.Send ? null : decision.Reason, problems, sheet);
        notifier.Add(context, NotificationKind.Info, card, buttons, application.Id);
        return ActionResult.Done("новый черновик пришлю карточкой");
    }

    private static ActionResult Conflict() => ActionResult.Fail("сообщение, диалог или данные кандидата изменились; черновик не перезаписан");

    internal static bool Editable(Vacancy vacancy, ApplicationEvent source) => vacancy.Status != VacancyStatus.Dismissed
        && source.Kind is EventKind.MessageIn or EventKind.FollowUp
        && source.DraftStatus is DraftStatus.None or DraftStatus.Pending or DraftStatus.Dismissed;

    internal static async Task<string> StoriesAsync(JobFinderDbContext context, CancellationToken ct) =>
        ResumeTailor.StoriesText(await context.Stories.AsNoTracking().Where(row => row.Confirmed).OrderBy(row => row.Id).ToListAsync(ct));

    internal static string SourceHash(ApplicationEvent source) => Fingerprint.Of(SourceJson(source));

    internal static string SourceJson(ApplicationEvent source) => JsonSerializer.Serialize(new
    {
        source.Id, source.ApplicationId, source.Revision, source.Kind, source.Channel, source.Author, source.Text, source.ExternalId,
        source.At, source.Stage, source.Classification, source.Draft, source.DraftStatus,
    });

    internal static string ContextHash(Vacancy vacancy) => Fingerprint.Of(JsonSerializer.Serialize(new
    {
        Context = EmployerMessageSources.Context(vacancy),
        VacancyText = JobFinderLlm.VacancyText(vacancy),
        Applications = vacancy.Applications.OrderBy(row => row.Id).Select(row => new
        {
            row.Id, row.CreatedAt, row.SentAt, row.LastInboundAt,
            Events = row.Events.OrderBy(ev => ev.Id).Select(ev => new { ev.Id, ev.At, ev.ExternalId, ev.Classification }),
        }),
    }));
}
