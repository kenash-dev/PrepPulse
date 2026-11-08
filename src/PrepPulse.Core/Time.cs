namespace PrepPulse.Core;

/// <summary>Source of "now". Injected so the engine and reports can be tested with a fake clock.</summary>
public interface IClock
{
    DateTimeOffset Now { get; }
    TimeZoneInfo Zone { get; }
}

public sealed class SystemClock : IClock
{
    public DateTimeOffset Now => DateTimeOffset.Now;
    public TimeZoneInfo Zone => TimeZoneInfo.Local;
}

/// <summary>Helpers for local calendar days. All storage is UTC epoch milliseconds; days are local.</summary>
public static class LocalTime
{
    public static DateOnly DateOf(DateTimeOffset t, TimeZoneInfo zone) =>
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(t, zone).DateTime);

    public static DateTimeOffset StartOfDay(DateOnly day, TimeZoneInfo zone)
    {
        var local = day.ToDateTime(TimeOnly.MinValue, DateTimeKind.Unspecified);
        return new DateTimeOffset(local, zone.GetUtcOffset(local));
    }

    public static DateTimeOffset EndOfDay(DateOnly day, TimeZoneInfo zone) => StartOfDay(day.AddDays(1), zone);

    public static double MinutesIntoDay(DateTimeOffset t, DateOnly day, TimeZoneInfo zone) =>
        (t - StartOfDay(day, zone)).TotalMinutes;

    public static TimeSpan Overlap(DateTimeOffset aStart, DateTimeOffset aEnd, DateTimeOffset bStart, DateTimeOffset bEnd)
    {
        var s = aStart > bStart ? aStart : bStart;
        var e = aEnd < bEnd ? aEnd : bEnd;
        return e > s ? e - s : TimeSpan.Zero;
    }

    public static TimeSpan OverlapDay(DateTimeOffset start, DateTimeOffset end, DateOnly day, TimeZoneInfo zone) =>
        Overlap(start, end, StartOfDay(day, zone), EndOfDay(day, zone));

    /// <summary>Monday of the week containing <paramref name="d"/>.</summary>
    public static DateOnly WeekStart(DateOnly d) => d.AddDays(-(((int)d.DayOfWeek + 6) % 7));

    public static long ToMs(DateTimeOffset t) => t.ToUnixTimeMilliseconds();

    public static DateTimeOffset FromMs(long ms, TimeZoneInfo zone) =>
        TimeZoneInfo.ConvertTime(DateTimeOffset.FromUnixTimeMilliseconds(ms), zone);
}
