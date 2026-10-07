using System.Globalization;
using System.Text;
using System.Text.Json;
using JobFinder.Core.Domain;
using JobFinder.Core.Filtering;
using JobFinder.Core.Text;
using JobFinder.Core.Time;
using JobFinder.Data;
using JobFinder.Integrations.Email;
using JobFinder.Integrations.HeadHunter;
using JobFinder.Llm.Client;
using JobFinder.Llm.Tasks;
using JobFinder.Pipeline.Career;
using JobFinder.Pipeline.Market;
using JobFinder.Pipeline.Notifications;
using JobFinder.Pipeline.Profile;
using JobFinder.Pipeline.Modules.Preparation;
using JobFinder.Pipeline.Settings;
using JobFinder.Pipeline.Workers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Wolverine.EntityFrameworkCore;

namespace JobFinder.Pipeline.Jobs;

public sealed class CareerOptions
{
    public const string Section = "Career";

    /// <summary>Business days without an answer before a follow-up is drafted.</summary>
    public int FollowUpBusinessDays { get; set; } = 5;

    /// <summary>Calendar days after the follow-up before the application counts as "no response".</summary>
    public int NoResponseDaysAfterFollowUp { get; set; } = 7;

    /// <summary>Applications the user sent themselves (no channel to follow up in) close as "no response" after this many days.</summary>
    public int ManualNoResponseDays { get; set; } = 21;

    public int OutreachMinScore { get; set; } = 80;

    public int OutreachPerRun { get; set; } = 3;

    public int ReminderMinutesBefore { get; set; } = 60;

    public int DebriefHoursAfter { get; set; } = 2;

    public int EmailInboxMinutes { get; set; } = 10;

    /// <summary>The first inbox read looks this far back (then it continues from the last UID).</summary>
    public int EmailFirstRunDays { get; set; } = 14;

    public int HhNegotiationsMinutes { get; set; } = 60;

    public DayOfWeek WeeklyReportDay { get; set; } = DayOfWeek.Sunday;

    public string WeeklyReportTime { get; set; } = "18:00";

    /// <summary>Local hours when drafts that ask for the user's attention (follow-ups, outreach) are produced.</summary>
    public int WorkdayStartHour { get; set; } = 9;

    public int WorkdayEndHour { get; set; } = 20;

    public bool InWorkday(DateTimeOffset local) => local.Hour >= WorkdayStartHour && local.Hour < WorkdayEndHour;
}

/// <summary>Admits legacy interviews to native preparation without waiting for paid work.</summary>
public sealed class InterviewWorker(JobFinder.Pipeline.Modules.Preparation.InterviewPreparationRequests requests, JobTriggers triggers,
    TimeProvider time, ILogger<InterviewWorker> log) : QueueWorker(triggers, time, log)
{
    public override string Name => JobNames.Interviews;

    protected override TimeSpan IdleDelay => TimeSpan.FromMinutes(5);

    protected override Task<int> ProcessBatchAsync(CancellationToken ct) => requests.ImportAsync(20, ct);
}

