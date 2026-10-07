using System.Globalization;
using JobFinder.Core.Domain;
using JobFinder.Core.Options;
using JobFinder.Core.Profile;
using JobFinder.Core.Text;

namespace JobFinder.Core.Filtering;

public readonly record struct SalaryVerdict(int Adjustment, string Note);

/// <summary>
/// Salary is a soft factor: a known range below expectations lowers the score, above raises it a little, unknown changes nothing.
/// Computed in code (not by the LLM): currency math and gross/net are exactly the things a model gets wrong.
/// </summary>
public sealed class SalaryEvaluator(SalaryOptions options)
{
    public SalaryVerdict Evaluate(Vacancy vacancy, DesiredSalary? desired)
    {
        if (desired is null || desired.Amount <= 0) return new(0, "ожидания по зарплате не заданы в профиле");

        var top = vacancy.SalaryTo ?? vacancy.SalaryFrom;
        if (top is null or <= 0) return new(0, "вилка не указана");

        var currency = Currency.Normalize(vacancy.Currency) ?? "RUB";
        if (!TryToUsd(top.Value, currency, out var offerUsd)) return new(0, $"нет курса для {currency}");
        if (vacancy.SalaryGross == true) offerUsd *= options.NetShareOfGross;

        var desiredCurrency = Currency.Normalize(desired.Currency) ?? "USD";
        if (!TryToUsd(desired.Amount, desiredCurrency, out var desiredUsd)) return new(0, $"нет курса для {desiredCurrency}");
        if (!desired.Net) desiredUsd *= options.NetShareOfGross;

        var ratio = offerUsd / desiredUsd;
        int adjustment;
        if (ratio >= 1m)
        {
            adjustment = (int)Math.Min(options.MaxBonus, Math.Round((ratio - 1m) * 20m));
        }
        else
        {
            adjustment = -(int)Math.Min(options.MaxPenalty, Math.Round((1m - ratio) * 60m));
        }

        var bound = vacancy.SalaryTo is not null ? "до" : "от";
        var gross = vacancy.SalaryGross == true ? " gross" : "";
        var note = string.Create(CultureInfo.InvariantCulture,
            $"{bound} {top.Value:#,0} {currency}{gross} ≈ {offerUsd:0} USD net, {ratio:P0} от ожиданий → {adjustment:+0;-0;0}");
        return new(adjustment, note);
    }

    /// <summary>A vacancy salary point as monthly net USD (null when the currency has no rate): the unit market statistics use.</summary>
    public decimal? ToMonthlyNetUsd(decimal amount, string? currency, bool? gross)
    {
        if (!TryToUsd(amount, Currency.Normalize(currency) ?? "RUB", out var usd)) return null;
        return gross == true ? usd * options.NetShareOfGross : usd;
    }

    private bool TryToUsd(decimal amount, string currency, out decimal usd)
    {
        if (currency == "USD")
        {
            usd = amount;
            return true;
        }

        if (options.RatesToUsd.TryGetValue(currency, out var rate) && rate > 0)
        {
            usd = amount * rate;
            return true;
        }

        usd = 0;
        return false;
    }
}
