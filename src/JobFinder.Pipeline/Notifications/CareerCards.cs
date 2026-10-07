using System.Globalization;
using System.Text;
using JobFinder.Core.Domain;
using JobFinder.Core.Text;
using JobFinder.Llm.Tasks;
using JobFinder.Pipeline.Career;
using JobFinder.Pipeline.Modules.Hiring;
using JobFinder.Pipeline.Modules.Preparation;

namespace JobFinder.Pipeline.Notifications;

/// <summary>Callback data of the career buttons (same "{area}:{action}:{id}[:{arg}]" shape as <see cref="Callback"/>).</summary>
public static class CareerCallback
{
    public const string Stage = "a:st";
    public const string ReplySend = "r:send";
    public const string ReplyEdit = "r:edit";
    public const string ReplySkip = "r:skip";
    public const string ReplyRedraft = "r:redo";
    public const string OutreachSend = "o:send";
    public const string OutreachSkip = "o:skip";
    public const string OutreachEdit = "o:edit";

    /// <summary>A draft to one recruiter: id = vacancy, arg = contact; the mode as the situation suggests.</summary>
    public const string RecruiterDraft = "t:draft";

    /// <summary>The same, from scratch: the earlier response is not mentioned.</summary>
    public const string RecruiterFresh = "t:fresh";

    /// <summary>The same, with the earlier response and its conversation.</summary>
    public const string RecruiterContext = "t:ctx";

    /// <summary>The Telegram chats of the vacancy's company into its response: id = vacancy.</summary>
    public const string RecruiterSync = "t:sync";
    public const string InterviewTime = "i:time";
    public const string InterviewMock = "i:mock";
    public const string InterviewPack = "i:pack";
    public const string InterviewRehearse = "i:reh";
    public const string HrRehearse = "h:reh";
    public const string InterviewDebrief = "i:debrief";
    public const string StorySave = "s:save";
    public const string StoryRedo = "s:redo";
    public const string StorySkip = "s:skip";
    public const string StoryStop = "s:stop";
    public const string MockStop = "m:stop";
    public const string AutoReplyOff = "d:off";
}

public static class CareerCards
{
    private static readonly CultureInfo Ru = CultureInfo.GetCultureInfo("ru-RU");

    public static List<string> StageRow(long applicationId) =>
    [
        Callback.Button("📞 Интервью", CareerCallback.Stage, applicationId, (int)ApplicationStage.HrScreen),
        Callback.Button("💬 Ответили", CareerCallback.Stage, applicationId, (int)ApplicationStage.Replied),
        Callback.Button("❌ Отказ", CareerCallback.Stage, applicationId, (int)ApplicationStage.Rejected),
        Callback.Button("🏆 Оффер", CareerCallback.Stage, applicationId, (int)ApplicationStage.Offer),
    ];

    /// <summary>A reply the system sent on its own: what they asked, what went out, how to stop it.</summary>
    public static (string Text, List<List<string>> Buttons) AutoReplied(Application app, ApplicationEvent ev, ReplyAnalysis analysis)
    {
        var v = app.Vacancy!;
        var sb = new StringBuilder("🤖 <b>Ответил сам</b> · ").Append(TextUtil.EscapeHtml(v.CompanyName ?? "?")).Append(" — ").Append(TextUtil.EscapeHtml(v.Title)).Append('\n');
        sb.Append(DialoguePolicy.IntentLabel(analysis.Intent)).Append(analysis.Counterpart == "bot" ? " · похоже, пишет бот" : "").Append('\n');
        sb.Append("<b>Они:</b>").Append(ReplyService.Quote(ev.Text, 500)).Append('\n');
        sb.Append("<b>Я:</b>").Append(ReplyService.Quote(ev.Draft, 600));
        sb.Append('\n').Append(Cards.Link(v.Url, "Вакансия")).Append(" · #").Append(v.Id);
        return (Truncate(sb.ToString()), [StageRow(app.Id), [Callback.Button("⏸ Выключить автоответы", CareerCallback.AutoReplyOff, 0)]]);
    }