/// <summary>An hour before an interview: the cheat sheet and the PDF again. A couple of hours after: "how did it go?" for the debrief.</summary>
public sealed class InterviewReminderJob(CareerOptions options, LocalClock clock, JobTriggers triggers, IDbContextFactory<JobFinderDbContext> db,
    Notifier notifier, TimeProvider time, ILogger<InterviewReminderJob> log, InterviewSources sources) : ScheduledJob(db, triggers, notifier, time, log)
{
    private readonly IDbContextFactory<JobFinderDbContext> _db = db;

    public override string Name => JobNames.InterviewReminders;

    protected override bool Enabled => true;

    protected override DateTimeOffset NextRunAfter(DateTimeOffset finishedAt) => finishedAt.AddMinutes(5);

    protected override async Task<JobOutcome> RunOnceAsync(CancellationToken ct)
    {
        await using var ctx = await _db.CreateDbContextAsync(ct);
        var now = Time.GetUtcNow();
        var horizon = now.AddMinutes(options.ReminderMinutesBefore);
        var soon = await ctx.Interviews.AsNoTracking()
            .Where(i => i.Status == InterviewStatus.Scheduled && !i.Reminded && i.ScheduledAt != null && i.ScheduledAt <= horizon && i.ScheduledAt > now.AddMinutes(-10))
            .OrderBy(i => i.Id).Take(100).ToListAsync(ct);
        var reminded = 0;
        foreach (var i in soon)
        {
            if (await NotifyAsync(InterviewEditSnapshot.Capture(i), now, debrief: false, ct)) reminded++;
        }

        // Candidates by the earliest possible end; the exact end (duration + delay) is checked in memory.
        var ended = await ctx.Interviews.AsNoTracking()
            .Where(i => i.Status == InterviewStatus.Scheduled && !i.DebriefAsked && i.ScheduledAt != null && i.ScheduledAt < now.AddHours(-options.DebriefHoursAfter))
            .OrderBy(i => i.Id).Take(100).ToListAsync(ct);
        var asked = 0;
        foreach (var i in ended.Where(i => i.ScheduledAt!.Value.AddMinutes(i.DurationMinutes).AddHours(options.DebriefHoursAfter) <= now))
        {
            if (await NotifyAsync(InterviewEditSnapshot.Capture(i), now, debrief: true, ct)) asked++;
        }

        // An interview nobody debriefed a few days later is over either way.
        var stale = await ctx.Interviews.AsNoTracking().Where(i => i.Status == InterviewStatus.Scheduled && i.DebriefAsked && i.ScheduledAt < now.AddDays(-3))
            .OrderBy(i => i.Id).Take(100).ToListAsync(ct);
        var closed = 0;
        foreach (var i in stale)
        {
            try
            {
                if ((await sources.SetStatusAsync(InterviewEditSnapshot.Capture(i), InterviewStatus.Done, ct)).Ok) closed++;
            }
            catch (InterviewEditConflictException) { }
        }
        return new JobOutcome(reminded + asked, reminded + asked + closed == 0 ? null : $"{reminded} reminders, {asked} debrief prompts, {closed} closed");
    }

    internal Task<bool> NotifyAsync(InterviewEditSnapshot snapshot, DateTimeOffset now, bool debrief, CancellationToken ct) =>
        InterviewReminderSources.NotifyAsync(_db, Notifier, options, clock, snapshot, now, debrief, ct);

}

/// <summary>
/// Replies by e-mail: an answer to our letter (In-Reply-To / References), a message from an address we wrote to (application or outreach),
/// or an hh notification about a vacancy we applied to. Everything else in the mailbox is not ours and is left alone.
/// </summary>
public sealed class EmailInboxJob(CareerOptions options, EmailSender email, Modules.Hiring.EmailInboxSources sources, HhApplier hhApplier,
    JobTriggers triggers, IDbContextFactory<JobFinderDbContext> db, Notifier notifier, TimeProvider time, ILogger<EmailInboxJob> log)
    : ScheduledJob(db, triggers, notifier, time, log)
{
    public override string Name => JobNames.EmailInbox;

    protected override bool Enabled => email.IsConfigured;

    protected override DateTimeOffset NextRunAfter(DateTimeOffset finishedAt) => finishedAt + Jittered(options.EmailInboxMinutes, 2);

    protected override async Task<JobOutcome> RunOnceAsync(CancellationToken ct)
    {
        var snapshot = await sources.CaptureAsync(email.InboxAccountId, ct);
        var batch = await email.ReadInboxAsync(Modules.Hiring.EmailInboxSources.Position(snapshot), options.EmailFirstRunDays, 100, ct);
        var publication = await sources.PublishAsync(snapshot, batch, email.SelfAddress, await hhApplier.HasSessionAsync(ct), ct);
        if (!publication.Published) return new JobOutcome(0, "mailbox cursor changed; retry on the next pass");
        if (publication.HhRefreshNeeded) Triggers.Trigger(JobNames.HhNegotiations);
        return new JobOutcome(publication.Items, publication.Matched == 0 ? null : $"{publication.Matched} replies");
    }
}

