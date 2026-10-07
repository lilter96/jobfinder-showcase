using System.Globalization;
using System.Text;
using JobFinder.Core.Domain;
using JobFinder.Core.Filtering;
using JobFinder.Core.Profile;
using JobFinder.Core.Text;
using JobFinder.Data;
using JobFinder.Integrations.Telegram;
using JobFinder.Llm.Tasks;
using JobFinder.Pipeline.Actions;
using JobFinder.Pipeline.Documents;
using JobFinder.Pipeline.Profile;
using JobFinder.Pipeline.Modules.Hiring;
using Microsoft.EntityFrameworkCore;

namespace JobFinder.Pipeline.Career;

/// <summary>Where the conversation with the company stands: what the next message to its recruiter should do.</summary>
public enum RecruiterSituation
{
    /// <summary>No response yet: the first message is the response (a Telegram application in the funnel).</summary>
    NotApplied,

    /// <summary>Responded (hh, e-mail…), no answer yet: a direct message so the response is noticed.</summary>
    Waiting,

    /// <summary>The response went unanswered: a reminder.</summary>
    Silent,

    /// <summary>We wrote to this person, no answer yet: a short nudge, not a second pitch.</summary>
    Nudge,

    /// <summary>They answered, the conversation goes on.</summary>
    Conversation,

    /// <summary>An interview stage.</summary>
    Interview,

    /// <summary>Rejected: feedback and other roles, without arguing.</summary>
    Rejected,

    Closed,
}

/// <summary>A fact the draft rests on and the profile fragment quoted for it; <paramref name="Found"/> = the quote is really in the profile.</summary>
public sealed record RecruiterEvidence(string Fact, string Quote, bool Found);

/// <param name="Fresh">Written from scratch: the earlier response is neither mentioned nor known to the model.</param>
/// <param name="Evidence">What in the profile the text rests on, for the user to see.</param>
/// <param name="Problems">What the checks still found in the best of the attempts (empty = passed): shown, never hidden.</param>
public sealed record RecruiterDraft(string Text, bool AttachResume, RecruiterSituation Situation, bool Fresh,
    IReadOnlyList<RecruiterEvidence> Evidence, IReadOnlyList<string> Problems);

