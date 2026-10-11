namespace FreedomBlaze.Services;

/// <summary>The news edition's calendar and daily refresh time, independent of the host's time zone.</summary>
public static class BitcoinNewsSchedule
{
    public static TimeZoneInfo TimeZone { get; } = TimeZoneInfo.CreateCustomTimeZone(
        "BitcoinNews.UTC-03", TimeSpan.FromHours(-3), "Bitcoin news (UTC-03:00)", "UTC-03:00");

    public static DateOnly Today(DateTimeOffset utcNow) =>
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(utcNow, TimeZone).DateTime);

    /// <summary>
    /// Returns the first 08:00 UTC-03:00 strictly after the supplied instant. Startup at or after
    /// 08:00 handles today's edition once and schedules its next daily refresh for tomorrow.
    /// </summary>
    public static DateTimeOffset GetNextRunUtc(DateTimeOffset utcNow)
    {
        var localNow = TimeZoneInfo.ConvertTime(utcNow, TimeZone);
        var nextRun = new DateTimeOffset(localNow.Date.AddHours(8), TimeZone.BaseUtcOffset).ToUniversalTime();
        return nextRun > utcNow ? nextRun : nextRun.AddDays(1);
    }
}