/// <summary>
/// The applicant's "Отклики" page on hh under the saved session. Every response — the ones the system sent and the ones the user made by
/// hand — is kept in <see cref="HhResponse"/>, so a vacancy already answered is never offered again. A collected vacancy with a
/// response gets an application (mode External for the user's own) and leaves the approval queue; status changes move the funnel with
/// a card, unread chats are announced with a link. The first import is quiet: one summary instead of a card per old response.
/// </summary>
public sealed class HhNegotiationsJob(CareerOptions options, HhNegotiations negotiations, HhApplier applier, HhOptions hhOptions, HhChat chats,
    HhClient hh, JobFinder.Integrations.Http.PoliteHttp http, Ingest.VacancyIngestor ingestor, ReplyService replies, Core.Options.PathsOptions paths, Modules.Hiring.HhNegotiationSources sources, LocalClock clock,
    Modules.Hiring.HhChatHistorySources histories, Modules.Hiring.HhResponseBackfillSources backfill, JobTriggers triggers,
    IDbContextFactory<JobFinderDbContext> db, Notifier notifier, TimeProvider time, ILogger<HhNegotiationsJob> log)
    : ScheduledJob(db, triggers, notifier, time, log)
{
    /// <summary>Pages read after the first full import: the page lists the most recently changed responses first.</summary>
    private const int RecentPages = 3;

    private readonly IDbContextFactory<JobFinderDbContext> _db = db;
    private readonly HashSet<string> _unknownStates = [];

    public override string Name => JobNames.HhNegotiations;

    protected override bool Enabled => true;

    protected override TimeSpan FirstDelay => TimeSpan.FromMinutes(3);

    protected override DateTimeOffset NextRunAfter(DateTimeOffset finishedAt) => finishedAt + Jittered(options.HhNegotiationsMinutes, 15);

    protected override async Task<JobOutcome> RunOnceAsync(CancellationToken ct)
    {
        if (!await applier.HasSessionAsync(ct)) return new JobOutcome(0, "нет сессии hh (--login hh)");
        if (clock.Now.Hour is < 7 or >= 23) return new JobOutcome(0, "ночь");
        await using var ctx = await _db.CreateDbContextAsync(ct);
        var firstImport = !await ctx.HhResponses.AnyAsync(ct);
        var observedAt = Time.GetUtcNow();
        var list = await negotiations.ReadAsync(Path.GetFullPath(Path.Combine(paths.Data, "debug")), ct, firstImport ? 20 : RecentPages);
        var publication = await sources.PublishAsync(list, observedAt, ct);
        foreach (var state in publication.UnknownStates)
            if (_unknownStates.Add(state)) log.Warn($"[{Name}] unknown hh state '{state}': mapping needed");
        var messages = 0;
        foreach (var chat in publication.Chats) messages += await ReadChatAsync(chat.ApplicationId, chat.ChatId, chat.VacancyId, ct);
        var fetched = await ImportMissingVacanciesAsync(ct);
        var histories = await ReadChatHistoriesAsync(ct);
        return new JobOutcome(list.Count, $"{publication.Imported} imported, {publication.Dequeued} left the queue, {publication.Changed} stage changes, {messages} chat messages, " +
                                          $"{fetched} response vacancies fetched, {histories} chat histories");
    }

    /// <summary>Chats read per run for the history of responses: a browser page each.</summary>
    private const int HistoriesPerRun = 5;

    private const string HistoryMarker = RejectionReviewService.ChatHistoryMarker;

    /// <summary>
    /// The whole hh chat of a response, once: the employer's messages (an invitation, the rejection text, questions) and ours (the letter)
    /// become the application's events, so the vacancy page and the rejection review show what actually happened. History only: nothing
    /// goes through the reply state machine. Rejections and interviews first. hh shows only the time of day, so the events keep the order
    /// within the response's dates, not the exact minute. Messages already stored (live chat reading, e-mail, our replies) are skipped.
    /// </summary>
    private async Task<int> ReadChatHistoriesAsync(CancellationToken ct)
    {
        if (await http.PausedUntilAsync(hhOptions.ApplyHost, ct) is not null) return 0;
        await using var context = await _db.CreateDbContextAsync(ct);
        var done = context.ApplicationEvents.Where(row => row.ExternalId != null && row.ExternalId.StartsWith(HistoryMarker)).Select(row => row.ApplicationId);
        var outcomes = new[] { ApplicationStage.Rejected, ApplicationStage.HrScreen, ApplicationStage.TechInterview, ApplicationStage.Final, ApplicationStage.Offer, ApplicationStage.Accepted };
        var candidates = await (
            from application in context.Applications.AsNoTracking()
            join source in context.VacancySources on application.VacancyId equals source.VacancyId
            join response in context.HhResponses on source.ExternalId equals response.HhVacancyId.ToString()
            where source.Source == SourceKind.HeadHunter && application.Channel == ApplicationChannel.HeadHunter
                && application.Status == ApplicationStatus.Sent && response.ChatId != null && !done.Contains(application.Id)
            orderby application.Stage != null && outcomes.Contains(application.Stage.Value) descending, response.UpdatedAt descending
            select new { application.Id, ChatId = response.ChatId!.Value, response.HhVacancyId }).Take(HistoriesPerRun).ToListAsync(ct);
        var read = 0;
        foreach (var candidate in candidates)
        {
            var snapshot = await histories.CaptureAsync(candidate.Id, candidate.ChatId, candidate.HhVacancyId, ct);
            if (snapshot is null) continue;
            List<HhChatMessage> messages;
            try { messages = await chats.ReadAsync(candidate.ChatId, candidate.HhVacancyId, ct); }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                log.Warn($"[{Name}] chat history {candidate.ChatId}: {error.Message}");
                if (error.GetType().Name.Contains("Timeout", StringComparison.Ordinal)) break;
                await histories.RecordFailureAsync(snapshot, error.Message, ct);
                continue;
            }
            if (await histories.PublishAsync(snapshot, messages, ct)) read++;
        }
        return read;
    }

    /// <summary>
    /// Vacancy pages fetched per (hourly) run for responses the searches never collected: slow on purpose, the search job already reads
    /// the same site, and together they once drew a 403.
    /// </summary>
    private const int MissingPerRun = 3;

    /// <summary>
    /// Responses whose vacancy the searches never collected (closed, older than the window, another region, another title): the vacancy
    /// page by id, ingested and scored like any other, even closed; the vacancy worker joins it to the response. Every response then has
    /// its vacancy for the rejection review. A page hh no longer shows is not asked again by this process.
    /// </summary>
    private async Task<int> ImportMissingVacanciesAsync(CancellationToken ct)
    {
        var missing = await backfill.MissingAsync(MissingPerRun, ct);

        var fetched = 0;
        foreach (var snapshot in missing)
        {
            var id = snapshot.HhVacancyId;
            try
            {
                // Background: only the site budget's leftover, never at the expense of new vacancies.
                var detail = await hh.GetVacancyAsync(hhOptions.ApplyHost, id, ct, JobFinder.Integrations.Http.ScrapePriority.Low);
                await ingestor.IngestHhAsync(detail, null, null, ct, scoreClosed: true);
                fetched++;
            }
            catch (HttpRequestException e) when (e.StatusCode is System.Net.HttpStatusCode.NotFound or System.Net.HttpStatusCode.Gone)
            {
                await backfill.MarkUnavailableAsync(snapshot, gone: true, ct);
                log.Info($"[{Name}] response vacancy {id}: gone from hh");
            }
            catch (FormatException e)
            {
                await backfill.MarkUnavailableAsync(snapshot, gone: false, ct);
                log.Warn($"[{Name}] response vacancy {id}: {e.Message}");
            }
            catch (JobFinder.Integrations.Http.SourceBlockedException e)
            {
                // The anonymous scraper is paused or out of budget; the browser steps (responses, chats) are not, so the run goes on.
                if (e.Fresh) log.Warn($"[{Name}] response vacancies: {e.Message}");
                await AlertBlockedAsync(e);
                break;
            }
        }

        if (fetched > 0) Triggers.Trigger(JobNames.Vacancies);
        return fetched;
    }

    /// <summary>
    /// The employer's messages after our last one: what still waits for an answer. Seen ones (by chat message id, or the same text that
    /// already came by e-mail) are skipped.
    /// </summary>
    private async Task<int> ReadChatAsync(long applicationId, long chatId, long vacancyId, CancellationToken ct)
    {
        List<HhChatMessage> chat;
        try
        {
            chat = await chats.ReadAsync(chatId, vacancyId, ct);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            log.Warn($"[{Name}] chat {chatId}: {e.Message}");
            return 0;
        }

        var lastMine = chat.FindLastIndex(m => m.Mine);
        var fresh = chat.Skip(lastMine + 1).Where(m => !m.Mine && m.Text.Length > 0).ToList();
        if (fresh.Count == 0) return 0;
        await using var ctx = await _db.CreateDbContextAsync(ct);
        var known = await ctx.ApplicationEvents.Where(e => e.ApplicationId == applicationId && e.Kind == EventKind.MessageIn)
            .Select(e => new { e.ExternalId, e.Text }).ToListAsync(ct);
        var handled = 0;
        foreach (var m in fresh)
        {
            var id = $"hhchat:{m.Id}";
            var head = TextUtil.Normalize(m.Text)[..Math.Min(60, TextUtil.Normalize(m.Text).Length)];
            if (known.Any(k => k.ExternalId == id || TextUtil.Normalize(k.Text).Contains(head, StringComparison.Ordinal))) continue;
            await replies.HandleInboundAsync(applicationId, EventChannel.HeadHunter, m.Author ?? "работодатель", m.Text, id, Time.GetUtcNow(), ct);
            handled++;
        }

        return handled;
    }

    /// <summary>The funnel stage a response is at: hh state, and "viewed" when the employer opened a response still waiting.</summary>
    public static ApplicationStage? StageOf(HhNegotiation n) => HhHistory.StageOf(n.State, n.ViewedByEmployer);

}

