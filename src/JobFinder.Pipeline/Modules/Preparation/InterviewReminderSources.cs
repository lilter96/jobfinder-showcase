using System.Globalization;
using System.Text;
using JobFinder.Core.Domain;
using JobFinder.Core.Text;
using JobFinder.Core.Time;
using JobFinder.Data;
using JobFinder.Llm.Tasks;
using JobFinder.Pipeline.Career;
using JobFinder.Pipeline.Jobs;
using JobFinder.Pipeline.Notifications;
using Microsoft.EntityFrameworkCore;

namespace JobFinder.Pipeline.Modules.Preparation;

internal static class InterviewReminderSources
{
    private static readonly CultureInfo Ru = CultureInfo.GetCultureInfo("ru-RU");
    internal static async Task<bool> NotifyAsync(IDbContextFactory<JobFinderDbContext> db, Notifier notifier, CareerOptions options, LocalClock clock, InterviewEditSnapshot snapshot, DateTimeOffset now, bool debrief, CancellationToken ct)
    {
        await using var context = await db.CreateDbContextAsync(ct);
        await using var transaction = await context.Database.BeginTransactionAsync(ct);
        await InterviewPreparationCapture.LockAsync(context, snapshot.Source.Id, ct);
        var source = await context.Interviews.Include(row => row.Application).ThenInclude(app => app!.Vacancy)
            .SingleOrDefaultAsync(row => row.Id == snapshot.Source.Id, ct);
        if (source is null || !snapshot.Matches(source) || source.Status != InterviewStatus.Scheduled || source.ScheduledAt is null) return false;
        if (debrief)
        {
            if (source.DebriefAsked || source.ScheduledAt.Value.AddMinutes(source.DurationMinutes).AddHours(options.DebriefHoursAfter) > now) return false;
            source.DebriefAsked = true;
            var vacancy = source.Application!.Vacancy!;
            notifier.Add(context, NotificationKind.Info,
                $"📝 <b>Как прошло?</b> {CareerCards.InterviewLabel(source.Kind)} · {TextUtil.EscapeHtml(vacancy.CompanyName ?? "?")} — {TextUtil.EscapeHtml(vacancy.Title)}\n" +
                "Расскажите пару минут (можно голосовым): сохраню вопросы и выводы, подготовлю благодарность и следующий этап.",
                [[Callback.Button("📝 Дебриф", CareerCallback.InterviewDebrief, source.Id, CareerPromptSources.ControlVersion(source))], CareerCards.StageRow(source.ApplicationId)], source.Id);
        }
        else
        {
            if (source.Reminded || source.ScheduledAt > now.AddMinutes(options.ReminderMinutesBefore) || source.ScheduledAt <= now.AddMinutes(-10)) return false;
            source.Reminded = true;
            notifier.Add(context, NotificationKind.Info, Reminder(source, now, clock), ReminderButtons(source), source.Id,
                Core.Options.StoredPath.Exists(source.PackPdfPath) ? source.PackPdfPath : null,
                source.PackPdfPath is null ? null : $"Шпаргалка — {source.Application!.Vacancy!.CompanyName}.pdf");
        }
        await notifier.SaveAndFlushAsync(context, ct);
        return true;
    }

    private static string Reminder(Interview i, DateTimeOffset now, LocalClock clock)
    {
        var v = i.Application!.Vacancy!;
        var at = TimeZoneInfo.ConvertTime(i.ScheduledAt!.Value, clock.Zone);
        var minutes = Math.Max(0, (int)Math.Round((i.ScheduledAt.Value - now).TotalMinutes));
        var sb = new StringBuilder("⏰ <b>Через ").Append(minutes).Append(" мин: ").Append(CareerCards.InterviewLabel(i.Kind)).Append("</b> · ")
            .Append(TextUtil.EscapeHtml(v.CompanyName ?? "?")).Append(" — ").Append(TextUtil.EscapeHtml(v.Title)).Append('\n')
            .Append("🗓 ").Append(at.ToString("HH:mm, dddd d MMMM", Ru)).Append('\n');
        // The last ten minutes: the speech to say first, the cases, the risks, how to start on the likely topics, what not to say.
        var c = InterviewPrep.Read(i.PackJson);
        if (c is { Version: InterviewCopilot.CurrentVersion })
        {
            var m = c.LastMinute;
            if (m.About.Length > 0) sb.Append("\n<b>О себе</b>\n").Append(ReplyService.Quote(m.About, 900)).Append('\n');
            Bullets(sb, "Сильные кейсы", m.TopStories.Select(s => $"{TextUtil.Truncate(s.Title, 80)}: {s.Line}"));
            Bullets(sb, "Риски", m.TopRisks);
            Bullets(sb, "Темы: как начать", m.TopTopics.Select(t => $"{t.Topic} → {TextUtil.Truncate(t.Line, 220)}"));
            Bullets(sb, "Не говорить", m.DoNotSay.Select(d => $"«{d.Say}» → {d.Instead}"));
            Bullets(sb, "Спросить", c.EmployerQuestions.MustAsk.Select(q => q.Question));
        }
        else
        {
            sb.Append("\nПодготовка ещё не готова: откройте вакансию и повторите рассказ о себе.\n");
        }

        sb.Append("\nУдачи! Камера, звук, тишина, вода рядом.");
        return CareerCards.Truncate(sb.ToString());
    }

    private static void Bullets(StringBuilder sb, string title, IEnumerable<string> lines)
    {
        var list = lines.Where(l => !string.IsNullOrWhiteSpace(l)).ToList();
        if (list.Count == 0) return;
        sb.Append("\n<b>").Append(title).Append("</b>\n");
        foreach (var line in list) sb.Append("• ").Append(TextUtil.EscapeHtml(line)).Append('\n');
    }

    private static List<List<string>> ReminderButtons(Interview i) =>
    [
        [Callback.Button("🧠 Прогнать ответы", CareerCallback.InterviewRehearse, i.Id), Callback.Button("📝 Дебриф", CareerCallback.InterviewDebrief, i.Id, CareerPromptSources.ControlVersion(i))],
    ];
}