/// <summary>
/// The Telegram conversation with a company's recruiter, tied to the vacancy's response. A draft written for the situation; sending only
/// by the user's button, with the resume tailored to the vacancy when asked (with no response yet the message is the response: a
/// TelegramDm application enters the funnel); and the chat itself, both sides, read from the account into the response's events,
/// messages the user typed on the phone included, so the vacancy page, reply drafts and the rejection review see the whole conversation.
/// </summary>
public sealed class RecruiterChatService(IDbContextFactory<JobFinderDbContext> db, TelegramUserClient telegram, LetterWriter writer, ProfileProvider profiles,
    EmployerMessages messages, TimeProvider time, TelegramChatHistorySources historySources, ILogger<RecruiterChatService> log)
{
    private static readonly CultureInfo Ru = CultureInfo.GetCultureInfo("ru-RU");

    /// <summary>
    /// From the response and its events: a conversation is when they wrote something; our own Telegram message without an answer is a
    /// nudge situation, not a conversation.
    /// </summary>
    public static RecruiterSituation Situation(Application? app) => app is not { Status: ApplicationStatus.Sent }
        ? RecruiterSituation.NotApplied
        : app.Stage switch
        {
            ApplicationStage.Rejected or ApplicationStage.Withdrawn => RecruiterSituation.Rejected,
            ApplicationStage.Accepted => RecruiterSituation.Closed,
            ApplicationStage.HrScreen or ApplicationStage.TechInterview or ApplicationStage.Final or ApplicationStage.Offer => RecruiterSituation.Interview,
            ApplicationStage.NoResponse => RecruiterSituation.Silent,
            ApplicationStage.Replied => RecruiterSituation.Conversation,
            _ when app.Events.Any(e => e.Kind == EventKind.MessageIn) => RecruiterSituation.Conversation,
            _ when app.Events.Any(e => e is { Kind: EventKind.MessageOut, Channel: EventChannel.Telegram }) => RecruiterSituation.Nudge,
            _ => RecruiterSituation.Waiting,
        };

    /// <summary>A nudge sooner than this after our last message reads as pressure.</summary>
    public static readonly TimeSpan NudgeAfter = TimeSpan.FromDays(2);

    public static string Label(RecruiterSituation s) => s switch
    {
        RecruiterSituation.NotApplied => "отклика ещё нет: сообщение станет откликом через Telegram и попадёт в воронку",
        RecruiterSituation.Waiting => "отклик отправлен, ответа нет: написать напрямую, чтобы его заметили",
        RecruiterSituation.Silent => "отклик остался без ответа: напомнить о себе",
        RecruiterSituation.Nudge => "вы уже написали, ответа пока нет: короткое напоминание, без второго питча",
        RecruiterSituation.Conversation => "переписка идёт: следующее сообщение по смыслу",
        RecruiterSituation.Interview => "идёт процесс интервью: сообщение по делу",
        RecruiterSituation.Rejected => "отказ: коротко спросить, чего не хватило, и нет ли других ролей",
        _ => "процесс завершён",
    };

    /// <summary>The resume goes with a first word to the person (a response, a nudge), not into a conversation, an interview or after a rejection.</summary>
    public static bool AttachByDefault(RecruiterSituation s) => s is RecruiterSituation.NotApplied or RecruiterSituation.Waiting or RecruiterSituation.Silent;

    /// <summary>
    /// After a rejection the message starts from scratch by default: a word about the rejected response (asking for feedback, "I applied
    /// on hh") reads as a request to reverse a decision, and gets the same answer.
    /// </summary>
    public static bool FreshByDefault(RecruiterSituation s) => s == RecruiterSituation.Rejected;

    public const string FreshLabel = "с чистого листа: как новый отклик, прошлый отклик и отказ не упоминаются";

    /// <summary>A response that ended (rejected, silent, withdrawn): a new message to the company is a new attempt, not part of it.</summary>
    public static bool IsClosed(Application? a) =>
        a is { Status: ApplicationStatus.Sent, Stage: ApplicationStage.Rejected or ApplicationStage.NoResponse or ApplicationStage.Withdrawn };

    /// <summary>The response that defines what can be done now: the newest one that is not failed or cancelled.</summary>
    public static Application? Live(Vacancy v) =>
        v.Applications.Where(a => a.Status is not (ApplicationStatus.Failed or ApplicationStatus.Cancelled)).OrderByDescending(a => a.CreatedAt).FirstOrDefault();

    /// <summary>The id a Telegram private message has in the events: the same one live messages get (<see cref="ReplyService.HandleTelegramAsync"/>).</summary>
    public static string ExternalId(long userId, int messageId) => $"{userId.ToString(CultureInfo.InvariantCulture)}/{messageId.ToString(CultureInfo.InvariantCulture)}";

    public static bool IsTelegramMessage(ApplicationEvent e) => e.Channel == EventChannel.Telegram && e.Kind is EventKind.MessageIn or EventKind.MessageOut;

    /// <param name="fresh">
    /// Write as a first message of a new candidate: the model gets neither the earlier response nor its conversation, so nothing of it can
    /// leak into the text. null = <see cref="FreshByDefault"/>. Without an earlier response every message is fresh anyway.
    /// </param>
    public async Task<(RecruiterDraft? Draft, string? Error)> DraftAsync(long vacancyId, long contactId, bool? fresh, CancellationToken ct)
    {
        if (await profiles.GetAsync(ct) is null) return (null, "нет профиля (--import-resume)");
        await using var ctx = await db.CreateDbContextAsync(ct);
        var v = await ctx.Vacancies.AsNoTracking().Include(x => x.Sources).Include(x => x.Company).Include(x => x.Applications).ThenInclude(a => a.Events)
            .FirstOrDefaultAsync(x => x.Id == vacancyId, ct);
        var contact = await ctx.CompanyContacts.AsNoTracking().FirstOrDefaultAsync(c => c.Id == contactId, ct);
        if (v is null || contact is null) return (null, "вакансия или контакт не найдены");
        return await DraftAsync(v, contact, fresh, ct);
    }

    internal async Task<(RecruiterDraft? Draft, string? Error)> DraftAsync(Vacancy v, CompanyContact contact, bool? fresh, CancellationToken ct)
    {
        if (contact.RetiredAt is not null) return (null, "контакт исключён из новых откликов");
        var app = Live(v);
        var situation = Situation(app);
        var clean = situation != RecruiterSituation.NotApplied && (fresh ?? FreshByDefault(situation));
        var attach = clean || AttachByDefault(situation);
        var who = (contact.PersonName is { Length: > 0 } person ? $"{person} ({contact.Value})" : contact.Value) + (contact.Role is { } role ? $", {role}" : "");
        var situationText = clean ? FreshText(v) : SituationText(situation, app, v, time.GetUtcNow());
        var conversation = clean ? "(переписки нет: кандидат пишет этому человеку впервые)" : ConversationText(app);
        // A first word to this person (a response, a nudge, a fresh start) or a turn in a going conversation: different limits.
        var first = clean || situation is RecruiterSituation.NotApplied or RecruiterSituation.Waiting or RecruiterSituation.Silent or RecruiterSituation.Rejected;
        var needsFact = first && situation != RecruiterSituation.Rejected;
        var draft = await writer.WriteAsync(new LetterBrief(v, LetterChannel.Telegram, who, situationText, attach, conversation, first, needsFact, Deep: true), ct);
        return draft is null
            ? (null, "нет профиля (profile/profile.json)")
            : (new RecruiterDraft(draft.Text, attach, situation, clean, draft.Evidence, draft.Problems), null);
    }

    /// <summary>
    /// The user's message to the recruiter. No response yet: it is the response (TelegramDm application, funnel, vacancy applied). A
    /// response exists: the message joins its conversation, and a sent outreach record makes the recruiter's replies come to it.
    /// </summary>
    /// <param name="attachResume">null = as the situation suggests (<see cref="AttachByDefault"/>): a bot card sends without a choice.</param>
    /// <param name="outreachId">The draft card this message came from (the bot), marked sent.</param>
    public Task<ActionResult> SendAsync(long vacancyId, long contactId, string text, bool? attachResume, CancellationToken ct,
        long? outreachId = null, bool? fresh = null) => messages.RecruiterAsync(vacancyId, contactId, text, attachResume, fresh, outreachId, ct);

    /// <summary>The chats with every Telegram contact of the vacancy's company, into the vacancy's response (one is created for a conversation the user started from the phone).</summary>
    public async Task<ActionResult> SyncVacancyAsync(long vacancyId, CancellationToken ct)
    {
        if (!telegram.IsReady) return ActionResult.Fail("Telegram не подключён");
        await using var ctx = await db.CreateDbContextAsync(ct);
        var company = await ctx.Vacancies.Where(v => v.Id == vacancyId).Select(v => v.CompanyName).FirstOrDefaultAsync(ct);
        var key = VacancyFingerprint.NormalizeCompany(company);
        var handles = await ctx.CompanyContacts.Where(c => c.CompanyKey == key && c.Kind == ContactKind.Telegram).Select(c => c.Value).ToListAsync(ct);
        if (handles.Count == 0) return ActionResult.Fail("у компании нет Telegram-контактов");
        var added = 0;
        foreach (var handle in handles) added += await SyncThreadAsync(vacancyId, handle, null, createApplication: true, ct);
        return ActionResult.Done(added == 0 ? "новых сообщений нет" : $"переписка обновлена: новых сообщений {added}");
    }

    /// <summary>
    /// Every so often: the chats with the Telegram contacts of companies with a recent response, so what the user wrote from the phone and
    /// what came while the app was down reach the response.
    /// </summary>
    public async Task<int> SyncActiveAsync(CancellationToken ct)
    {
        if (!telegram.IsReady) return 0;
        await using var ctx = await db.CreateDbContextAsync(ct);
        var since = time.GetUtcNow().AddDays(-45);
        var contacts = await ctx.CompanyContacts.AsNoTracking().Where(c => c.Kind == ContactKind.Telegram).ToListAsync(ct);
        if (contacts.Count == 0) return 0;
        var apps = await ctx.Applications.AsNoTracking().Include(a => a.Vacancy)
            .Where(a => a.Status == ApplicationStatus.Sent && (a.SentAt >= since || a.LastInboundAt >= since || a.LastOutboundAt >= since)
                        && a.Stage != ApplicationStage.Accepted && a.Stage != ApplicationStage.Withdrawn)
            .ToListAsync(ct);
        var latest = apps.GroupBy(a => VacancyFingerprint.NormalizeCompany(a.Vacancy?.CompanyName))
            .ToDictionary(g => g.Key, g => g.MaxBy(a => a.SentAt ?? a.CreatedAt)!);
        var added = 0;
        foreach (var c in contacts.Where(c => latest.ContainsKey(c.CompanyKey)))
        {
            try
            {
                added += await SyncThreadAsync(latest[c.CompanyKey].VacancyId, c.Value, null, createApplication: false, ct);
            }
            catch (Exception e) when (!ct.IsCancellationRequested)
            {
                log.Warn($"[recruiter] chat {c.Value}: {e.Message}");
            }
        }

        if (added > 0) log.Info($"[recruiter] chats: {added} new messages");
        return added;
    }

    /// <summary>
    /// The chat with <paramref name="handle"/> into the vacancy's response: only messages not stored anywhere yet (live ones have the same
    /// id), not older than the response (or the vacancy) by more than two weeks. Our own messages sent from here are recognized by their
    /// text and get the id instead of a copy. History only: nothing goes through the reply drafting.
    /// </summary>
    /// <param name="exceptMessageId">The message being handled live right now (it comes through the reply state machine).</param>
    /// <param name="createApplication">A conversation without a response becomes one (the user wrote from the phone).</param>
    public async Task<int> SyncThreadAsync(long vacancyId, string handle, int? exceptMessageId, bool createApplication, CancellationToken ct)
    {
        var snapshot = await historySources.CaptureAsync(vacancyId, handle, exceptMessageId, createApplication, ct);
        if (snapshot is null) return 0;
        var history = await telegram.GetDirectHistoryAsync(snapshot.Handle);
        return history is null || history.Messages.Count == 0 ? 0 : await historySources.PublishAsync(snapshot, history, ct);
    }

    /// <summary>
    /// New events for the fetched messages; our own messages already stored without an id (sent from here) are claimed by their text,
    /// not copied.
    /// </summary>
    public static (List<ApplicationEvent> Added, List<(ApplicationEvent Event, string ExternalId)> Claimed) Merge(IEnumerable<ApplicationEvent> existing,
        long userId, string handle, IEnumerable<TgDirectMessage> messages)
        => TelegramChatHistorySources.Merge(existing, userId, handle, messages);

    /// <summary>A first message of a new candidate: nothing earlier exists for this text.</summary>
    internal static string FreshText(Vacancy v) =>
        $"Это первое сообщение кандидата этому человеку и одновременно отклик (вакансия опубликована {Found(v)}; площадку упоминать не нужно). " +
        "Прошлых откликов, статусов, отказов и переписки для этого сообщения нет: ни упоминаний, ни намёков («снова», «ещё раз», «ранее " +
        "откликался»), никаких дат.";

    private static string Found(Vacancy v) => v.Sources.Select(x => x.Source).FirstOrDefault() switch
    {
        SourceKind.HeadHunter => "на hh.ru / rabota.by",
        SourceKind.Telegram => "в Telegram-канале",
        SourceKind.Habr => "на Хабр Карьере",
        _ => "в объявлении",
    };

    internal static string SituationText(RecruiterSituation s, Application? app, Vacancy v, DateTimeOffset now)
    {
        var sent = app?.SentAt ?? app?.CreatedAt;
        var days = sent is { } at ? (int)Math.Max(0, (now - at).TotalDays) : 0;
        var when = sent is { } d ? d.ToString("d MMMM", Ru) : "";
        var via = app?.Channel switch
        {
            ApplicationChannel.HeadHunter => "на hh",
            ApplicationChannel.Email => "по почте",
            ApplicationChannel.TelegramDm => "в Telegram",
            _ => app?.Mode == ApplicationMode.External ? "на hh" : "",
        };
        var found = Found(v);
        return s switch
        {
            RecruiterSituation.NotApplied =>
                $"Отклика ещё нет: это первое сообщение этому человеку и одновременно отклик (вакансия опубликована {found}; площадку упоминать не нужно).",
            RecruiterSituation.Waiting =>
                $"Кандидат уже откликнулся {via} {days} дн. назад, ответа нет. Цель: чтобы отклик заметили. Отклик {via} можно упомянуть одним коротким " +
                "оборотом внутри фразы, без дат.",
            RecruiterSituation.Silent =>
                $"Отклик {via} ({when}) остался без ответа {days} дн. Цель: мягко напомнить о себе одним фактом под вакансию, без упрёка.",
            RecruiterSituation.Nudge =>
                $"Кандидат уже написал этому человеку ({(app?.LastOutboundAt is { } last ? (int)Math.Max(0, (now - last).TotalDays) : days)} дн. назад, " +
                "см. <conversation>), ответа пока нет. Нужно короткое напоминание в одно-два предложения: без повторного знакомства, без пересказа " +
                "опыта и без давления, например спросить, удалось ли посмотреть. facts можно оставить пустыми.",
            RecruiterSituation.Conversation => "Переписка уже идёт (см. <conversation>): напиши следующее сообщение по смыслу, ответь на последний вопрос, если он есть.",
            RecruiterSituation.Interview => $"Идёт процесс, стадия: {FunnelService.Label(app?.Stage)}. Сообщение по делу, в контексте переписки.",
            RecruiterSituation.Rejected =>
                $"Кандидат откликался {via}, и пришёл отказ. Он пишет рекрутеру коротко: поблагодарить, спросить, чего не хватило, и нет ли других " +
                "ролей, где такой опыт нужнее. Без просьбы пересмотреть решение и без дат.",
            _ => "Процесс по вакансии завершён.",
        };
    }

    internal static string ConversationText(Application? app)
    {
        if (app is null) return "(переписки нет)";
        var sb = new StringBuilder();
        foreach (var e in app.Events.OrderBy(e => e.At).TakeLast(14))
        {
            var what = e.Kind switch
            {
                EventKind.MessageIn => $"{e.Author ?? "работодатель"} ({e.Channel})",
                EventKind.MessageOut => $"кандидат ({e.Channel})",
                EventKind.StageChanged => "стадия: " + FunnelService.Label(e.Stage),
                _ => "заметка",
            };
            sb.Append('[').Append(e.At.ToString("dd.MM HH:mm", CultureInfo.InvariantCulture)).Append("] ").Append(what);
            if (!string.IsNullOrWhiteSpace(e.Text)) sb.Append(": ").Append(TextUtil.Truncate(e.Text, 700));
            sb.Append('\n');
        }

        return sb.Length == 0 ? "(переписки нет)" : sb.ToString();
    }
}
