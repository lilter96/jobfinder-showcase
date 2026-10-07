using System.Globalization;
using System.Net;
using System.Text;
using JobFinder.Core.Domain;
using JobFinder.Llm.Tasks;
using JobFinder.Pipeline.Career;

namespace JobFinder.Pipeline.Documents;

/// <summary>
/// The interview preparation as a printable document. Page one is the last ten minutes (pitch, why them, why the role, cases, risks,
/// topics, follow-ups, what not to say); then what matters, the questions with their speech from start to 60 seconds and the
/// follow-ups, gaps, money, questions to ask, risks; the company notes last, as supporting data.
/// </summary>
public static class PackHtml
{
    public static string Render(InterviewCopilot c, Vacancy v, string kind, DateTimeOffset? local, string fontCss)
    {
        var sb = new StringBuilder();
        sb.Append("<!doctype html><html lang=\"ru\"><head><meta charset=\"utf-8\"><title>Интервью — ").Append(E(v.CompanyName)).Append("</title><style>")
            .Append(fontCss).Append(Css).Append("</style></head><body>");
        sb.Append("<h1>").Append(E(v.CompanyName ?? "Компания")).Append("</h1><div class=\"sub\">").Append(E(c.Role.Length > 0 ? c.Role : v.Title))
            .Append(" · ").Append(E(kind));
        if (local is { } at) sb.Append(" · ").Append(E(at.ToString("d MMMM yyyy, HH:mm", CultureInfo.GetCultureInfo("ru-RU")))).Append(" (Минск)");
        sb.Append(" · риск: ").Append(E(CopilotRules.Label(c.OverallRisk))).Append("</div>");

        var m = c.LastMinute;
        sb.Append("<div class=\"last\"><h2>За 10 минут до интервью</h2>");
        Speech(sb, "О себе", m.About);
        Speech(sb, "Почему компания", m.WhyCompany);
        Speech(sb, "Почему эта роль", m.WhyRole);
        if (m.TopStories.Count > 0)
        {
            sb.Append("<div class=\"k\">Сильные кейсы</div><ul>");
            foreach (var s in m.TopStories) sb.Append("<li><b>").Append(E(s.Title)).Append("</b> ").Append(E(s.Line)).Append("</li>");
            sb.Append("</ul>");
        }

        List(sb, "Риски", m.TopRisks);
        Lines(sb, "Темы: как начать", m.TopTopics);
        Lines(sb, "Уточнения: как начать", m.FollowUps);
        if (m.DoNotSay.Count > 0)
        {
            sb.Append("<div class=\"k\">Не говорить</div><ul class=\"dont\">");
            foreach (var d in m.DoNotSay) sb.Append("<li>«").Append(E(d.Say)).Append("» → ").Append(E(d.Instead)).Append("</li>");
            sb.Append("</ul>");
        }

        sb.Append("</div>");

        if (c.Priorities.Count > 0)
        {
            sb.Append("<h2>Что реально важно на этом интервью</h2><ol class=\"prio\">");
            foreach (var p in c.Priorities)
            {
                sb.Append("<li><b>").Append(E(p.Topic)).Append("</b> · ").Append(E(CopilotRules.Label(p.Priority))).Append(" · ")
                    .Append(E(CopilotRules.Label(p.CandidateLevel))).Append(" · ").Append(E(CopilotRules.Label(p.Basis)))
                    .Append("<div class=\"why\">").Append(E(p.Reason)).Append(p.Evidence.Length > 0 ? " Основание: " + E(p.Evidence) : "").Append("</div></li>");
            }

            sb.Append("</ol>");
        }

        sb.Append("<h2>Вопросы и ответы</h2>");
        foreach (var item in c.Preparation) Question(sb, item, c.Evidence);

        foreach (var g in c.Gaps)
        {
            sb.Append("<div class=\"q\"><div class=\"qq\">Пробел: ").Append(E(g.Topic)).Append(" · ").Append(E(CopilotRules.Label(g.Level))).Append("</div>");
            if (g.Known.Length > 0) sb.Append("<div class=\"why\">Рядом в опыте: ").Append(E(g.Known)).Append("</div>");
            if (g.Learn.Count > 0) sb.Append("<div class=\"why\">Повторить: ").Append(E(string.Join("; ", g.Learn))).Append("</div>");
            if (g.SafeWording.Length > 0) sb.Append("<div class=\"say\">").Append(E(g.SafeWording)).Append("</div>");
            sb.Append("</div>");
        }

        sb.Append("<h2>Переговоры</h2>");
        if (c.Salary is { } salary) Question(sb, salary, c.Evidence);
        if (c.Negotiation.Stance.Length > 0) sb.Append("<p>").Append(E(c.Negotiation.Stance)).Append("</p>");
        if (c.Negotiation.Market.Length > 0) sb.Append("<p class=\"why\">Рынок: ").Append(E(c.Negotiation.Market)).Append("</p>");
        List(sb, "Согласовать", c.Negotiation.Conditions);

        if (c.EmployerQuestions.MustAsk.Count + c.EmployerQuestions.NiceToAsk.Count > 0)
        {
            sb.Append("<h2>Вопросы работодателю</h2>");
            Ask(sb, "Обязательно", c.EmployerQuestions.MustAsk);
            Ask(sb, "Если останется время", c.EmployerQuestions.NiceToAsk);
        }

        if (c.Risks.Count > 0)
        {
            sb.Append("<h2>Риски</h2><ul>");
            foreach (var r in c.Risks) sb.Append("<li><b>").Append(E(r.Risk)).Append("</b> (").Append(E(CopilotRules.Label(r.Severity))).Append(") ").Append(E(r.Mitigation)).Append("</li>");
            sb.Append("</ul>");
        }

        sb.Append("<h2>О компании (справка)</h2><p>").Append(E(c.Company.Brief)).Append("</p>");
        List(sb, "Что им важно", c.Company.WhatTheyLookFor);
        List(sb, "Из знаний модели, проверить", c.Company.ToVerify);
        sb.Append("</body></html>");
        return sb.ToString();
    }