    /// <param name="draftProblems">What the style check still finds in the draft after the rewrite.</param>
    /// <param name="facts">To name the missing facts by their questions.</param>
    public static (string Text, List<List<string>> Buttons) Reply(Application app, ApplicationEvent ev, ReplyAnalysis analysis, DateTimeOffset? interviewLocal,
        string? whyNotAuto = null, IReadOnlyList<string>? draftProblems = null, Core.Profile.CandidateFacts? facts = null, bool queuedAutomatic = false)
    {
        var v = app.Vacancy!;
        var icon = ev.Classification switch
        {
            ReplyClass.Invitation => "📞",
            ReplyClass.Rejection => "❌",
            ReplyClass.Offer => "🏆",
            ReplyClass.Question => "❓",
            ReplyClass.Viewed => "👀",
            _ => "💬",
        };
        var kind = ev.Classification switch
        {
            ReplyClass.Invitation => "приглашение",
            ReplyClass.Rejection => "отказ",
            ReplyClass.Offer => "оффер",
            ReplyClass.Question => "вопрос",
            ReplyClass.Viewed => "просмотрено",
            _ => "сообщение",
        };
        var sb = new StringBuilder();
        sb.Append(icon).Append(" <b>").Append(kind).Append("</b> · ").Append(TextUtil.EscapeHtml(v.CompanyName ?? "?")).Append(" — ")
            .Append(TextUtil.EscapeHtml(v.Title)).Append('\n');
        sb.Append("от ").Append(TextUtil.EscapeHtml(ev.Author)).Append(" · ").Append(ev.Channel).Append(" · стадия: ")
            .Append(FunnelService.Label(app.Stage)).Append('\n');
        sb.Append(ReplyService.Quote(ev.Text)).Append('\n');
        sb.Append("<i>").Append(TextUtil.EscapeHtml(analysis.Summary)).Append("</i>\n");
        if (interviewLocal is { } at) sb.Append("🗓 ").Append(at.ToString("dddd d MMMM, HH:mm", Ru)).Append(" (Минск)\n");
        if (analysis.Counterpart == "bot") sb.Append("🤖 похоже, пишет бот или ИИ-ассистент\n");
        foreach (var conflict in analysis.Conflicts) sb.Append("⚠️ Расхождение: ").Append(TextUtil.EscapeHtml(conflict)).Append('\n');
        if (queuedAutomatic) sb.Append("⏳ Автоответ в очереди; доставка ещё не подтверждена.\n");
        else if (whyNotAuto is not null) sb.Append("🔸 Нужен ваш ответ: ").Append(TextUtil.EscapeHtml(whyNotAuto)).Append('\n');
        if (ev.Draft is { } draft)
        {
            sb.Append("\n<b>Черновик ответа").Append(ev.Channel == EventChannel.HeadHunter ? " (в чат hh)" : "").Append(":</b>\n");
            sb.Append(ReplyService.Quote(draft, 1500)).Append('\n');
            if (analysis.Verify.Count > 0)
                sb.Append("🔎 Выведено из опыта, сверьте: ").Append(TextUtil.EscapeHtml(string.Join("; ", analysis.Verify))).Append('\n');
            if (analysis.MissingFacts.Count > 0)
                sb.Append("📝 Нет в фактах: ").Append(TextUtil.EscapeHtml(string.Join(", ", analysis.MissingFacts.Select(k => facts?.Get(k)?.Question ?? k))))
                    .Append(" (/facts)\n");
            if (draftProblems is { Count: > 0 })
                sb.Append("⚠️ Черновик не прошёл проверку: ").Append(TextUtil.EscapeHtml(string.Join("; ", draftProblems))).Append('\n');
        }

        sb.Append('\n').Append(Cards.Link(v.Url, "Вакансия")).Append(" · #").Append(v.Id);
        var buttons = new List<List<string>>();
        if (!queuedAutomatic && ev.Draft is not null && ev.Channel is EventChannel.Telegram or EventChannel.Email or EventChannel.HeadHunter)
        {
            var version = ReplyEditSnapshot.Capture(ev, app).Version;
            buttons.Add([Callback.Button("✉️ Отправить ответ", CareerCallback.ReplySend, ev.Id, version), Callback.Button("✏️ Изменить", CareerCallback.ReplyEdit, ev.Id, version)]);
            buttons.Add([Callback.Button("🔄 Переписать", CareerCallback.ReplyRedraft, ev.Id, version), Callback.Button("🙈 Не отвечать", CareerCallback.ReplySkip, ev.Id, version)]);
        }
        buttons.Add(StageRow(app.Id));
        if (queuedAutomatic) buttons.Add([Callback.Button("⏸ Выключить автоответы", CareerCallback.AutoReplyOff, 0)]);
        return (Truncate(sb.ToString()), buttons);
    }

