namespace JobFinder.Core.Time;

/// <summary>
/// "Today", working hours and digest times are counted in the user's zone (Europe/Minsk), not in UTC and not in the container's zone.
/// </summary>
public sealed class LocalClock(TimeProvider time, string timeZoneId)
{
    public TimeZoneInfo Zone { get; } = TimeZoneInfo.FindSystemTimeZoneById(timeZoneId);

    public DateTimeOffset UtcNow => time.GetUtcNow();

    public DateTimeOffset Now => TimeZoneInfo.ConvertTime(time.GetUtcNow(), Zone);

    /// <summary>The UTC instant the current local day started at.</summary>
    public DateTimeOffset StartOfTodayUtc()
    {
        var now = Now;
        var midnight = new DateTimeOffset(now.Year, now.Month, now.Day, 0, 0, 0, now.Offset);
        return midnight.ToUniversalTime();
    }

    /// <summary>The next UTC instant the local clock shows <paramref name="at"/> (today if still ahead, else tomorrow).</summary>
    public DateTimeOffset NextOccurrenceUtc(TimeOnly at)
    {
        var now = Now;
        var candidate = new DateTime(now.Year, now.Month, now.Day, at.Hour, at.Minute, 0, DateTimeKind.Unspecified);
        if (candidate <= now.DateTime) candidate = candidate.AddDays(1);
        return new DateTimeOffset(candidate, Zone.GetUtcOffset(candidate)).ToUniversalTime();
    }
}
