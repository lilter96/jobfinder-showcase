using System.Globalization;
using System.Text;
using JobFinder.Core.Domain;
using JobFinder.Core.Text;

namespace JobFinder.Pipeline.Notifications;

/// <summary>
/// Callback data: <c>{area}:{action}:{id}[:{arg}]</c>; comment buttons append the captured revision after arg, within Telegram's 64-byte limit. The bot parses exactly
/// these; the dashboard calls the same actions directly.
/// </summary>
public static class Callback
{
    public const string ApplyVacancy = "v:apply";
    public const string SkipVacancy = "v:skip";
    public const string RegenLetter = "v:regen";
    public const string EditLetter = "v:edit";
    public const string ManualDone = "v:done";
    public const string ResumePdf = "v:cv";
    public const string PickComment = "c:pick";
    public const string EditComment = "c:edit";
    public const string SkipComment = "c:skip";
    public const string CancelComment = "c:cancel";
    public const string ApproveChannels = "ch:approve";
    public const string ReviewChannels = "ch:review";

    public static string Button(string label, string action, long id, int? arg = null) =>
        arg is null ? $"{label}|{action}:{id}" : $"{label}|{action}:{id}:{arg}";
}

/// <summary>Telegram HTML texts of the bot messages. One place for the wording, so the bot and the digest look the same.</summary>
public static class Cards
{
    /// <param name="recruiter">The company's Telegram recruiter, if known: a direct message is offered next to the response.</param>
    /// <param name="letterProblems">What the letter checks still found: shown, so the user fixes the letter before sending (each one cut: a
    /// reviewer's note with its fix runs to a paragraph, and the card must stay under Telegram's 4096 characters).</param>
    public static (string Text, List<List<string>> Buttons) VacancyApproval(Vacancy v, Workers.ApplyRoute route, CompanyContact? recruiter = null,
        IReadOnlyList<string>? letterProblems = null)
    {
        if (v.LetterReview is { } review && review.TextHash == Career.Fingerprint.Of(Core.Text.HumanText.Clean(v.CoverLetter)))
            letterProblems ??= review.Problems;
        var sb = new StringBuilder();
        sb.Append(ScoreBadge(v.Score)).Append(" <b>").Append(TextUtil.EscapeHtml(v.Title)).Append("</b>\n");
        AppendFacts(sb, v);
        if (v.Legitimacy == Legitimacy.Suspicious)
            sb.Append("🕵️ <b>Подозрительно:</b> ").Append(TextUtil.EscapeHtml(string.Join("; ", v.Evaluation?.LegitimacyReasons ?? []))).Append('\n');
        if (v.Evaluation?.MarketNote is { } market) sb.Append("💹 ").Append(TextUtil.EscapeHtml(market)).Append('\n');
        if (v.ResponsesCount is > 0) sb.Append("👥 откликов уже: ").Append(v.ResponsesCount).Append('\n');
        if (v.Evaluation is { } e)
        {
            sb.Append('\n').Append(TextUtil.EscapeHtml(e.Summary)).Append('\n');
            if (e.Strengths.Count > 0) sb.Append("✅ ").Append(TextUtil.EscapeHtml(string.Join("; ", e.Strengths.Take(4)))).Append('\n');
            if (e.Gaps.Count > 0) sb.Append("⚠️ ").Append(TextUtil.EscapeHtml(string.Join("; ", e.Gaps.Take(3)))).Append('\n');
            if (e.RedFlags.Count > 0) sb.Append("🚩 ").Append(TextUtil.EscapeHtml(string.Join("; ", e.RedFlags.Take(3)))).Append('\n');
        }

        if (!string.IsNullOrWhiteSpace(v.CoverLetter))
        {
            var header = route.Channel switch
            {
                ApplicationChannel.TelegramDm => $"Сообщение {TextUtil.EscapeHtml(route.Target)} (+ резюме PDF)",
                ApplicationChannel.Email => $"Письмо на {TextUtil.EscapeHtml(route.Target)} (+ резюме PDF)",
                _ => "Письмо",
            };
            sb.Append("\n<b>").Append(header).Append(":</b>\n<blockquote expandable>").Append(TextUtil.EscapeHtml(v.CoverLetter)).Append("</blockquote>\n");
            if (letterProblems is { Count: > 0 })
                sb.Append("⚠️ Письмо не прошло проверку, лучше «✏️ Своё письмо» или «🔄 Другое письмо»: ")
                    .Append(TextUtil.EscapeHtml(string.Join("; ", letterProblems.Take(3).Select(p => TextUtil.Truncate(p, 220))))).Append('\n');
        }

        sb.Append('\n').Append(Link(v.Url, "Открыть вакансию")).Append(" · #").Append(v.Id);

        var send = route.Channel switch
        {
            ApplicationChannel.HeadHunter => "🚀 Откликнуться",
            ApplicationChannel.TelegramDm => "✉️ В ЛС + резюме",
            ApplicationChannel.Email => "📧 На почту + резюме",
            _ => null,
        };
        var row1 = new List<string>();
        if (send is not null) row1.Add(Callback.Button(send, Callback.ApplyVacancy, v.Id));
        else row1.Add(Callback.Button("✅ Откликнулся сам", Callback.ManualDone, v.Id));
        row1.Add(Callback.Button("🙈 Пропустить", Callback.SkipVacancy, v.Id));
        var row2 = new List<string>
        {
            Callback.Button("✏️ Своё письмо", Callback.EditLetter, v.Id),
            Callback.Button("🔄 Другое письмо", Callback.RegenLetter, v.Id),
        };
        var row3 = new List<string> { Callback.Button("📄 Резюме под вакансию", Callback.ResumePdf, v.Id) };
        if (recruiter is { Kind: ContactKind.Telegram } r && route.Channel != ApplicationChannel.TelegramDm)
            row3.Add(Callback.Button($"✈️ Рекрутеру {r.Value}", CareerCallback.RecruiterDraft, v.Id, (int)r.Id));
        return (Truncate(sb.ToString()), [row1, row2, row3]);
    }