    private static void Question(StringBuilder sb, PrepItem item, IReadOnlyDictionary<string, string> evidence)
    {
        var q = item.Q;
        sb.Append("<div class=\"q\"><div class=\"qq\">").Append(E(q.Question)).Append("</div><div class=\"why\">")
            .Append(E(CopilotRules.Label(q.Category))).Append(" · приоритет ").Append(E(CopilotRules.Label(q.Priority))).Append(" · ")
            .Append(E(CopilotRules.Label(q.CandidateLevel))).Append(" · проверяют: ").Append(E(q.Tests)).Append("</div>");
        if (item.A is { NeedsPosition: true } position)
        {
            sb.Append("<div class=\"check\">Нужна ваша позиция (дашборд): ").Append(E(string.Join(" / ", position.PositionOptions))).Append("</div></div>");
            return;
        }

        if (item.RequiresApproval) sb.Append("<div class=\"check\">Проверьте перед интервью: ").Append(E(string.Join("; ", item.ApprovalReasons))).Append("</div>");
        if (item.A is { } a)
        {
            sb.Append("<div class=\"start\">").Append(E(a.Start)).Append("</div>");
            sb.Append("<div class=\"core\"><span class=\"t\">30 сек</span> ").Append(E(a.Core)).Append("</div>");
            if (a.Extended is { Length: > 0 } ext) sb.Append("<div class=\"ext\"><span class=\"t\">60 сек</span> ").Append(E(ext)).Append("</div>");
            if (q.Story is { } s && evidence.TryGetValue(s.Ref, out var story))
                sb.Append("<div class=\"story\">Кейс: ").Append(E(Short(story))).Append(s.Contribution.Length > 0 ? " · вклад: " + E(s.Contribution) : "").Append("</div>");
            foreach (var f in a.FollowUps)
                sb.Append("<div class=\"fu\"><b>→ ").Append(E(f.Question)).Append("</b> ").Append(E(f.Start)).Append("</div>");
            if (a.DoNotSay.Count > 0)
                sb.Append("<div class=\"dont\">Не говорить: ").Append(E(string.Join("; ", a.DoNotSay.Select(d => $"«{d.Say}» → {d.Instead}")))).Append("</div>");
            if (a.Verify.Count > 0) sb.Append("<div class=\"check\">Сверить: ").Append(E(string.Join("; ", a.Verify))).Append("</div>");
        }

        sb.Append("</div>");
    }