/// <summary>
/// No answer after five business days: a short follow-up is drafted for the same channel (sent by the button; for hh the text is copied to
/// the chat). No answer a week after that: the application closes as "no response".
/// </summary>
public sealed class FollowUpJob(CareerOptions options, LetterWriter writer, ProfileProvider profiles, JobFinder.Pipeline.Modules.Hiring.FollowUpSources sources, LocalClock clock,
    JobTriggers triggers, IDbContextFactory<JobFinderDbContext> db, Notifier notifier, TimeProvider time, ILogger<FollowUpJob> log)
    : ScheduledJob(db, triggers, notifier, time, log)
{
    private const int MaxDraftsPerRun = 5;
    private readonly IDbContextFactory<JobFinderDbContext> _db = db;

    public override string Name => JobNames.FollowUps;

    protected override bool Enabled => true;

    protected override DateTimeOffset NextRunAfter(DateTimeOffset finishedAt) => finishedAt + Jittered(60, 10);

    protected override async Task<JobOutcome> RunOnceAsync(CancellationToken ct)
    {
        var local = clock.Now;
        var profile = await profiles.GetAsync(ct);
        if (profile is null) return new JobOutcome(0, "нет профиля");
        await using var ctx = await _db.CreateDbContextAsync(ct);
        var now = Time.GetUtcNow();
        var open = await ctx.Applications.AsNoTracking().Include(a => a.Vacancy).ThenInclude(v => v!.Company)
            .Include(a => a.Vacancy).ThenInclude(v => v!.Sources)
            .Where(a => a.Status == ApplicationStatus.Sent && a.SentAt != null && a.LastInboundAt == null
                        && (a.Stage == ApplicationStage.Sent || a.Stage == ApplicationStage.Viewed))
            .OrderBy(a => a.SentAt).ToListAsync(ct);

        var drafted = 0;
        var closed = 0;
        var workday = local.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday) && options.InWorkday(local);
        foreach (var app in open)
        {
            var snapshot = JobFinder.Pipeline.Modules.Hiring.FollowUpSnapshot.Capture(app);
            if (app.FollowUpSentAt is { } proposed)
            {
                if (now - proposed < TimeSpan.FromDays(options.NoResponseDaysAfterFollowUp)) continue;
                if (await sources.CloseAsync(snapshot, "нет ответа после напоминания", ct)) closed++;
                continue;
            }

            if (app.Channel == ApplicationChannel.Manual)
            {
                if (now - app.SentAt!.Value < TimeSpan.FromDays(options.ManualNoResponseDays)) continue;
                if (await sources.CloseAsync(snapshot, $"нет ответа {options.ManualNoResponseDays} дн.", ct)) closed++;
                continue;
            }

            var days = BusinessDays(TimeZoneInfo.ConvertTime(app.SentAt!.Value, clock.Zone).Date, local.Date);
            if (days < options.FollowUpBusinessDays || !workday || drafted >= MaxDraftsPerRun) continue;
            // The same writer and checks as the response itself: a reminder in the candidate's name is held to the response's standard.
            LetterDraft? draft;
            try
            {
                draft = await writer.WriteAsync(new LetterBrief(app.Vacancy!, LetterOf(app.Channel), app.Target ?? "рекрутер, имя неизвестно",
                    $"Кандидат откликнулся {days} рабочих дней назад, ответа нет: это напоминание, а не новое знакомство.", AttachesResume: false,
                    $"Кандидат (отклик):\n{app.CoverLetter}", First: false, NeedsFact: false, Deep: false), ct);
                if (draft is null) break;
            }
            catch (LlmUnavailableException e)
            {
                log.Warn($"[{Name}] application {app.Id}: {e.Message}");
                break;
            }

            if (await sources.DraftAsync(snapshot, draft, days, ct)) drafted++;
        }

        return new JobOutcome(drafted + closed, drafted + closed == 0 ? null : $"{drafted} follow-ups drafted, {closed} closed as no response");
    }

    private static LetterChannel LetterOf(ApplicationChannel c) => c switch
    {
        ApplicationChannel.TelegramDm => LetterChannel.Telegram,
        ApplicationChannel.Email => LetterChannel.Email,
        _ => LetterChannel.Hh,
    };

    public static EventChannel ChannelOf(ApplicationChannel c) => c switch
    {
        ApplicationChannel.TelegramDm => EventChannel.Telegram,
        ApplicationChannel.Email => EventChannel.Email,
        ApplicationChannel.HeadHunter => EventChannel.HeadHunter,
        _ => EventChannel.Manual,
    };

    /// <summary>Weekdays after <paramref name="from"/> up to and including <paramref name="to"/>.</summary>
    public static int BusinessDays(DateTime from, DateTime to)
    {
        var days = 0;
        for (var d = from.Date.AddDays(1); d <= to.Date; d = d.AddDays(1))
        {
            if (d.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday)) days++;
        }

        return days;
    }
}

