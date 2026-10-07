using System.Text.RegularExpressions;
using JobFinder.Core.Domain;
using JobFinder.Core.Text;

namespace JobFinder.Core.Filtering;

/// <param name="Text">Title + description, as the vacancy gives it.</param>
/// <param name="HasCompany">The employer is named.</param>
/// <param name="Reposts">Times the same opening (company + title) was published anew in the last 90 days.</param>
/// <param name="DaysSinceCreated">Age of the original posting (hh creationTime), when known.</param>
/// <param name="ResponsesCount">Applications it already has (hh), when known.</param>
/// <param name="SalaryToMarketP90">Offer / market p90 of the segment, when both are known.</param>
/// <param name="LlmVerdict">ok / suspicious / scam from the scoring model.</param>
public sealed record LegitimacyInput(
    string Text,
    bool HasCompany,
    int Reposts,
    int? DaysSinceCreated,
    int? ResponsesCount,
    decimal? SalaryToMarketP90,
    string LlmVerdict,
    IReadOnlyList<string> LlmReasons);

public readonly record struct LegitimacyVerdict(Legitimacy Level, List<string> Reasons);

/// <summary>
/// Ghost jobs and scams: code-checked signals (reposts, age, a salary far above the market, scam phrases) combined with the model's
/// reading of the text. The strongest level wins; every reason is kept so the card can say why.
/// </summary>
public static partial class LegitimacyRules
{
    public const int RepostThreshold = 3;
    public const int StaleDays = 45;
    public const decimal SalaryAnomaly = 1.5m;

    public static LegitimacyVerdict Evaluate(LegitimacyInput input)
    {
        var level = Legitimacy.Ok;
        var reasons = new List<string>();

        void Raise(Legitimacy to, string reason)
        {
            if (to > level) level = to;
            reasons.Add(reason);
        }

        var text = TextUtil.Normalize(input.Text);
        var scam = ScamPhrases().Match(text);
        if (scam.Success) Raise(Legitimacy.Scam, $"признак скама: «{scam.Value}»");

        switch (input.LlmVerdict)
        {
            case "scam":
                Raise(Legitimacy.Scam, "модель: " + string.Join("; ", input.LlmReasons.DefaultIfEmpty("признаки мошенничества")));
                break;
            case "suspicious":
                Raise(Legitimacy.Suspicious, "модель: " + string.Join("; ", input.LlmReasons.DefaultIfEmpty("сомнительная вакансия")));
                break;
        }

        if (input.Reposts >= RepostThreshold) Raise(Legitimacy.Suspicious, $"перепубликуется: {input.Reposts} раз за 90 дней");
        if (input.DaysSinceCreated >= StaleDays)
        {
            var crowd = input.ResponsesCount is > 500 ? $", {input.ResponsesCount} откликов" : "";
            Raise(Legitimacy.Suspicious, $"открыта {input.DaysSinceCreated} дней{crowd} — похоже на вечную вакансию");
        }

        if (input.SalaryToMarketP90 >= SalaryAnomaly)
            Raise(Legitimacy.Suspicious, $"вилка в {input.SalaryToMarketP90:0.#} раза выше 90-го перцентиля рынка");
        if (!input.HasCompany) Raise(Legitimacy.Suspicious, "компания не названа");

        return new LegitimacyVerdict(reasons.Count == 0 ? Legitimacy.Ok : level, reasons);
    }

    [GeneratedRegex(@"обучени[ея] за (свой|ваш|собственный) сч[её]т|оплат\w* (обучени|курс|стажировк)|(стартов|первоначальн)\w* взнос|пассивн\w* доход|криптовалют\w* (заработ|доход)|работа в (тво[её]м|вашем) городе|без вложений.{0,20}доход|доход от \d+ ?(тыс|к) в (день|неделю)")]
    private static partial Regex ScamPhrases();
}