    public static string AutoApplied(Vacancy v) =>
        Truncate($"🚀 <b>Автоотклик</b> {ScoreBadge(v.Score)} {TextUtil.EscapeHtml(v.Title)} — {TextUtil.EscapeHtml(v.CompanyName ?? "?")}\n" +
                 $"{TextUtil.EscapeHtml(v.Evaluation?.Summary ?? "")}\n{Link(v.Url, "Вакансия")} · #{v.Id}");

    public static string ApplyProblem(Vacancy v, string outcome, string message) =>
        Truncate($"⚠️ <b>Отклик не отправлен</b> ({TextUtil.EscapeHtml(outcome)}): {TextUtil.EscapeHtml(v.Title)} — " +
                 $"{TextUtil.EscapeHtml(v.CompanyName ?? "?")}\n{TextUtil.EscapeHtml(message)}\n{Link(v.Url, "Откликнуться вручную")} · #{v.Id}");

    /// <summary>Under a response the user finishes on the site (or drops): the same actions as on the dashboard.</summary>
    public static List<List<string>> ManualRow(long vacancyId) =>
        [[Callback.Button("✅ Откликнулся сам", Callback.ManualDone, vacancyId), Callback.Button("🙈 Пропустить", Callback.SkipVacancy, vacancyId)]];

    /// <summary>A response checked under dry-run: held, not failed — it goes out by itself once dry-run is off.</summary>
    public static (string Text, List<List<string>> Buttons) DryRunHeld(Vacancy v, bool letterRequired, int holdDays) =>
        (Truncate($"🧪 <b>Dry-run</b>: отклик проверен, но не отправлен — {TextUtil.EscapeHtml(v.Title)} — {TextUtil.EscapeHtml(v.CompanyName ?? "?")}\n" +
                  (letterRequired ? "Письмо обязательно: при отправке откроется форма.\n" : "Быстрый отклик: письмо уйдёт в чат сразу после него.\n") +
                  $"Уйдёт сам после /dryrun off (если прошло не больше {holdDays} дн.).\n{Link(v.Url, "Вакансия")} · #{v.Id}"),
            ManualRow(v.Id));

    public static (string Text, List<List<string>> Buttons) CommentDraft(CommentDraft d)
    {
        var sb = new StringBuilder();
        sb.Append("💬 <b>Threads</b> · ").Append(AudienceLabel(d.Audience)).Append(" · @").Append(TextUtil.EscapeHtml(d.Author)).Append('\n');
        sb.Append("<blockquote expandable>").Append(TextUtil.EscapeHtml(TextUtil.Truncate(d.PostText, 900))).Append("</blockquote>\n");
        if (!string.IsNullOrWhiteSpace(d.Reason)) sb.Append("<i>").Append(TextUtil.EscapeHtml(d.Reason)).Append("</i>\n");
        for (var i = 0; i < d.Variants.Count; i++)
            sb.Append('\n').Append(i + 1).Append(". ").Append(TextUtil.EscapeHtml(d.Variants[i])).Append('\n');
        sb.Append('\n').Append(Link(d.PostUrl, "Пост")).Append(" · #c").Append(d.Id);

        var picks = d.Variants.Select((_, i) => Callback.Button($"✅ {i + 1}", Callback.PickComment, d.Id, i) + ":" + d.Revision.ToString(CultureInfo.InvariantCulture)).ToList();
        return (Truncate(sb.ToString()),
        [
            picks,
            [Callback.Button("✏️ Свой текст", Callback.EditComment, d.Id, 0) + ":" + d.Revision.ToString(CultureInfo.InvariantCulture), Callback.Button("🙈 Пропустить", Callback.SkipComment, d.Id, 0) + ":" + d.Revision.ToString(CultureInfo.InvariantCulture)],
        ]);
    }

    public static string CommentPosted(CommentDraft d) =>
        Truncate($"✅ <b>Комментарий опубликован</b> под постом {Link(d.PostUrl, "@" + d.Author)} · #c{d.Id}\n" +
                 $"<blockquote expandable>{TextUtil.EscapeHtml(TextUtil.Truncate(d.PostText, 300))}</blockquote>\n💬 {TextUtil.EscapeHtml(d.ChosenText ?? "")}");