    public static (string Text, List<List<string>> Buttons) FollowUp(Application app, ApplicationEvent ev, int days)
    {
        var v = app.Vacancy!;
        var sb = new StringBuilder();
        sb.Append("⏰ <b>Нет ответа ").Append(days).Append(" раб. дн.</b> · ").Append(TextUtil.EscapeHtml(v.CompanyName ?? "?")).Append(" — ")
            .Append(TextUtil.EscapeHtml(v.Title)).Append('\n');
        sb.Append("Черновик напоминания (").Append(ev.Channel).Append(ev.Author is null ? "" : " → " + TextUtil.EscapeHtml(ev.Author)).Append("):\n");
        sb.Append(ReplyService.Quote(ev.Draft, 1200));
        sb.Append('\n').Append(Cards.Link(v.Url, "Вакансия")).Append(" · #").Append(v.Id);
        var buttons = new List<List<string>>();
        if (ev.Channel is EventChannel.Telegram or EventChannel.Email or EventChannel.HeadHunter)
        {
            var version = ReplyEditSnapshot.Capture(ev, app).Version;
            buttons.Add([Callback.Button("✉️ Отправить", CareerCallback.ReplySend, ev.Id, version), Callback.Button("✏️ Изменить", CareerCallback.ReplyEdit, ev.Id, version),
                Callback.Button("🙈 Не надо", CareerCallback.ReplySkip, ev.Id, version)]);
        }
        buttons.Add(StageRow(app.Id));
        return (Truncate(sb.ToString()), buttons);
    }

    /// <param name="draft">A draft to a chosen recruiter: its situation, mode (from scratch or with the earlier response) and a mode switch.</param>
    public static (string Text, List<List<string>> Buttons) Outreach(Outreach o, CompanyContact c, Vacancy v, RecruiterDraft? draft = null)
    {
        var attachResume = draft?.AttachResume ?? true;
        var situation = draft is null ? null : draft.Fresh ? RecruiterChatService.FreshLabel : RecruiterChatService.Label(draft.Situation);
        var sb = new StringBuilder();
        sb.Append(situation is null ? "👤 <b>Кому написать</b> · " : "✈️ <b>Рекрутеру</b> · ").Append(TextUtil.EscapeHtml(v.CompanyName ?? "?")).Append(" — ")
            .Append(TextUtil.EscapeHtml(v.Title)).Append(" · #").Append(v.Id).Append('\n');
        sb.Append(TextUtil.EscapeHtml(c.PersonName ?? c.Value));
        if (c.Role is not null) sb.Append(" · ").Append(TextUtil.EscapeHtml(c.Role));
        sb.Append("\n").Append(c.Kind).Append(": ").Append(TextUtil.EscapeHtml(c.Value)).Append(" · источник: ").Append(TextUtil.EscapeHtml(c.Source)).Append('\n');
        if (situation is not null) sb.Append("Ситуация: ").Append(TextUtil.EscapeHtml(situation)).Append('\n');
        sb.Append(c.Kind == ContactKind.LinkedIn ? "\n<b>Заметка к запросу в контакты</b> (≤ 300 знаков, скопируйте):\n<code>" + TextUtil.EscapeHtml(o.Text) + "</code>"
            : "\n<b>Сообщение</b>" + (attachResume ? " (+ резюме PDF под вакансию)" : "") + ":\n" + ReplyService.Quote(o.Text));
        foreach (var e in draft?.Evidence ?? [])
            sb.Append('\n').Append(e.Found ? "✔ опора: «" : "⚠️ нет в профиле: «").Append(TextUtil.EscapeHtml(TextUtil.Truncate(e.Quote, 120))).Append('»');
        if (draft?.Problems is { Count: > 0 } problems)
            sb.Append("\n\n⚠️ <b>Не прошёл проверку</b>, лучше поправить («✏️ Свой текст»): ").Append(TextUtil.EscapeHtml(string.Join("; ", problems.Take(4))));
        var version = JobFinder.Pipeline.Modules.Hiring.OutreachEditSnapshot.Capture(o, c).Version;
        var buttons = new List<List<string>>();
        switch (c.Kind)
        {
            case ContactKind.Telegram:
                buttons.Add([Callback.Button("✈️ Отправить", CareerCallback.OutreachSend, o.Id, version), Callback.Button("✏️ Свой текст", CareerCallback.OutreachEdit, o.Id, version)]);
                var row = new List<string>();
                // With an earlier response the other mode is one tap away: a new draft card comes, this one stays unsent.
                if (draft is { Situation: not RecruiterSituation.NotApplied })
                    row.Add(draft.Fresh
                        ? Callback.Button("↩️ С учётом прошлого отклика", CareerCallback.RecruiterContext, o.VacancyId, (int)o.ContactId)
                        : Callback.Button("🧼 С чистого листа", CareerCallback.RecruiterFresh, o.VacancyId, (int)o.ContactId));
                row.Add(Callback.Button("🙈 Не надо", CareerCallback.OutreachSkip, o.Id, version));
                buttons.Add(row);
                break;
            case ContactKind.Email:
                buttons.Add([Callback.Button("📧 Отправить", CareerCallback.OutreachSend, o.Id, version), Callback.Button("🙈 Не надо", CareerCallback.OutreachSkip, o.Id, version)]);
                break;
            default:
                sb.Append('\n').Append(Cards.Link(c.Value, "Открыть профиль"));
                buttons.Add([Callback.Button("✅ Отправил сам", CareerCallback.OutreachSend, o.Id, version), Callback.Button("🙈 Не надо", CareerCallback.OutreachSkip, o.Id, version)]);
                break;
        }

        return (Truncate(sb.ToString()), buttons);
    }

