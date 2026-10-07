using System.Globalization;
using System.Text.RegularExpressions;
using JobFinder.Core.Text;

namespace JobFinder.Core.Time;

/// <summary>
/// "завтра 15:00", "пт 11:30", "28.09 14:00", "28.09.2026 9:30", "14.00", "завтра в 15" — the ways a person types an interview time in a chat, read in the
/// user's zone. Anything else is not guessed.
/// </summary>
public static partial class LocalDateParser
{
    private static readonly Dictionary<string, DayOfWeek> Days = new(StringComparer.Ordinal)
    {
        ["пн"] = DayOfWeek.Monday, ["понедельник"] = DayOfWeek.Monday, ["вт"] = DayOfWeek.Tuesday, ["вторник"] = DayOfWeek.Tuesday,
        ["ср"] = DayOfWeek.Wednesday, ["среда"] = DayOfWeek.Wednesday, ["среду"] = DayOfWeek.Wednesday, ["чт"] = DayOfWeek.Thursday,
        ["четверг"] = DayOfWeek.Thursday, ["пт"] = DayOfWeek.Friday, ["пятница"] = DayOfWeek.Friday, ["пятницу"] = DayOfWeek.Friday,
        ["сб"] = DayOfWeek.Saturday, ["суббота"] = DayOfWeek.Saturday, ["субботу"] = DayOfWeek.Saturday, ["вс"] = DayOfWeek.Sunday,
        ["воскресенье"] = DayOfWeek.Sunday,
    };

    public static bool TryParse(string input, DateTimeOffset nowLocal, TimeZoneInfo zone, out DateTimeOffset result)
    {
        result = default;
        var text = TextUtil.Normalize(input);

        // A date "dd.mm[.yyyy]" only counts when it is a real date ("14.00" is a time, not the 14th of month 0).
        var dm = DayMonth().Match(text);
        DateTime? explicitDate = null;
        if (dm.Success)
        {
            var day = int.Parse(dm.Groups["d"].Value, CultureInfo.InvariantCulture);
            var month = int.Parse(dm.Groups["mo"].Value, CultureInfo.InvariantCulture);
            var year = dm.Groups["y"].Success ? int.Parse(dm.Groups["y"].Value, CultureInfo.InvariantCulture) : nowLocal.Year;
            if (year < 100) year += 2000;
            if (month is >= 1 and <= 12 && day >= 1 && day <= DateTime.DaysInMonth(year, month))
            {
                var date = new DateTime(year, month, day);
                if (!dm.Groups["y"].Success && date < nowLocal.Date.AddDays(-1)) date = date.AddYears(1);
                explicitDate = date;
            }
        }

        var time = ColonTime().Match(text);
        if (!time.Success)
            time = DotTime().Matches(text).LastOrDefault(m => explicitDate is null || m.Index != dm.Index) ?? Match.Empty;
        if (!time.Success) time = HourOnly().Match(text);
        if (!time.Success) return false;
        var hour = int.Parse(time.Groups["h"].Value, CultureInfo.InvariantCulture);
        var minute = time.Groups["m"].Success ? int.Parse(time.Groups["m"].Value, CultureInfo.InvariantCulture) : 0;
        if (hour > 23 || minute > 59) return false;
        var clock = new TimeSpan(hour, minute, 0);

        DateTime day0;
        if (explicitDate is { } d) day0 = d;
        else if (text.Contains("послезавтра")) day0 = nowLocal.Date.AddDays(2);
        else if (text.Contains("завтра")) day0 = nowLocal.Date.AddDays(1);
        else if (text.Contains("сегодня")) day0 = nowLocal.Date;
        else if (Days.Where(kv => TextUtil.WholeWord(kv.Key).IsMatch(text)).Select(kv => (DayOfWeek?)kv.Value).FirstOrDefault() is { } weekday)
        {
            var delta = ((int)weekday - (int)nowLocal.DayOfWeek + 7) % 7;
            day0 = nowLocal.Date.AddDays(delta == 0 && nowLocal.TimeOfDay > clock ? 7 : delta);
        }
        else day0 = nowLocal.TimeOfDay > clock ? nowLocal.Date.AddDays(1) : nowLocal.Date;

        var local = day0 + clock;
        result = new DateTimeOffset(local, zone.GetUtcOffset(local));
        return true;
    }

    [GeneratedRegex(@"(?<![\d])(?<h>\d{1,2}):(?<m>\d{2})(?![\d])")]
    private static partial Regex ColonTime();

    [GeneratedRegex(@"(?<![\d.])(?<h>\d{1,2})\.(?<m>\d{2})(?![\d.])")]
    private static partial Regex DotTime();

    [GeneratedRegex(@"(?<![\d:.])(?<d>\d{1,2})[./](?<mo>\d{1,2})(?:[./](?<y>\d{2,4}))?(?![\d:])")]
    private static partial Regex DayMonth();

    /// <summary>"в 15", "15 ч", "в 9 утра": a whole hour.</summary>
    [GeneratedRegex(@"(?:(?<=(?:^|\s)в\s)(?<h>\d{1,2})(?![\d:./])|(?<![\d:.])(?<h>\d{1,2})\s?(?:ч\b|час))")]
    private static partial Regex HourOnly();
}