    private static void Speech(StringBuilder sb, string label, string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        sb.Append("<div class=\"k\">").Append(E(label)).Append(" <span class=\"t\">").Append(E(CopilotRules.Seconds(text))).Append("</span></div>")
            .Append("<div class=\"say\">").Append(E(text)).Append("</div>");
    }

    private static void Lines(StringBuilder sb, string title, IReadOnlyCollection<LastMinuteLine> lines)
    {
        if (lines.Count == 0) return;
        sb.Append("<div class=\"k\">").Append(E(title)).Append("</div><ul>");
        foreach (var l in lines) sb.Append("<li><b>").Append(E(l.Topic)).Append("</b> ").Append(E(l.Line)).Append("</li>");
        sb.Append("</ul>");
    }

    private static void Ask(StringBuilder sb, string title, IReadOnlyCollection<AskItem> items)
    {
        if (items.Count == 0) return;
        sb.Append("<div class=\"k\">").Append(E(title)).Append("</div><ol>");
        foreach (var i in items) sb.Append("<li>").Append(E(i.Question)).Append(i.Why.Length > 0 ? " <span class=\"why\">" + E(i.Why) + "</span>" : "").Append("</li>");
        sb.Append("</ol>");
    }

    private static void List(StringBuilder sb, string title, IReadOnlyCollection<string> items)
    {
        if (items.Count == 0) return;
        sb.Append("<div class=\"k\">").Append(E(title)).Append("</div><ul>");
        foreach (var i in items) sb.Append("<li>").Append(E(i)).Append("</li>");
        sb.Append("</ul>");
    }

    private static string Short(string text) => text.Length <= 140 ? text : text[..140].TrimEnd() + "...";

    private static string E(string? s) => WebUtility.HtmlEncode(s ?? "");

    private const string Css = """
        @page { size: A4; margin: 12mm 14mm; }
        body { font-family: 'Inter', 'Segoe UI', Arial, sans-serif; font-size: 9.4pt; line-height: 1.42; color: #1c2230; margin: 0; }
        h1 { font-size: 18pt; margin: 0; color: #111827; }
        .sub { color: #1f4e8c; font-weight: 600; margin: 2pt 0 6pt; }
        h2 { font-size: 10pt; text-transform: uppercase; letter-spacing: .6pt; color: #1f4e8c; border-bottom: 1px solid #d6dde8;
             padding-bottom: 2pt; margin: 12pt 0 5pt; }
        p { margin: 0 0 4pt; }
        ul, ol { margin: 2pt 0 4pt 14pt; padding: 0; } li { margin: 0 0 2pt; }
        .last { background: #f1f5fb; border-left: 3px solid #1f4e8c; padding: 4pt 9pt 6pt; break-after: page; }
        .last h2 { margin-top: 4pt; }
        .k { font-weight: 600; margin-top: 5pt; color: #111827; }
        .t { color: #6b7280; font-weight: 400; font-size: 8.4pt; }
        .say { margin: 1pt 0 3pt; }
        .q { margin: 0 0 9pt; break-inside: avoid; }
        .qq { font-weight: 700; color: #111827; font-size: 10pt; }
        .why { color: #4b5563; font-size: 8.8pt; }
        .start { font-weight: 600; margin-top: 2pt; }
        .core, .ext { margin-top: 2pt; }
        .story { color: #1f4e8c; font-size: 8.8pt; margin-top: 2pt; }
        .fu { font-size: 8.8pt; margin-top: 1pt; }
        .dont { color: #9a3412; font-size: 8.8pt; margin-top: 2pt; }
        .check { color: #8a5a00; font-size: 8.8pt; margin-top: 2pt; }
        """;
}