    public static (string Text, List<List<string>> Buttons) Pack(Interview i, Vacancy v, InterviewCopilot c, DateTimeOffset? local)
    {
        var sb = new StringBuilder();
        sb.Append("🎯 <b>Подготовка к интервью</b> (").Append(InterviewLabel(i.Kind)).Append(") · ").Append(TextUtil.EscapeHtml(v.CompanyName ?? "?"))
            .Append(" — ").Append(TextUtil.EscapeHtml(v.Title)).Append('\n');
        sb.Append(local is { } at ? "🗓 " + at.ToString("dddd d MMMM, HH:mm", Ru) + " (Минск)\n" : "🗓 время не указано — нажмите «Указать время»\n");
        sb.Append("Риск интервью: ").Append(Career.CopilotRules.Label(c.OverallRisk)).Append('\n');
        if (c.Priorities.Count > 0)
        {
            sb.Append("\n<b>Что важно</b>\n");
            var n = 0;
            foreach (var p in c.Priorities.Take(5))
                sb.Append(++n).Append(". ").Append(TextUtil.EscapeHtml(p.Topic)).Append(" · ").Append(Career.CopilotRules.Label(p.Priority)).Append(", ")
                    .Append(Career.CopilotRules.Label(p.CandidateLevel)).Append('\n');
        }

        if (c.LastMinute.About.Length > 0)
            sb.Append("\n<b>О себе (30 сек)</b>\n").Append(ReplyService.Quote(c.LastMinute.About, 900)).Append('\n');
        var toCheck = c.Preparation.Count(p => p.RequiresApproval) + (c.Salary?.RequiresApproval == true ? 1 : 0);
        if (toCheck > 0) sb.Append("⚠️ Проверьте перед интервью: ").Append(toCheck).Append(" ответов (дашборд → Интервью, пометка ⚠️)\n");
        sb.Append("\nВ PDF: страница «за 10 минут», ").Append(c.Preparation.Count)
            .Append(" вопросов с речью на 15, 30 и 60 секунд и уточнениями, пробелы, переговоры, вопросы работодателю. «Прогнать мои ответы»: бот спрашивает, вы отвечаете голосом, он сверяет с подготовленным.");
        return (Truncate(sb.ToString()),
        [
            [Callback.Button("🧠 Прогнать мои ответы", CareerCallback.InterviewRehearse, i.Id)],
            [Callback.Button("🗓 Указать время", CareerCallback.InterviewTime, i.Id, CareerPromptSources.ControlVersion(i, v)), Callback.Button("📝 Дебриф", CareerCallback.InterviewDebrief, i.Id, CareerPromptSources.ControlVersion(i, v))],
            [
                Callback.Button("🎤 HR", CareerCallback.InterviewMock, i.Id, (int)InterviewKind.HrScreen),
                Callback.Button("🎤 Тех", CareerCallback.InterviewMock, i.Id, (int)InterviewKind.Technical),
                Callback.Button("🎤 SD", CareerCallback.InterviewMock, i.Id, (int)InterviewKind.SystemDesign),
                Callback.Button("🎤 Код", CareerCallback.InterviewMock, i.Id, (int)InterviewKind.Coding),
            ],
        ]);
    }

    public static string InterviewLabel(InterviewKind k) => k switch
    {
        InterviewKind.HrScreen => "HR-скрининг",
        InterviewKind.Technical => "техническое",
        InterviewKind.SystemDesign => "system design",
        InterviewKind.Coding => "live-coding",
        InterviewKind.Final => "финальное",
        _ => "интервью",
    };

    public static string Truncate(string html)
    {
        const int max = 4000;
        if (html.Length <= max) return html;
        var cut = html.LastIndexOf('\n', max);
        var text = html[..(cut > 0 ? cut : max)];
        // Exact openers: "<b>" must not match "<blockquote".
        foreach (var (open, close) in new[] { ("<blockquote", "</blockquote>"), ("<code>", "</code>"), ("<b>", "</b>"), ("<i>", "</i>") })
        {
            if (text.LastIndexOf(open, StringComparison.Ordinal) > text.LastIndexOf(close, StringComparison.Ordinal)) text += close;
        }

        return text + "\n…";
    }
}