/// <summary>For strong applications (score ≥ the outreach threshold) a few days old: who to write at the company, and what.</summary>
public sealed class OutreachJob(CareerOptions options, OutreachService outreach, Modules.Hiring.OutreachScans scans, RuntimeSettings settings, LocalClock clock, JobTriggers triggers,
    IDbContextFactory<JobFinderDbContext> db, Notifier notifier, TimeProvider time, ILogger<OutreachJob> log)
    : ScheduledJob(db, triggers, notifier, time, log)
{
    public override string Name => JobNames.Outreach;

    protected override bool Enabled => true;

    protected override DateTimeOffset NextRunAfter(DateTimeOffset finishedAt) => finishedAt + Jittered(30, 10);

    protected override async Task<JobOutcome> RunOnceAsync(CancellationToken ct)
    {
        if (!options.InWorkday(clock.Now)) return new JobOutcome(0, "вне рабочего времени");
        var threshold = Math.Max(options.OutreachMinScore, (await settings.GetDecisionAsync(ct)).AutoApplyThreshold);
        var batch = await scans.ReserveAsync(threshold, options.OutreachPerRun, ct);
        var prepared = 0;
        foreach (var ticket in batch.Tickets)
        {
            var ready = await outreach.PrepareAsync(ticket.VacancyId, ct) is not null;
            if (ready) prepared++;
            await scans.FinishAsync(ticket, threshold, ready, ct);
        }
        return new JobOutcome(prepared, batch.Tickets.Count == 0 && batch.Damaged == 0 ? null
            : $"{prepared}/{batch.Tickets.Count} with a contact; {batch.Damaged} damaged scan states retained");
    }
}

