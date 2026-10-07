using System.Globalization;
using System.Text;
using JobFinder.Core.Domain;
using JobFinder.Core.Filtering;
using JobFinder.Core.Text;
using JobFinder.Data;
using JobFinder.Integrations.Email;
using JobFinder.Integrations.Telegram;
using JobFinder.Pipeline.Actions;
using JobFinder.Pipeline.Profile;
using JobFinder.Pipeline.Modules.Hiring;
using Microsoft.EntityFrameworkCore;

namespace JobFinder.Pipeline.Career;

/// <summary>
/// Replies from employers, whatever the channel: stored as events (dedup by the channel's message id), read by the model (invitation,
/// question, rejection, offer), turned into a stage change and — for an invitation with a time — an interview, and shown to the user with
/// a drafted answer that is sent back through the same channel only when the user presses the button.
/// </summary>
public sealed class ReplyService(IDbContextFactory<JobFinderDbContext> db, EmployerMessages messages, FactsProvider facts,
    RecruiterChatService chats, ILogger<ReplyService> log, ReplyDraftSources draftSources, ReplyProcessingRequests processing)
{
    /// <summary>The author of replies the system sent on its own (the funnel shows them apart from the user's).</summary>
    public const string AutoAuthor = "я (авто)";

    /// <param name="author">Who wrote: "@username" (Telegram), an e-mail address, "hh".</param>
    /// <param name="yourChat">A chat the user leads themselves (their own outgoing messages are not seen here): a draft, never an automatic answer.</param>
    public async Task HandleInboundAsync(long applicationId, EventChannel channel, string author, string text, string? externalId,
        DateTimeOffset at, CancellationToken ct, string? subject = null, bool yourChat = false)
    {
        await processing.IncomingAsync(new InboundReply
        {
            ApplicationId = applicationId, Channel = channel, Author = author, Text = text, ExternalId = externalId,
            At = at, Subject = subject, YourChat = yourChat,
        }, ct);
    }

    /// <summary>
    /// A new draft for a message that still waits for an answer (the «Переписать» button): the same reading with the current profile,
    /// facts and stories, and a new card. Nothing is sent: the draft waits for the user's button even when it is routine.
    /// </summary>
    /// <param name="preview">Only show what the model would answer now: nothing is saved, no card (tuning the prompt).</param>
    public async Task<ActionResult> RedraftAsync(long eventId, CancellationToken ct, bool preview = false, ReplyEditSnapshot? expected = null)
    {
        if (expected is not null && await draftSources.CaptureEditAsync(eventId, expected.Version, ct) != expected)
            return ActionResult.Fail("карточка устарела; черновик не перезаписан");
        await facts.GetAsync(ct);
        var captured = await draftSources.CaptureAsync(eventId, ct);
        if (captured is null || captured.Event.Kind != EventKind.MessageIn
            || expected is not null && !expected.Matches(captured.Event, captured.Application))
            return ActionResult.Fail("сообщение не найдено, изменилось или требует сверки отправки");
        var ev = captured.Event;
        var app = captured.Application;
        ReplyProcessingResult prepared;
        try
        {
            var ticket = await processing.RedraftAsync(captured, preview, ct);
            prepared = await processing.WaitAsync(ticket.RequestId, ct);
        }
        catch (ReplyProcessingSupersededException)
        {
            return ActionResult.Fail("сообщение, диалог или данные кандидата изменились; черновик не перезаписан");
        }
        var analysis = prepared.Analysis;
        var problems = prepared.Problems;
        if (string.IsNullOrWhiteSpace(analysis.Draft)) return ActionResult.Fail("модель считает, что отвечать не нужно: " + analysis.Summary);
        if (preview)
            return ActionResult.Done($"{analysis.Intent}, {analysis.Counterpart}\n{HumanText.Clean(analysis.Draft.Trim())}\n" +
                                     $"расхождения: {string.Join("; ", analysis.Conflicts)}\nсверить: {string.Join("; ", analysis.Verify)}\n" +
                                     $"нет фактов: {string.Join(", ", analysis.MissingFacts)}\nпроверка: {string.Join("; ", problems)}");


        log.Info($"[reply] application {app.Id}: event {ev.Id} redrafted{(problems.Count > 0 ? " (still: " + string.Join("; ", problems) + ")" : "")}");
        return ActionResult.Done("новый черновик пришлю карточкой");
    }

    /// <summary>
    /// A short fingerprint of a draft's text, carried by the card's «Отправить» button: a card shown before the draft was rewritten must
    /// not send the new text the user has not seen. Stable across processes (not <see cref="string.GetHashCode()"/>); never 0 (0 = an old card
    /// without a version).
    /// </summary>
    public static int DraftVersion(string? draft)
    {
        var hash = 2166136261u;
        foreach (var b in Encoding.UTF8.GetBytes(draft ?? ""))
            hash = unchecked((hash ^ b) * 16777619u);
        var version = (int)(hash & 0x7FFFFFFF);
        return version == 0 ? 1 : version;
    }

    /// <summary>A private Telegram message: a reply when it comes from someone we wrote to (application DM or outreach).</summary>
    public async Task HandleTelegramAsync(TgPrivateMessage m, CancellationToken ct)
    {
        await using var ctx = await db.CreateDbContextAsync(ct);
        var handle = m.Username is null ? null : "@" + m.Username;
        var app = await ctx.Applications
            .Where(a => a.Channel == ApplicationChannel.TelegramDm && a.Status == ApplicationStatus.Sent
                        && (a.TargetPeerId == m.UserId || (handle != null && a.Target != null && a.Target.ToLower() == handle.ToLower())))
            .OrderByDescending(a => a.SentAt).FirstOrDefaultAsync(ct);
        if (app is null && handle is not null)
        {
            // A contact we reached through outreach answers: the conversation belongs to the application for that vacancy.
            var vacancyId = await ctx.Outreaches.Where(o => o.Status == OutreachStatus.Sent && o.Contact!.Kind == ContactKind.Telegram
                                                          && o.Contact.Value.ToLower() == handle.ToLower())
                .OrderByDescending(o => o.SentAt).Select(o => (long?)o.VacancyId).FirstOrDefaultAsync(ct);
            if (vacancyId is not null)
                app = await ctx.Applications.Where(a => a.VacancyId == vacancyId && a.Status == ApplicationStatus.Sent).OrderByDescending(a => a.SentAt)
                    .FirstOrDefaultAsync(ct);
        }

        var yourChat = false;
        if (app is null && handle is not null)
        {
            // A company's contact we did not write to from here (a recruiter the user found, or one from a vacancy text): the chat is the
            // user's own, so it goes to the application at that company as a draft only, never an automatic answer.
            var keys = await ctx.CompanyContacts.Where(c => c.Kind == ContactKind.Telegram && c.Value == handle.ToLower())
                .Select(c => c.CompanyKey).ToListAsync(ct);
            if (keys.Count > 0)
            {
                var sent = await ctx.Applications.Include(a => a.Vacancy).Where(a => a.Status == ApplicationStatus.Sent)
                    .OrderByDescending(a => a.SentAt).Take(500).ToListAsync(ct);
                app = sent.FirstOrDefault(a => keys.Contains(VacancyFingerprint.NormalizeCompany(a.Vacancy?.CompanyName)));
                yourChat = app is not null;
            }
        }

        if (app is null) return;
        if (handle is not null)
        {
            // The chat as it stands (what the user wrote from the phone included), so the draft answers the conversation, not one line.
            try
            {
                await chats.SyncThreadAsync(app.VacancyId, handle, m.MessageId, createApplication: false, ct);
            }
            catch (Exception e) when (!ct.IsCancellationRequested)
            {
                log.Warn($"[reply] chat history of {handle}: {e.Message}");
            }
        }

        await HandleInboundAsync(app.Id, EventChannel.Telegram, handle ?? m.Name ?? m.UserId.ToString(CultureInfo.InvariantCulture), m.Text,
            RecruiterChatService.ExternalId(m.UserId, m.MessageId), m.Date, ct, yourChat: yourChat);
    }

    /// <summary>Sends the drafted (or user-edited) answer to the author of the inbound message, through its channel.</summary>
    /// <summary>Automatic replies since the user last wrote or sent something themselves (newest events first).</summary>
    internal static int AutoInARow(IEnumerable<ApplicationEvent> events)
    {
        var count = 0;
        foreach (var e in events.OrderByDescending(e => e.At))
        {
            if (e.Kind != EventKind.MessageOut) continue;
            if (e.Author != AutoAuthor) break;
            count++;
        }

        return count;
    }

    public Task<ActionResult> SendReplyAsync(long eventId, string? overrideText, CancellationToken ct, bool auto = false, int version = 0,
        ReplyEditSnapshot? expected = null) => messages.ReplyAsync(eventId, overrideText, auto, version, ct, expected);

    /// <summary>The hh chat of the application's vacancy, from the synced "Отклики" history.</summary>
    internal static async Task<(long? ChatId, long? HhVacancyId)> HhChatIdAsync(JobFinderDbContext ctx, Application app, CancellationToken ct)
    {
        var external = await ctx.VacancySources.Where(s => s.VacancyId == app.VacancyId && s.Source == SourceKind.HeadHunter)
            .Select(s => s.ExternalId).ToListAsync(ct);
        var ids = external.Select(e => long.TryParse(e, out var id) ? id : 0).Where(id => id > 0).ToList();
        var row = await ctx.HhResponses.Where(r => ids.Contains(r.HhVacancyId) && r.ChatId != null).FirstOrDefaultAsync(ct);
        return (row?.ChatId, row?.HhVacancyId);
    }

    public async Task<ActionResult> DismissDraftAsync(long eventId, CancellationToken ct, ReplyEditSnapshot? expected = null)
    {
        var snapshot = expected ?? await draftSources.CaptureEditAsync(eventId, null, ct);
        return snapshot is null || snapshot.EventId != eventId ? ActionResult.Fail("уже обработано")
            : await draftSources.DismissAsync(snapshot, ct);
    }

    public static string Quote(string? text, int max = 700)
    {
        var sb = new StringBuilder("<blockquote expandable>").Append(TextUtil.EscapeHtml(TextUtil.Truncate(text, max))).Append("</blockquote>");
        return sb.ToString();
    }
}
