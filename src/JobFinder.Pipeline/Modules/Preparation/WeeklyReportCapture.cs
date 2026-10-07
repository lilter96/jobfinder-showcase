using System.Globalization;
using System.Text;
using JobFinder.Core.Domain;
using JobFinder.Core.Filtering;
using JobFinder.Core.Text;
using JobFinder.Core.Time;
using JobFinder.Data;
using JobFinder.Pipeline.Career;
using JobFinder.Pipeline.Jobs;
using JobFinder.Pipeline.Market;
using Microsoft.EntityFrameworkCore;

namespace JobFinder.Pipeline.Modules.Preparation;

public sealed class WeeklyReportCapture(MarketService market, LocalClock clock)
{
    internal async Task<WeeklyReportFrame> CaptureAsync(JobFinderDbContext ctx, DateTimeOffset now, string coverage, CancellationToken ct)
    {
        using var stage = Core.Telemetry.Telemetry.StartStage("capture-statistics", "weekly-report");
        var weekAgo = now.AddDays(-7);
        var apps = await ctx.Applications.AsNoTracking().Include(application => application.Vacancy).Where(application => application.Status == ApplicationStatus.Sent && application.SentAt != null)
            .ToListAsync(ct);
        var week = apps.Where(application => application.SentAt >= weekAgo).ToList();
        var funnel = FunnelStats.Of(apps);
        var weekFunnel = FunnelStats.Of(week);
        var found = await ctx.Vacancies.CountAsync(vacancy => vacancy.FirstSeenAt >= weekAgo, ct);
        var scoredGood = await ctx.Vacancies.CountAsync(vacancy => vacancy.FirstSeenAt >= weekAgo && vacancy.Score >= 60, ct);
        var spent = await ctx.LlmCalls.Where(countEntry => countEntry.At >= weekAgo).SumAsync(countEntry => countEntry.CostUsd, ct);
        var interviews = await ctx.Interviews.CountAsync(i => i.CreatedAt >= weekAgo, ct);
        var mocks = await ctx.MockSessions.Where(m => m.CreatedAt >= weekAgo && m.Status == MockStatus.Finished).CountAsync(ct);
        var stories = await ctx.Stories.CountAsync(segment => segment.Confirmed, ct);

        var sb = new StringBuilder("📊 <b>Неделя в поиске</b> · ").Append(TimeZoneInfo.ConvertTime(now, clock.Zone).ToString("dd.MM", CultureInfo.InvariantCulture)).Append("\n\n");
        sb.Append("Найдено вакансий: <b>").Append(found).Append("</b>, подходящих (≥60): <b>").Append(scoredGood).Append("</b>\n");
        sb.Append("Отправлено откликов: <b>").Append(week.Count).Append("</b> (").Append(string.Join(", ", week.GroupBy(application => application.Channel)
            .Select(group => $"{WeeklyReportJob.ChannelLabel(group.Key)} {group.Count()}"))).Append(")\n");
        sb.Append("За неделю: ответы ").Append(weekFunnel.Replied).Append(", интервью ").Append(weekFunnel.Interviews).Append(", офферы ")
            .Append(weekFunnel.Offers).Append(", отказы ").Append(weekFunnel.Rejected).Append('\n');
        sb.Append("Интервью назначено: ").Append(interviews).Append(" · тренировок: ").Append(mocks).Append(" · историй в банке: ").Append(stories).Append('\n');

        sb.Append("\n<b>Воронка за всё время</b> (").Append(funnel.Sent).Append(" откликов)\n");
        sb.Append("ответили ").Append(Pct(funnel.Replied, funnel.Sent)).Append(" → интервью ").Append(Pct(funnel.Interviews, funnel.Sent))
            .Append(" → оффер ").Append(Pct(funnel.Offers, funnel.Sent)).Append('\n');
        foreach (var group in apps.GroupBy(application => application.Channel).OrderByDescending(group => group.Count()))
        {
            var channelStats = FunnelStats.Of(group);
            sb.Append("• ").Append(WeeklyReportJob.ChannelLabel(group.Key)).Append(": ").Append(channelStats.Sent).Append(" → ответ ").Append(Pct(channelStats.Replied, channelStats.Sent)).Append(", интервью ")
                .Append(Pct(channelStats.Interviews, channelStats.Sent)).Append('\n');
        }

        var byScore = apps.Where(application => application.Vacancy?.Score is not null).GroupBy(application => application.Vacancy!.Score >= 80 ? "≥80" : "60–79").OrderBy(group => group.Key).ToList();
        if (byScore.Count > 1)
            sb.Append("По оценке: ").Append(string.Join(", ", byScore.Select(group => $"{group.Key} → интервью {Pct(FunnelStats.Of(group).Interviews, group.Count())}"))).Append('\n');

        var snapshot = await market.SnapshotAsync(ct);
        var segments = snapshot.Segments.Where(segment => segment.Geo != "*" && segment.Geo != "other").OrderByDescending(segment => segment.Count).Take(5).ToList();
        if (segments.Count > 0)
        {
            sb.Append("\n<b>Рынок</b> (net USD/мес, ").Append(snapshot.Total).Append(" вакансий с зарплатой за 60 дней)\n");
            foreach (var segment in segments)
                sb.Append("• ").Append(MarketSnapshot.SegmentLabel(segment)).Append(": медиана ").Append(MarketSnapshot.Money(segment.P50)).Append(", p75 ")
                    .Append(MarketSnapshot.Money(segment.P75)).Append(" (n=").Append(segment.Count).Append(")\n");
        }

        var (demandText, demandTop, gaps) = await DemandAsync(ctx, now, coverage, ct);
        if (demandTop.Count > 0)
        {
            sb.Append("\n<b>Спрос (ключевые слова подходящих вакансий, 30 дн.)</b>\n").Append(TextUtil.EscapeHtml(string.Join(", ", demandTop.Take(15)))).Append('\n');
            if (gaps.Count > 0) sb.Append("Нет в резюме: ").Append(TextUtil.EscapeHtml(string.Join(", ", gaps.Take(10)))).Append('\n');
        }

        return new()
        {
            HeaderHtml = sb.ToString(), CostHtml = "\nLLM за неделю: $" + spent.ToString("0.##", CultureInfo.InvariantCulture),
            Sent = week.Count, DemandText = demandText, HasDemand = demandTop.Count > 0,
            FunnelText = $"отправлено {funnel.Sent}, ответили {funnel.Replied}, интервью {funnel.Interviews}, офферы {funnel.Offers}, отказы {funnel.Rejected}",
        };
    }

