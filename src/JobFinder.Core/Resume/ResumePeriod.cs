using System.Globalization;
using System.Text.RegularExpressions;

namespace JobFinder.Core.Resume;

/// <summary>
/// Work periods in one format for every entry, the way ATS parsers read them best (month name and year, an en dash between):
/// «Ноябрь 2023 – Июль 2026», «Ноябрь 2023 – по настоящее время»; in English «Nov 2023 – Jul 2026», «Nov 2023 – Present». Reads what
/// resumes and models write: «Ноя 2023 — Июл 2026 · 2 года 9 мес», «11.2023 - 07.2026», «ноябрь 2023 – н.в.», «2021 — 2022».
/// </summary>
public static partial class ResumePeriod
{
    /// <param name="Number">1-12; 0 when only the year is known.</param>
    public readonly record struct Month(int Year, int Number) : IComparable<Month>
    {
        public int CompareTo(Month other) => (Year, Number).CompareTo((other.Year, other.Number));
    }

    /// <param name="End">null = to the present day.</param>
    public sealed record Range(Month Start, Month? End);

    private static readonly string[] Ru =
        ["Январь", "Февраль", "Март", "Апрель", "Май", "Июнь", "Июль", "Август", "Сентябрь", "Октябрь", "Ноябрь", "Декабрь"];

    private static readonly string[] En = ["Jan", "Feb", "Mar", "Apr", "May", "Jun", "Jul", "Aug", "Sep", "Oct", "Nov", "Dec"];

    private static readonly (string Prefix, int Number)[] Names =
    [
        ("янв", 1), ("фев", 2), ("мар", 3), ("апр", 4), ("май", 5), ("мая", 5), ("июн", 6), ("июл", 7), ("авг", 8), ("сен", 9), ("окт", 10),
        ("ноя", 11), ("дек", 12), ("jan", 1), ("feb", 2), ("mar", 3), ("apr", 4), ("may", 5), ("jun", 6), ("jul", 7), ("aug", 8), ("sep", 9),
        ("oct", 10), ("nov", 11), ("dec", 12),
    ];

    public static Range? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        // "Ноя 2023 — Июл 2026 · 2 года 9 мес": the duration after the dot or in brackets is not part of the range.
        var range = text.Split('·', '(')[0];
        var dates = new List<Month>();
        foreach (Match m in Date().Matches(range))
        {
            var year = int.Parse(m.Groups["year"].Value, CultureInfo.InvariantCulture);
            var month = m.Groups["num"].Success ? int.Parse(m.Groups["num"].Value, CultureInfo.InvariantCulture)
                : m.Groups["name"].Success ? MonthNumber(m.Groups["name"].Value)
                : 0;
            if (month is < 0 or > 12) return null;
            dates.Add(new Month(year, month));
        }

        if (dates.Count == 0) return null;
        var present = Present().IsMatch(range);
        return dates.Count >= 2 && !present ? new Range(dates[0], dates[1]) : new Range(dates[0], present ? null : dates.Count == 1 ? dates[0] : dates[1]);
    }

    public static string Format(Range r, bool en)
    {
        var start = Format(r.Start, en);
        if (r.End == r.Start) return start;
        var end = r.End is { } e ? Format(e, en) : en ? "Present" : "по настоящее время";
        return $"{start} – {end}";
    }

    /// <summary>The period in the one format, or as it was when it cannot be read.</summary>
    public static string Normalize(string? period, bool en) => Parse(period) is { } r ? Format(r, en) : (period ?? "").Trim();

    /// <summary>Latest first: the current job, then by end, then by start.</summary>
    public static IComparer<Range?> Recent { get; } = Comparer<Range?>.Create((a, b) =>
    {
        if (a is null || b is null) return a is null ? (b is null ? 0 : 1) : -1;
        var byEnd = (b.End ?? new Month(9999, 12)).CompareTo(a.End ?? new Month(9999, 12));
        return byEnd != 0 ? byEnd : b.Start.CompareTo(a.Start);
    });

    private static string Format(Month m, bool en) =>
        m.Number == 0 ? m.Year.ToString(CultureInfo.InvariantCulture) : $"{(en ? En : Ru)[m.Number - 1]} {m.Year.ToString(CultureInfo.InvariantCulture)}";

    private static int MonthNumber(string name)
    {
        var n = name.ToLowerInvariant().TrimEnd('.');
        foreach (var (prefix, number) in Names)
            if (n.StartsWith(prefix, StringComparison.Ordinal))
                return number;
        // "since 2021", "с 2021": a word that is not a month leaves the year alone.
        return 0;
    }

    [GeneratedRegex(@"(?:(?<name>\p{L}{3,9}\.?)\s+|(?<num>\d{1,2})[./])?(?<year>(?:19|20)\d{2})(?!\d)")]
    private static partial Regex Date();

    [GeneratedRegex(@"настоящ\p{L}*|н\.\s?в\.|сейчас|по н\.в|present|now|current", RegexOptions.IgnoreCase)]
    private static partial Regex Present();
}