    public static (string Text, List<List<string>> Buttons) ChannelsProposed(IReadOnlyList<TelegramChannel> channels, long? batchId = null)
    {
        var sb = new StringBuilder($"📋 <b>Предложено {channels.Count} Telegram-каналов/чатов для мониторинга</b>\n");
        foreach (var c in channels.Take(60))
        {
            var reason = TextUtil.EscapeHtml(TextUtil.Truncate(c.Reason ?? c.Title, 70));
            if (reason.Length > 96) reason = TextUtil.EscapeHtml(TextUtil.Truncate(c.Reason ?? c.Title, 16));
            sb.Append("• ").Append(c.Username is null ? $"#{c.Id}" : "@" + TextUtil.EscapeHtml(c.Username)).Append(" — ").Append(reason).Append('\n');
        }
        if (channels.Count > 60) sb.Append("… и ещё ").Append(channels.Count - 60).Append('\n');
        sb.Append("\nСписок можно поправить в дашборде (страница «Каналы»). После одобрения аккаунт подписывается постепенно, ")
            .Append("каналы мьютятся, уходят в архив и в папку.");
        var text = sb.ToString();
        return (Truncate(text), text.Length <= 4000 && batchId is > 0 && channels.Count is > 0 and <= Modules.Discovery.ChannelDecisions.CardSize
            ? [[Callback.Button($"✅ Одобрить эти {channels.Count}", Callback.ApproveChannels, batchId.Value)]] : []);
    }

    public static string Alert(string title, string details) => Truncate($"🛑 <b>{TextUtil.EscapeHtml(title)}</b>\n{TextUtil.EscapeHtml(details)}");

    public static string ScoreBadge(int? score) => score switch
    {
        null => "⚪",
        >= 80 => $"🟢 {score}",
        >= 60 => $"🟡 {score}",
        _ => $"🔴 {score}",
    };

    public static string Link(string url, string text) => $"<a href=\"{TextUtil.EscapeHtml(url).Replace("\"", "&quot;")}\">{TextUtil.EscapeHtml(text)}</a>";

    public static string Salary(Vacancy v)
    {
        if (v.SalaryFrom is null && v.SalaryTo is null) return "";
        var ru = CultureInfo.GetCultureInfo("ru-RU");
        var from = v.SalaryFrom?.ToString("#,0", ru);
        var to = v.SalaryTo?.ToString("#,0", ru);
        var range = from is not null && to is not null ? $"{from}–{to}" : from is not null ? $"от {from}" : $"до {to}";
        return $"{range} {v.Currency}{(v.SalaryGross == true ? " gross" : "")}";
    }

    public static string Formats(WorkFormat f)
    {
        var parts = new List<string>();
        if (f.HasFlag(WorkFormat.Office)) parts.Add("офис");
        if (f.HasFlag(WorkFormat.Hybrid)) parts.Add("гибрид");
        if (f.HasFlag(WorkFormat.Remote)) parts.Add("удалёнка");
        return string.Join("/", parts);
    }

    public static string AudienceLabel(AudienceKind a) => a switch
    {
        AudienceKind.Recruiter => "HR/рекрутер",
        AudienceKind.HiringManager => "нанимающий менеджер",
        AudienceKind.Tech => "тех-пост",
        AudienceKind.Hiring => "ищут разработчика",
        _ => "другое",
    };

    private static void AppendFacts(StringBuilder sb, Vacancy v)
    {
        var facts = new List<string>();
        if (v.CompanyName is not null) facts.Add("🏢 " + TextUtil.EscapeHtml(v.CompanyName));
        var place = string.Join(", ", new[] { v.City, Formats(v.WorkFormats) }.Where(s => !string.IsNullOrWhiteSpace(s)));
        if (place.Length > 0) facts.Add("📍 " + TextUtil.EscapeHtml(place));
        var salary = Salary(v);
        if (salary.Length > 0) facts.Add("💰 " + TextUtil.EscapeHtml(salary));
        if (v.Evaluation?.SalaryNote is { } note && v.SalaryAdjustment != 0) facts.Add($"({TextUtil.EscapeHtml(note)})");
        if (EnglishLevel.A1 <= v.EnglishRequired) facts.Add("🇬🇧 " + v.EnglishRequired);
        var sources = string.Join(", ", v.Sources.Select(s => s.Source).Distinct());
        if (sources.Length > 0) facts.Add("🔎 " + sources);
        sb.Append(string.Join("\n", facts)).Append('\n');
    }

    /// <summary>Telegram caps a message at 4096 characters; cut on a line boundary and never inside a tag.</summary>
    private static string Truncate(string html)
    {
        const int max = 4000;
        if (html.Length <= max) return html;
        var cut = html.LastIndexOf('\n', max);
        var text = html[..(cut > 0 ? cut : max)];
        // An unclosed blockquote would make Telegram reject the whole message.
        if (text.LastIndexOf("<blockquote", StringComparison.Ordinal) > text.LastIndexOf("</blockquote>", StringComparison.Ordinal))
            text += "</blockquote>";
        return text + "\n…";
    }
}
