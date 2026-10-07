using System.Globalization;

namespace JobFinder.Pipeline.Jobs;

/// <summary>Local hours a job keeps off a site ("00:30-07:00", may cross midnight): a run due inside them moves to their end.</summary>
public static class QuietHours
{
    public static bool IsValid(string window) => TryParse(window, out _, out _);

    public static DateTimeOffset NextAllowed(DateTimeOffset dueUtc, string? window, TimeZoneInfo zone)
    {
        if (!TryParse(window, out var from, out var to)) return dueUtc;
        var local = TimeZoneInfo.ConvertTime(dueUtc, zone);
        var time = TimeOnly.FromTimeSpan(local.TimeOfDay);
        var inside = from <= to ? time >= from && time < to : time >= from || time < to;
        if (!inside) return dueUtc;
        var endDate = from > to && time >= from ? local.Date.AddDays(1) : local.Date;
        var end = endDate + to.ToTimeSpan();
        return new DateTimeOffset(end, zone.GetUtcOffset(end)).ToUniversalTime();
    }

    private static bool TryParse(string? window, out TimeOnly from, out TimeOnly to)
    {
        from = to = default;
        var parts = (window ?? "").Split('-', 2, StringSplitOptions.TrimEntries);
        return parts.Length == 2 && TimeOnly.TryParse(parts[0], CultureInfo.InvariantCulture, out from) &&
               TimeOnly.TryParse(parts[1], CultureInfo.InvariantCulture, out to) && from != to;
    }
}
