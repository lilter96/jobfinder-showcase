using System.Globalization;
using System.Text;
using JobFinder.Core.Filtering;
using JobFinder.Core.Text;
using JobFinder.Pipeline.Career;
using JobFinder.Pipeline.Notifications;

namespace JobFinder.Pipeline.Modules.Hiring;

public static class OfferPresentation
{
    public const string Version = "v1";

    public static BotReply Render(OfferPreparationInput input, OfferPreparationResult result, long? applicationId)
    {
        var a = result.Analysis;
        var netUsd = result.MonthlyNetUsd;
        var segment = input.Market;

        var sb = new StringBuilder("🏆 <b>Оффер: ").Append(TextUtil.EscapeHtml(a.Company)).Append("</b>");
        if (a.MonthlyAmount is { } m)
            sb.Append("\n💰 ").Append(m.ToString("#,0", CultureInfo.GetCultureInfo("ru-RU"))).Append(' ').Append(TextUtil.EscapeHtml(a.Currency ?? ""))
                .Append(a.Gross == true ? " gross" : a.Gross == false ? " net" : "");
        if (netUsd is { } n)
        {
            sb.Append(" ≈ ").Append(MarketSnapshot.Money(n)).Append(" net");
            if (segment?.PercentileOf(n) is { } percentile) sb.Append(" — p").Append(percentile).Append(" рынка");
        }

        sb.Append("\n\n").Append(TextUtil.EscapeHtml(a.Verdict)).Append('\n');
        List("Условия", a.Terms.Select(t => $"{t.Name}: {t.Value}"));
        List("Сильные стороны", a.Strengths);
        List("Риски", a.Risks);
        List("Уточнить до подписания", a.QuestionsToClarify);
        sb.Append("\n🎯 <b>Цель:</b> ").Append(TextUtil.EscapeHtml(a.CounterTarget)).Append("\n🚪 <b>Нижняя граница:</b> ").Append(TextUtil.EscapeHtml(a.WalkAway));
        sb.Append("\n\n<b>Контрпредложение (скопируйте):</b>\n<code>").Append(TextUtil.EscapeHtml(a.CounterScript)).Append("</code>");
        sb.Append("\n\n<i>Не юридическая консультация: договор стоит показать юристу.</i>");
        return new BotReply(CareerCards.Truncate(sb.ToString()), applicationId is null ? null : [CareerCards.StageRow(applicationId.Value)]);

        void List(string title, IEnumerable<string> items)
        {
            var list = items.ToList();
            if (list.Count == 0) return;
            sb.Append("\n<b>").Append(title).Append(":</b>\n");
            foreach (var i in list) sb.Append("• ").Append(TextUtil.EscapeHtml(i)).Append('\n');
        }
    }
}