    /// <summary>Keywords of the vacancies that fit (score ≥ 60) over 30 days, with frequency, and the ones the resume does not mention.</summary>
    internal static async Task<(string Text, List<string> Top, List<string> Gaps)> DemandAsync(JobFinderDbContext ctx, DateTimeOffset now, string resume, CancellationToken ct)
    {
        var since = now.AddDays(-30);
        var evaluations = await ctx.Vacancies.AsNoTracking().Where(vacancy => vacancy.FirstSeenAt >= since && vacancy.Score >= 60 && vacancy.Evaluation != null)
            .Select(vacancy => new { vacancy.Evaluation, vacancy.Skills }).ToListAsync(ct);
        var counts = new Dictionary<string, (string Display, int Count)>(StringComparer.OrdinalIgnoreCase);
        foreach (var evaluation in evaluations)
        {
            foreach (var keyword in evaluation.Evaluation!.Keywords.Concat(evaluation.Skills).Select(keyword => keyword.Trim()).Where(keyword => keyword.Length is > 1 and < 40).Distinct(StringComparer.OrdinalIgnoreCase))
                counts[keyword] = counts.TryGetValue(keyword, out var countEntry) ? (countEntry.Display, countEntry.Count + 1) : (keyword, 1);
        }

        var top = counts.Values.Where(countEntry => countEntry.Count >= 2).OrderByDescending(countEntry => countEntry.Count).Take(40).ToList();
        resume = TextUtil.Normalize(resume);
        var gaps = top.Where(countEntry => !resume.Contains(TextUtil.Normalize(countEntry.Display), StringComparison.Ordinal)).Select(countEntry => countEntry.Display).ToList();
        var text = string.Join("\n", top.Select(countEntry => $"{countEntry.Display}: {countEntry.Count}"));
        return (text, top.Select(countEntry => $"{countEntry.Display} ({countEntry.Count})").ToList(), gaps);
    }

    private static string Pct(int part, int total) => total == 0 ? "—" : $"{part} ({100.0 * part / total:0}%)";
}
