using System.Globalization;
using System.Text;
using JobFinder.Core.Domain;
using JobFinder.Core.Text;
using JobFinder.Core.Time;
using JobFinder.Data;
using JobFinder.Integrations.Speech;
using JobFinder.Llm.Client;
using JobFinder.Pipeline.Actions;
using JobFinder.Pipeline.Career;
using JobFinder.Pipeline.Jobs;
using JobFinder.Pipeline.Notifications;
using JobFinder.Pipeline.Modules.Hiring;
using JobFinder.Pipeline.Modules.Preparation;
using Microsoft.EntityFrameworkCore;
using Telegram.Bot;
using Telegram.Bot.Exceptions;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.ReplyMarkups;

namespace JobFinder.Bot;

/// <summary>
/// The career side of the bot: funnel buttons (stage, reply, outreach), interviews (time, pack, practice, debrief), the story interview,
/// offers, and voice messages (Deepgram → the same dialogue as typed text). Dialogue state lives in the pipeline (<see cref="CareerChat"/>),
/// so a restart does not lose a half-told story.
/// </summary>
public sealed partial class CareerBot(BotOptions options, CareerChat chat, StoryBank stories, MockInterviews mocks, OfferAdvisor offers,
    FunnelService funnel, ReplyService replies, OutreachService outreach, RecruiterChatService chats, WeeklyReportJob report, DeepgramStt stt,
    JobFinder.Pipeline.Profile.FactsProvider facts, JobFinder.Pipeline.Settings.RuntimeSettings settings,
    IDbContextFactory<JobFinderDbContext> db, LocalClock clock, ILogger<CareerBot> log, OutreachSources outreachSources, ReplyDraftSources replySources,
    BotEdits edits, OwnerBotMessages messages)
{
    private static readonly CultureInfo Ru = CultureInfo.GetCultureInfo("ru-RU");

    public static readonly BotCommand[] Commands =
    [
        new() { Command = "pipeline", Description = "Воронка откликов" },
        new() { Command = "interviews", Description = "Интервью и подготовка" },
        new() { Command = "mock", Description = "Тренировка интервью: /mock tech|hr|sd|code [id]" },
        new() { Command = "stories", Description = "Банк историй (интервью в боте)" },
        new() { Command = "offer", Description = "Разбор оффера и переговоры" },
        new() { Command = "debrief", Description = "Дебриф после интервью" },
        new() { Command = "report", Description = "Недельный отчёт сейчас" },
        new() { Command = "facts", Description = "Анкета фактов для ответов работодателям" },
        new() { Command = "autoreply", Description = "Автоответы работодателям: /autoreply on|off" },
        new() { Command = "stop", Description = "Прервать диалог" },
    ];

    public const string Help = """

        <b>Карьера</b>
        /pipeline — воронка: стадии откликов и активные процессы
        /interviews — ближайшие интервью: время, пакет, тренировка, дебриф
        /mock tech|hr|sd|code [id вакансии] — тренировка интервью (текст или голос)
        /stories — банк историй STAR: интервью по достижениям из резюме
        /offer [текст] — разбор оффера, рынок, контрпредложение
        /debrief — рассказать, как прошло интервью
        /outreach id — найти, кому написать в компании
        /report — недельный отчёт с советами для резюме hh
        /facts — анкета фактов: на что система отвечает сама (правка — дашборд, «Факты»)
        /autoreply on|off — рутинные ответы работодателям сами или только по кнопке
        /stop — прервать текущий диалог
        Голосовые сообщения понимаются в любом диалоге.
        """;

    // ---- messages ----

    /// <returns>true when the command is a career command (handled here).</returns>
    public async Task<bool> TryCommandAsync(ITelegramBotClient bot, string command, string arg, CancellationToken ct)
    {
        Func<Task<BotReply>>? action = command switch
        {
            "/pipeline" => () => PipelineAsync(ct),
            "/interviews" => () => InterviewsAsync(bot, ct),
            "/mock" => () => StartMockAsync(arg, ct),
            "/stories" => () => stories.StartAsync(ct),
            "/offer" => () => string.IsNullOrWhiteSpace(arg) ? chat.AskOfferAsync(ct) : offers.AnalyzeAsync(arg, ct),
            "/debrief" => () => DebriefAsync(arg, ct),
            "/outreach" => () => OutreachAsync(arg, ct),
            "/report" => async () => new BotReply((await report.BuildAsync(ct)).Html),
            "/facts" => () => FactsReplyAsync(ct),
            "/autoreply" => async () => await AutoReplyAsync(arg, ct),
            "/stop" => () => chat.StopAsync(ct),
            _ => null,
        };
        if (action is null) return false;
        await RunAsync(bot, action, ct);
        return true;
    }

    /// <returns>false when no dialogue is waiting for text (the caller shows its help).</returns>
    public async Task<bool> TryTextAsync(ITelegramBotClient bot, string text, CancellationToken ct)
    {
        var handled = true;
        await RunAsync(bot, async () =>
        {
            var reply = await chat.HandleTextAsync(text, ct);
            handled = reply is not null;
            return reply ?? new BotReply("");
        }, ct);
        return handled;
    }

    public async Task VoiceAsync(ITelegramBotClient bot, Message msg, CancellationToken ct)
    {
        if (!stt.IsConfigured)
        {
            await SendAsync(bot, new BotReply("🎙 Распознавание речи не настроено (Stt:ApiKey в config/appsettings.Development.json)."), ct);
            return;
        }

        var (fileId, mime) = msg switch
        {
            { Voice: { } v } => (v.FileId, v.MimeType ?? "audio/ogg"),
            { Audio: { } a } => (a.FileId, a.MimeType ?? "audio/mpeg"),
            { VideoNote: { } n } => (n.FileId, "video/mp4"),
            _ => (null, ""),
        };
        if (fileId is null) return;
        await TypingAsync(bot, ct);
        string text;
        try
        {
            using var audio = new MemoryStream();
            await bot.GetInfoAndDownloadFile(fileId, audio, ct);
            audio.Position = 0;
            text = (await stt.TranscribeAsync(audio, mime, ct)).Trim();
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            log.Warn($"[bot] voice not transcribed: {e.Message}");
            await SendAsync(bot, new BotReply("⚠️ Не удалось распознать голосовое: " + TextUtil.EscapeHtml(e.Message)), ct);
            return;
        }

        if (text.Length == 0)
        {
            await SendAsync(bot, new BotReply("🎙 Не расслышал — повторите, пожалуйста."), ct);
            return;
        }

        await SendAsync(bot, new BotReply("🗣 <i>" + TextUtil.EscapeHtml(TextUtil.Truncate(text, 3500)) + "</i>"), ct);
        if (!await TryTextAsync(bot, text, ct))
            await SendAsync(bot, new BotReply("Сейчас нет активного диалога. Голосом можно отвечать в /stories, /mock, /offer и дебрифе."), ct);
    }

    /// <summary>The user's own text in reply to "send me the text" (reply or outreach): sent as written.</summary>
    public async Task<bool> TryEditAsync(ITelegramBotClient bot, Message msg, CancellationToken ct)
    {
        if (options.OwnerUserId <= 0 || msg.From?.Id != options.OwnerUserId || msg.Chat.Id != options.OwnerUserId
            || msg.ReplyToMessage is not { } replied || string.IsNullOrWhiteSpace(msg.Text)) return false;
        CapturedBotEdit? captured;
        try { captured = await edits.FindAsync(bot.BotId, options.OwnerUserId, replied.MessageId, replied.Text, replied.From?.Id, ct); }
        catch (BotEditPromptException error)
        {
            await SendAsync(bot, new BotReply("⚠️ " + TextUtil.EscapeHtml(error.Message)), ct);
            return true;
        }
        if (captured is null || captured.Value.Kind is not ("reply" or "outreach")) return false;
        if (captured.Value.Completed)
        {
            await SendAsync(bot, new BotReply("Этот запрос уже обработан. Откройте новый запрос редактирования."), ct);
            return true;
        }
        var submitted = await edits.RecordReplyAsync(captured, msg.MessageId, msg.Text, ct);
        if (submitted is null)
        {
            await SendAsync(bot, new BotReply("⚠️ Запрос изменился; сохранённый текст не перезаписан."), ct);
            return true;
        }
        var edit = submitted.Value;
        var result = edit.Kind == "reply" ? await replies.SendReplyAsync(edit.SourceId, edit.Draft, ct, expected: edit.Reply)
            : await outreach.SendAsync(edit.SourceId, ct, edit.Draft, edit.Outreach);
        if (result.Ok) await edits.CompleteAsync(submitted, ct);
        await SendAsync(bot, new BotReply((result.Ok ? "✅ " : "⚠️ ") + TextUtil.EscapeHtml(result.Message)), ct);
        return true;
    }

    // ---- buttons ----

    /// <returns>false when the action is not a career one.</returns>
    public async Task<bool> TryCallbackAsync(ITelegramBotClient bot, CallbackQuery cb, string action, long id, int arg, CancellationToken ct)
    {
        switch (action)
        {
            case CareerCallback.Stage:
                var stage = (ApplicationStage)arg;
                await DoneAsync(bot, cb, await funnel.SetStageAsync(id, stage, "отмечено в боте", ct), ct);
                if (stage == ApplicationStage.Offer) await SendAsync(bot, await chat.AskOfferAsync(ct), ct);
                else if (FunnelService.IsInterview(stage))
                    await SendAsync(bot, new BotReply("🎯 Готовлю пакет к интервью (бриф о компании, вероятные вопросы, питч). Пришлю, как будет готов."), ct);
                return true;
            case CareerCallback.ReplySend:
                await AnswerAsync(bot, cb, "Отправляю…", ct);
                var replySend = await replySources.CaptureEditAsync(id, arg, ct);
                await DoneAsync(bot, cb, replySend is null ? ActionResult.Fail("карточка устарела; откройте актуальный черновик")
                    : await replies.SendReplyAsync(id, null, ct, expected: replySend), ct, answered: true);
                return true;
            case CareerCallback.ReplyEdit:
                var replyEdit = await replySources.CaptureEditAsync(id, arg, ct);
                if (replyEdit is null)
                {
                    await DoneAsync(bot, cb, ActionResult.Fail("карточка устарела; откройте актуальный черновик"), ct);
                    return true;
                }
                await AskForTextAsync(bot, cb, "reply", id, "✏️ Пришлите свой текст ответом на это сообщение — он уйдёт получателю как есть.", ct,
                    reply: replyEdit);
                return true;
            case CareerCallback.ReplySkip:
                var replySkip = await replySources.CaptureEditAsync(id, arg, ct);
                await DoneAsync(bot, cb, replySkip is null ? ActionResult.Fail("карточка устарела; откройте актуальный черновик")
                    : await replies.DismissDraftAsync(id, ct, replySkip), ct);
                return true;
            case CareerCallback.ReplyRedraft:
                await AnswerAsync(bot, cb, "Переписываю…", ct);
                var replyRedraft = await replySources.CaptureEditAsync(id, arg, ct);
                var redo = replyRedraft is null ? ActionResult.Fail("карточка устарела; откройте актуальный черновик")
                    : await replies.RedraftAsync(id, ct, expected: replyRedraft);
                if (redo.Ok) await ClearKeyboardAsync(bot, cb, ct);
                else await SendAsync(bot, new BotReply("⚠️ " + TextUtil.EscapeHtml(redo.Message)), ct);
                return true;
            case CareerCallback.OutreachSend:
                await AnswerAsync(bot, cb, "Отправляю…", ct);
                var sendSnapshot = await outreachSources.CaptureEditAsync(id, arg, ct);
                await DoneAsync(bot, cb, sendSnapshot is null ? ActionResult.Fail("карточка устарела; откройте актуальный черновик")
                    : await outreach.SendAsync(id, ct, snapshot: sendSnapshot), ct, answered: true);
                return true;
            case CareerCallback.OutreachSkip:
                var skipSnapshot = await outreachSources.CaptureEditAsync(id, arg, ct);
                await DoneAsync(bot, cb, skipSnapshot is null ? ActionResult.Fail("карточка устарела; откройте актуальный черновик")
                    : await outreach.DismissAsync(id, ct, skipSnapshot), ct);
                return true;
            case CareerCallback.OutreachEdit:
                var editSnapshot = await outreachSources.CaptureEditAsync(id, arg, ct);
                if (editSnapshot is null)
                {
                    await DoneAsync(bot, cb, ActionResult.Fail("карточка устарела; откройте актуальный черновик"), ct);
                    return true;
                }
                await AskForTextAsync(bot, cb, "outreach", id,
                    "✏️ Пришлите свой текст ответом на это сообщение: он уйдёт рекрутеру как есть (с резюме, если оно положено по ситуации).", ct, editSnapshot);
                return true;
            case CareerCallback.RecruiterDraft:
            case CareerCallback.RecruiterFresh:
            case CareerCallback.RecruiterContext:
                await AnswerAsync(bot, cb, action == CareerCallback.RecruiterFresh ? "Пишу с чистого листа…" : "Пишу черновик…", ct);
                bool? fresh = action switch { CareerCallback.RecruiterFresh => true, CareerCallback.RecruiterContext => false, _ => null };
                if (await outreach.DraftForContactAsync(id, arg, fresh, ct) is { Ok: false } failed)
                    await SendAsync(bot, new BotReply("⚠️ " + TextUtil.EscapeHtml(failed.Message)), ct);
                return true;
            case CareerCallback.RecruiterSync:
                await AnswerAsync(bot, cb, "Читаю переписку…", ct);
                var synced = await chats.SyncVacancyAsync(id, ct);
                await SendAsync(bot, new BotReply((synced.Ok ? "💬 " : "⚠️ ") + TextUtil.EscapeHtml(synced.Message) +
                                                  $"\nВся переписка на странице вакансии: http://127.0.0.1:8080/vacancies/{id}"), ct);
                return true;
            case CareerCallback.InterviewTime:
                await AnswerAsync(bot, cb, null, ct);
                await SendAsync(bot, await chat.AskTimeAsync(id, ct, arg), ct);
                return true;
            case CareerCallback.InterviewDebrief:
                await AnswerAsync(bot, cb, null, ct);
                await SendAsync(bot, await chat.AskDebriefAsync(id, ct, arg), ct);
                return true;
            case CareerCallback.HrRehearse:
                await AnswerAsync(bot, cb, "Готовлю вопросы…", ct);
                await RunAsync(bot, () => mocks.RehearseHrAsync(ct), ct);
                return true;
            case CareerCallback.InterviewRehearse:
                await AnswerAsync(bot, cb, "Готовлю вопросы…", ct);
                await RunAsync(bot, () => mocks.RehearseAsync(id, ct), ct);
                return true;
            case CareerCallback.InterviewPack:
                await AnswerAsync(bot, cb, null, ct);
                await SendPackAsync(bot, id, ct);
                return true;
            case CareerCallback.InterviewMock:
                await AnswerAsync(bot, cb, "Готовлю вопросы…", ct);
                var vacancyId = await VacancyOfInterviewAsync(id, ct);
                await RunAsync(bot, () => mocks.StartAsync((InterviewKind)arg, vacancyId, ct), ct);
                return true;
            case CareerCallback.StorySave:
                await AnswerAsync(bot, cb, "Сохраняю…", ct);
                await ClearKeyboardAsync(bot, cb, ct);
                await RunAsync(bot, () => stories.SaveAsync(id, arg, ct), ct);
                return true;
            case CareerCallback.StoryRedo:
                await AnswerAsync(bot, cb, "Переписываю…", ct);
                await ClearKeyboardAsync(bot, cb, ct);
                await RunAsync(bot, () => stories.RedoAsync(id, arg, ct), ct);
                return true;
            case CareerCallback.StorySkip:
                await AnswerAsync(bot, cb, null, ct);
                await ClearKeyboardAsync(bot, cb, ct);
                await RunAsync(bot, () => stories.SkipAsync(id, arg, ct), ct);
                return true;
            case CareerCallback.AutoReplyOff:
                await settings.SetAsync(JobFinder.Pipeline.Settings.RuntimeSettings.ChatAutoReply, false, ct);
                await DoneAsync(bot, cb, ActionResult.Done("автоответы выключены"), ct);
                return true;
            case CareerCallback.StoryStop:
                await AnswerAsync(bot, cb, null, ct);
                await ClearKeyboardAsync(bot, cb, ct);
                await RunAsync(bot, () => stories.StopAsync(id, arg, ct), ct);
                return true;
            case CareerCallback.MockStop:
                await AnswerAsync(bot, cb, null, ct);
                await ClearKeyboardAsync(bot, cb, ct);
                await RunAsync(bot, () => mocks.StopAsync(id, arg, ct), ct);
                return true;
            default:
                return false;
        }
    }

    // ---- commands ----

    /// <summary>The facts sheet at a glance: what the system may say on its own, what still waits for the user.</summary>
    private async Task<BotReply> FactsReplyAsync(CancellationToken ct)
    {
        var sheet = await facts.GetAsync(ct);
        var sb = new StringBuilder("🗂 <b>Анкета фактов</b> — на это система отвечает работодателям сама, только если факт подтверждён.\n\n");
        foreach (var f in sheet.Facts)
        {
            var mark = f.Confirmed && !string.IsNullOrWhiteSpace(f.Answer) ? "✅" : string.IsNullOrWhiteSpace(f.Answer) ? "❔" : "✏️";
            sb.Append(mark).Append(' ').Append(TextUtil.EscapeHtml(f.Question)).Append(": ")
                .Append(TextUtil.EscapeHtml(string.IsNullOrWhiteSpace(f.Answer) ? "не заполнено" : f.Answer)).Append('\n');
        }

        sb.Append("\n✅ подтверждено · ✏️ черновик из профиля — проверьте · ❔ пусто\nПравка и подтверждение: дашборд → «Факты» (http://127.0.0.1:8080/facts).");
        return new BotReply(CareerCards.Truncate(sb.ToString()));
    }

    private async Task<BotReply> AutoReplyAsync(string arg, CancellationToken ct)
    {
        var key = JobFinder.Pipeline.Settings.RuntimeSettings.ChatAutoReply;
        if (arg is "on" or "off" or "1" or "0" or "да" or "нет") await settings.SetAsync(key, arg is "on" or "1" or "да", ct);
        var on = await settings.GetBoolAsync(key, true, ct);
        return new BotReply(on
            ? "🤖 Автоответы включены: рутина (интерес, подтверждённые факты, слоты созвона) уходит сама, остальное — черновиком. /autoreply off — выключить."
            : "⏸ Автоответы выключены: каждый ответ работодателю ждёт вашей кнопки. /autoreply on — включить.");
    }

    private async Task<BotReply> PipelineAsync(CancellationToken ct)
    {
        await using var ctx = await db.CreateDbContextAsync(ct);
        var apps = await ctx.Applications.AsNoTracking().Include(a => a.Vacancy)
            .Where(a => a.Status == ApplicationStatus.Sent && a.Stage != null).ToListAsync(ct);
        if (apps.Count == 0) return new BotReply("Воронка пуста: ещё нет отправленных откликов.");
        var sb = new StringBuilder("📈 <b>Воронка</b> · всего откликов ").Append(apps.Count).Append('\n');
        var byStage = apps.GroupBy(a => a.Stage!.Value).ToDictionary(g => g.Key, g => g.Count());
        sb.Append(string.Join(" · ", Enum.GetValues<ApplicationStage>().Where(byStage.ContainsKey).Select(s => $"{FunnelService.Label(s)} {byStage[s]}"))).Append('\n');
        var stats = FunnelStats.Of(apps);
        sb.Append("Конверсия: ответ ").Append(Pct(stats.Replied, stats.Sent)).Append(", интервью ").Append(Pct(stats.Interviews, stats.Sent))
            .Append(", оффер ").Append(Pct(stats.Offers, stats.Sent)).Append('\n');

        var active = apps.Where(a => a.Stage is ApplicationStage.Replied or ApplicationStage.HrScreen or ApplicationStage.TechInterview
                                     or ApplicationStage.Final or ApplicationStage.Offer)
            .OrderByDescending(a => a.StageChangedAt).Take(15).ToList();
        if (active.Count > 0)
        {
            sb.Append("\n<b>В процессе</b>\n");
            foreach (var a in active) sb.Append(Line(a)).Append('\n');
        }

        var waiting = apps.Where(a => a.Stage is ApplicationStage.Sent or ApplicationStage.Viewed).OrderBy(a => a.SentAt).Take(5).ToList();
        if (waiting.Count > 0)
        {
            sb.Append("\n<b>Дольше всех без ответа</b>\n");
            foreach (var a in waiting) sb.Append(Line(a)).Append('\n');
        }

        sb.Append("\nСтадию можно поменять кнопками в карточках или в дашборде (страница «Воронка»).");
        return new BotReply(CareerCards.Truncate(sb.ToString()));

        string Line(Application a)
        {
            var at = TimeZoneInfo.ConvertTime(a.StageChangedAt ?? a.SentAt ?? a.CreatedAt, clock.Zone).ToString("dd.MM", CultureInfo.InvariantCulture);
            return $"• {FunnelService.Label(a.Stage)} · {TextUtil.EscapeHtml(TextUtil.Truncate(a.Vacancy?.CompanyName ?? "?", 30))} — " +
                   $"{Cards.Link(a.Vacancy!.Url, TextUtil.Truncate(a.Vacancy.Title, 45))} · {at} · #{a.VacancyId}";
        }
    }

    /// <summary>Upcoming interviews, each as its own card with the interview buttons.</summary>
    private async Task<BotReply> InterviewsAsync(ITelegramBotClient bot, CancellationToken ct)
    {
        await using var ctx = await db.CreateDbContextAsync(ct);
        var list = await ctx.Interviews.AsNoTracking().Include(i => i.Application).ThenInclude(a => a!.Vacancy)
            .Where(i => i.Status == InterviewStatus.Scheduled).OrderBy(i => i.ScheduledAt == null).ThenBy(i => i.ScheduledAt).Take(8).ToListAsync(ct);
        if (list.Count == 0) return new BotReply("Запланированных интервью нет. Когда пригласят — отметьте «📞 Интервью» в карточке отклика, пакет подготовки придёт сам.");
        foreach (var i in list.Skip(1))
        {
            var (text, buttons) = InterviewCard(i);
            await SendAsync(bot, new BotReply(text, buttons), ct);
        }

        var (firstText, firstButtons) = InterviewCard(list[0]);
        return new BotReply(firstText, firstButtons);
    }

    private (string Text, List<List<string>> Buttons) InterviewCard(Interview i)
    {
        var v = i.Application!.Vacancy!;
        var when = i.ScheduledAt is { } at ? TimeZoneInfo.ConvertTime(at, clock.Zone).ToString("dddd d MMMM, HH:mm", Ru) : "время не указано";
        var text = $"🎯 <b>{CareerCards.InterviewLabel(i.Kind)}</b> · {TextUtil.EscapeHtml(v.CompanyName ?? "?")} — {TextUtil.EscapeHtml(v.Title)}\n🗓 {when}\n" +
                   (i.PackPdfPath is null ? "Пакет готовится…" : "Пакет готов.");
        List<List<string>> buttons =
        [
            [Callback.Button("🗓 Время", CareerCallback.InterviewTime, i.Id, CareerPromptSources.ControlVersion(i)), Callback.Button("📄 Пакет", CareerCallback.InterviewPack, i.Id),
                Callback.Button("📝 Дебриф", CareerCallback.InterviewDebrief, i.Id, CareerPromptSources.ControlVersion(i))],
            [
                Callback.Button("🎤 HR", CareerCallback.InterviewMock, i.Id, (int)InterviewKind.HrScreen),
                Callback.Button("🎤 Тех", CareerCallback.InterviewMock, i.Id, (int)InterviewKind.Technical),
                Callback.Button("🎤 SD", CareerCallback.InterviewMock, i.Id, (int)InterviewKind.SystemDesign),
                Callback.Button("🎤 Код", CareerCallback.InterviewMock, i.Id, (int)InterviewKind.Coding),
            ],
        ];
        return (text, buttons);
    }

    private async Task<BotReply> StartMockAsync(string arg, CancellationToken ct)
    {
        var kind = InterviewKind.Technical;
        long? vacancyId = null;
        foreach (var token in arg.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (long.TryParse(token.TrimStart('#'), NumberStyles.Integer, CultureInfo.InvariantCulture, out var id)) vacancyId = id;
            else kind = token.ToLowerInvariant() switch
            {
                "hr" or "хр" => InterviewKind.HrScreen,
                "sd" or "system" or "design" or "сд" => InterviewKind.SystemDesign,
                "code" or "coding" or "live" or "код" => InterviewKind.Coding,
                "behavioral" or "beh" => InterviewKind.Other,
                _ => InterviewKind.Technical,
            };
        }

        // Without an id: practise for the nearest interview, if there is one.
        if (vacancyId is null)
        {
            await using var ctx = await db.CreateDbContextAsync(ct);
            vacancyId = await ctx.Interviews.Where(i => i.Status == InterviewStatus.Scheduled).OrderBy(i => i.ScheduledAt == null).ThenBy(i => i.ScheduledAt)
                .Select(i => (long?)i.Application!.VacancyId).FirstOrDefaultAsync(ct);
        }

        return await mocks.StartAsync(kind, vacancyId, ct);
    }

    private async Task<BotReply> DebriefAsync(string arg, CancellationToken ct)
    {
        long? id = long.TryParse(arg, NumberStyles.Integer, CultureInfo.InvariantCulture, out var given) ? given : null;
        if (id is null)
        {
            await using var ctx = await db.CreateDbContextAsync(ct);
            var now = clock.UtcNow;
            id = await ctx.Interviews.Where(i => i.Status == InterviewStatus.Scheduled && i.Debrief == null)
                .OrderBy(i => i.ScheduledAt == null || i.ScheduledAt > now).ThenByDescending(i => i.ScheduledAt).Select(i => (long?)i.Id).FirstOrDefaultAsync(ct);
        }

        return id is null ? new BotReply("Нет интервью для дебрифа. Отметьте стадию «📞 Интервью» в карточке отклика.") : await chat.AskDebriefAsync(id.Value, ct);
    }

    private async Task<BotReply> OutreachAsync(string arg, CancellationToken ct)
    {
        if (!long.TryParse(arg.TrimStart('#'), NumberStyles.Integer, CultureInfo.InvariantCulture, out var vacancyId))
            return new BotReply("Формат: /outreach 123 (id вакансии из карточки).");
        var o = await outreach.PrepareAsync(vacancyId, ct);
        return new BotReply(o is null
            ? "Не нашёл, кому написать (или в эту компанию уже писали на этой неделе). Контакты копятся из постов, hh и поиска в Telegram/LinkedIn."
            : "👤 Черновик готов — карточка выше.");
    }

    // ---- plumbing ----

    private async Task<long?> VacancyOfInterviewAsync(long interviewId, CancellationToken ct)
    {
        await using var ctx = await db.CreateDbContextAsync(ct);
        return await ctx.Interviews.Where(i => i.Id == interviewId).Select(i => (long?)i.Application!.VacancyId).FirstOrDefaultAsync(ct);
    }

    private async Task SendPackAsync(ITelegramBotClient bot, long interviewId, CancellationToken ct)
    {
        await using var ctx = await db.CreateDbContextAsync(ct);
        var i = await ctx.Interviews.AsNoTracking().Include(x => x.Application).ThenInclude(a => a!.Vacancy).FirstOrDefaultAsync(x => x.Id == interviewId, ct);
        if (i?.PackPdfPath is not { } stored || !JobFinder.Core.Options.StoredPath.Exists(stored))
        {
            await SendAsync(bot, new BotReply("Пакет ещё готовится — пришлю, как только будет готов."), ct);
            return;
        }

        await SendAsync(bot, new BotReply($"📄 Пакет: {TextUtil.EscapeHtml(i.Application!.Vacancy!.CompanyName ?? "")} ({CareerCards.InterviewLabel(i.Kind)})",
            AttachmentPath: stored, AttachmentName: $"Интервью — {i.Application.Vacancy.CompanyName} — {CareerCards.InterviewLabel(i.Kind)}.pdf"), ct);
    }

    /// <summary>Runs a dialogue step with a typing indicator and turns failures into a message instead of silence.</summary>
    private async Task RunAsync(ITelegramBotClient bot, Func<Task<BotReply>> step, CancellationToken ct)
    {
        await TypingAsync(bot, ct);
        BotReply reply;
        try
        {
            reply = await step();
        }
        catch (LlmUnavailableException e)
        {
            reply = new BotReply("⚠️ Модели сейчас недоступны: " + TextUtil.EscapeHtml(e.Message) + "\nПовторите чуть позже — диалог сохранён.");
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            log.Error($"[bot] career action failed: {e.Message}", e);
            reply = new BotReply("⚠️ " + TextUtil.EscapeHtml(e.Message));
        }

        if (reply.Text.Length > 0) await SendAsync(bot, reply, ct);
    }

    public Task SendAsync(ITelegramBotClient bot, BotReply reply, CancellationToken ct) =>
        reply.Text.Length == 0 && reply.AttachmentPath is null ? Task.CompletedTask
            : messages.PublishAsync(bot, options.OwnerUserId, reply.Text, ct, reply.Buttons, reply.AttachmentPath, reply.AttachmentName);

    private async Task AskForTextAsync(ITelegramBotClient bot, CallbackQuery cb, string kind, long id, string prompt, CancellationToken ct,
        OutreachEditSnapshot? snapshot = null, ReplyEditSnapshot? reply = null)
    {
        await AnswerAsync(bot, cb, null, ct);
        await edits.BeginQuestionAsync(bot.BotId, options.OwnerUserId,
            new BotEditPrompt { Kind = kind, SourceId = id, Outreach = snapshot, Reply = reply }, prompt, ct);
    }

    /// <summary>Answers the button and replaces its row with the outcome (other rows of the card — e.g. the stage buttons — stay).</summary>
    private async Task DoneAsync(ITelegramBotClient bot, CallbackQuery cb, ActionResult result, CancellationToken ct, bool answered = false)
    {
        if (!answered) await AnswerAsync(bot, cb, TextUtil.Truncate(result.Message, 190), ct, alert: !result.Ok);
        else if (!result.Ok) await SendAsync(bot, new BotReply("⚠️ " + TextUtil.EscapeHtml(result.Message)), ct);
        if (!result.Ok || cb.Message?.ReplyMarkup is not { } markup) return;
        if (cb.Data is null || !markup.InlineKeyboard.SelectMany(row => row).Any(button => button.CallbackData == cb.Data)) return;
        if (await messages.EditKeyboardAsync(bot, cb.Message.Chat.Id, cb.Message.MessageId,
                [["✔️ " + TextUtil.Truncate(result.Message, 50) + "|noop:0:0"]], ct, replaceCallback: cb.Data) is null)
            log.Debug("[bot] keyboard row is stale or has no verified native card receipt; not queued");
    }

    private async Task ClearKeyboardAsync(ITelegramBotClient bot, CallbackQuery cb, CancellationToken ct)
    {
        if (cb.Message is not { } m) return;
        if (await messages.EditKeyboardAsync(bot, m.Chat.Id, m.MessageId, [], ct) is null)
            log.Debug("[bot] keyboard has no verified native card receipt; not cleared");
    }

    private static async Task AnswerAsync(ITelegramBotClient bot, CallbackQuery cb, string? text, CancellationToken ct, bool alert = false)
    {
        try
        {
            await bot.AnswerCallbackQuery(cb.Id, text, showAlert: alert, cancellationToken: ct);
        }
        catch (ApiRequestException)
        {
            // Answered too late (a long step before): the button still worked.
        }
    }

    private Task TypingAsync(ITelegramBotClient bot, CancellationToken ct) =>
        bot.SendChatAction(options.OwnerUserId, ChatAction.Typing, cancellationToken: ct);

    private static string Pct(int part, int total) => total == 0 ? "—" : $"{part} ({100.0 * part / total:0}%)";

}
