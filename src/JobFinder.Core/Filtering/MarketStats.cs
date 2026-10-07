using System.Globalization;
using System.Text.RegularExpressions;
using JobFinder.Core.Domain;
using JobFinder.Core.Text;

namespace JobFinder.Core.Filtering;

/// <summary>One salary observation: a vacancy's pay as monthly net USD, with the segment it belongs to.</summary>
public readonly record struct SalaryPoint(string Seniority, string Geo, decimal NetUsd);

public sealed record SegmentStats(string Seniority, string Geo, int Count, decimal P25, decimal P50, decimal P75, decimal P90, decimal[] Sorted)
{
    /// <summary>Share of observations at or below <paramref name="value"/>, 0..100.</summary>
    public int PercentileOf(decimal value)
    {
        var below = Sorted.Count(s => s <= value);
        return (int)Math.Round(100m * below / Sorted.Length);
    }
}

/// <summary>
/// The salary market as the system itself has seen it: every collected vacancy with a salary (whatever its fit), bucketed by seniority
/// and by where the job is. Numbers the model cannot invent, recomputed from the database.
/// </summary>
public sealed partial class MarketSnapshot
{
    public const int MinCount = 5;
    private readonly Dictionary<(string, string), SegmentStats> _segments;

    private MarketSnapshot(Dictionary<(string, string), SegmentStats> segments, int total, DateTimeOffset builtAt)
    {
        _segments = segments;
        Total = total;
        BuiltAt = builtAt;
    }

    public int Total { get; }

    public DateTimeOffset BuiltAt { get; }

    public IEnumerable<SegmentStats> Segments => _segments.Values;

    public static MarketSnapshot Build(IEnumerable<SalaryPoint> points, DateTimeOffset now)
    {
        var list = points.Where(p => p.NetUsd > 100 && p.NetUsd < 50_000).ToList();
        var segments = new Dictionary<(string, string), SegmentStats>();
        // Exact segments plus the "*" roll-ups used when an exact segment is too thin.
        foreach (var group in list.GroupBy(p => (p.Seniority, p.Geo))
                     .Concat(list.GroupBy(p => (p.Seniority, "*")))
                     .Concat(list.GroupBy(p => ("*", p.Geo)))
                     .Concat(list.GroupBy(_ => ("*", "*"))))
        {
            var sorted = group.Select(p => p.NetUsd).Order().ToArray();
            if (sorted.Length == 0) continue;
            segments[group.Key] = new SegmentStats(group.Key.Item1, group.Key.Item2, sorted.Length,
                Quantile(sorted, 0.25m), Quantile(sorted, 0.5m), Quantile(sorted, 0.75m), Quantile(sorted, 0.9m), sorted);
        }

        return new MarketSnapshot(segments, list.Count, now);
    }

    /// <summary>The most specific segment with enough data: exact → same seniority anywhere → same place any seniority → everything.</summary>
    public SegmentStats? Lookup(string seniority, string geo)
    {
        foreach (var key in new[] { (seniority, geo), (seniority, "*"), ("*", geo), ("*", "*") })
        {
            if (_segments.TryGetValue(key, out var s) && s.Count >= MinCount) return s;
        }

        return null;
    }

    public static string SeniorityOf(string title)
    {
        var t = TextUtil.Normalize(title);
        if (SeniorWords().IsMatch(t)) return "senior+";
        if (t.Contains("middle") || t.Contains("мидл")) return "middle";
        if (t.Contains("junior") || t.Contains("джун")) return "junior";
        return "any";
    }

    public static string GeoOf(Vacancy v)
    {
        if (v.WorkFormats.HasFlag(WorkFormat.Remote)) return v.CountryCode == "BY" ? "remote-by" : "remote-ru";
        var city = TextUtil.Normalize(v.City);
        return city is "минск" or "minsk" ? "minsk" : "other";
    }

    public static string SegmentLabel(SegmentStats s)
    {
        var seniority = s.Seniority switch
        {
            "senior+" => "Senior+ .NET",
            "middle" => "Middle .NET",
            "junior" => "Junior .NET",
            _ => ".NET",
        };
        var geo = s.Geo switch
        {
            "remote-by" => "удалёнка РБ",
            "remote-ru" => "удалёнка РФ",
            "minsk" => "Минск офис/гибрид",
            "other" => "офис вне Минска",
            _ => "все форматы",
        };
        return $"{seniority}, {geo}";
    }

    public static string Money(decimal usd) => "$" + Math.Round(usd, 0).ToString("#,0", CultureInfo.GetCultureInfo("ru-RU"));

    // Word starts, not substrings: "outstaff" is not "staff", "валидация" is not "лид"; team/tech leads are spelled as one word.
    [GeneratedRegex(@"(?<![\p{L}\p{N}])(senior|sr\b|старш|сеньор|синьор|ведущ|lead|лид|architect|архитект|principal|staff|head)|teamlead|тимлид|techlead|техлид")]
    private static partial Regex SeniorWords();

    private static decimal Quantile(decimal[] sorted, decimal q)
    {
        if (sorted.Length == 1) return sorted[0];
        var pos = q * (sorted.Length - 1);
        var lower = (int)Math.Floor(pos);
        var upper = Math.Min(lower + 1, sorted.Length - 1);
        return sorted[lower] + (sorted[upper] - sorted[lower]) * (pos - lower);
    }
}