/// <summary>
/// Sunday evening: the week in numbers (funnel, conversions by channel), the market (net USD percentiles by segment), what the market asks
/// for versus the resume, and concrete edits for the main hh resume.
/// </summary>
public sealed class WeeklyReportJob(CareerOptions options, LocalClock clock, WeeklyReportSources sources, WeeklyReportRequests requests,
    ProfileProvider profiles, JobTriggers triggers, IDbContextFactory<JobFinderDbContext> db, Notifier notifier, TimeProvider time,
    ILogger<WeeklyReportJob> log) : ScheduledJob(db, triggers, notifier, time, log)
{
    public const string LatestReportKey = "report.weekly";
    public const string LatestAdviceKey = "report.hhAdvice";
    private readonly IDbContextFactory<JobFinderDbContext> _db = db;

    public override string Name => JobNames.WeeklyReport;

    protected override bool Enabled => true;

    protected override TimeSpan FirstDelay => NextRunAfter(Time.GetUtcNow()) - Time.GetUtcNow();

    protected override DateTimeOffset NextRunAfter(DateTimeOffset finishedAt)
    {
        var at = TimeOnly.TryParse(options.WeeklyReportTime, CultureInfo.InvariantCulture, out var t) ? t : new TimeOnly(18, 0);
        var next = clock.NextOccurrenceUtc(at);
        while (TimeZoneInfo.ConvertTime(next, clock.Zone).DayOfWeek != options.WeeklyReportDay) next = next.AddDays(1);
        return next;
    }

    /// <summary>A missed Sunday report is sent on start the same evening or night, not a week later.</summary>
    protected override DateTimeOffset? LastSlot(DateTimeOffset now) => NextRunAfter(now).AddDays(-7);

    protected override Task<DateTimeOffset?> LastSuccessfulStartAsync(CancellationToken ct) => sources.LastSlotAsync(ct);

    protected override Task<JobOutcome> RunOnceAsync(CancellationToken ct) => throw new InvalidOperationException("weekly report requires its captured job invocation");

    protected override async Task<JobOutcome> RunForInvocationAsync(JobInvocation invocation, CancellationToken ct)
    {
        await sources.RequestAsync(ct, invocation);
        await using var context = await _db.CreateDbContextAsync(ct);
        var run = await context.JobRuns.AsNoTracking().SingleAsync(row => row.Id == invocation.RunId, ct);
        return new(run.Items, run.Message);
    }

    public Task<Guid> RequestAsync(CancellationToken ct, bool force = false) => sources.RequestAsync(ct, force: force);

    public async Task<WeeklyReportResult> BuildAsync(CancellationToken ct) => await requests.WaitAsync(await RequestAsync(ct), ct);

    public Task<(string? Html, HhResumeAdvice? Advice)> LatestAsync(CancellationToken ct) => sources.LatestAsync(ct);

    public async Task<(string Text, List<string> Top, List<string> Gaps)> DemandAsync(JobFinderDbContext context, CancellationToken ct)
    {
        var coverage = (profiles.ResumeText() ?? "") + " " + string.Join(" ", (await profiles.GetAsync(ct))?.Skills.Select(skill => skill.Name) ?? []);
        return await WeeklyReportCapture.DemandAsync(context, Time.GetUtcNow(), coverage, ct);
    }

    public static string ChannelLabel(ApplicationChannel c) => c switch
    {
        ApplicationChannel.HeadHunter => "hh",
        ApplicationChannel.TelegramDm => "Telegram",
        ApplicationChannel.Email => "почта",
        _ => "вручную",
    };
}

/// <summary>Counts over sent applications: how many got any answer, reached an interview, got an offer, were rejected.</summary>
public readonly record struct FunnelStats(int Sent, int Replied, int Interviews, int Offers, int Rejected)
{
    public static FunnelStats Of(IEnumerable<Application> apps)
    {
        int sent = 0, replied = 0, interviews = 0, offers = 0, rejected = 0;
        foreach (var a in apps)
        {
            sent++;
            var s = a.Stage ?? ApplicationStage.Sent;
            if (a.LastInboundAt is not null || s is not (ApplicationStage.Sent or ApplicationStage.Viewed or ApplicationStage.NoResponse or ApplicationStage.Withdrawn)) replied++;
            if (s is ApplicationStage.HrScreen or ApplicationStage.TechInterview or ApplicationStage.Final or ApplicationStage.Offer or ApplicationStage.Accepted) interviews++;
            if (s is ApplicationStage.Offer or ApplicationStage.Accepted) offers++;
            if (s == ApplicationStage.Rejected) rejected++;
        }

        return new FunnelStats(sent, replied, interviews, offers, rejected);
    }
}
