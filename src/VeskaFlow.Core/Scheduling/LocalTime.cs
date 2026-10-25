namespace VeskaFlow.Core.Scheduling;

/// <summary>Timezone helpers. Instants are always UTC; the location's IANA zone is stored separately.</summary>
public static class LocalTime
{
    public static TimeZoneInfo Zone(string ianaId) => TimeZoneInfo.FindSystemTimeZoneById(ianaId);

    public static bool IsValidZone(string ianaId)
    {
        try { TimeZoneInfo.FindSystemTimeZoneById(ianaId); return true; }
        catch (Exception e) when (e is TimeZoneNotFoundException or InvalidTimeZoneException) { return false; }
    }

    public static DateTime ToLocal(DateTime utc, string ianaId) =>
        TimeZoneInfo.ConvertTimeFromUtc(AsUtc(utc), Zone(ianaId));

    /// <summary>Converts a wall-clock time to UTC. A time that does not exist (DST gap) moves forward to the first valid time.</summary>
    public static DateTime ToUtc(DateTime localWallClock, TimeZoneInfo tz)
    {
        var local = DateTime.SpecifyKind(localWallClock, DateTimeKind.Unspecified);
        while (tz.IsInvalidTime(local))
            local = local.AddMinutes(15);
        return TimeZoneInfo.ConvertTimeToUtc(local, tz);
    }

    /// <summary>[start, end) in UTC of one local calendar day. Handles 23h/25h DST days.</summary>
    public static (DateTime StartUtc, DateTime EndUtc) DayRangeUtc(DateOnly localDate, string ianaId)
    {
        var tz = Zone(ianaId);
        return (ToUtc(localDate.ToDateTime(TimeOnly.MinValue), tz),
                ToUtc(localDate.AddDays(1).ToDateTime(TimeOnly.MinValue), tz));
    }

    public static DateOnly LocalDateOf(DateTime utc, string ianaId) =>
        DateOnly.FromDateTime(ToLocal(utc, ianaId));

    public static DateTime AsUtc(DateTime value) => value.Kind switch
    {
        DateTimeKind.Utc => value,
        DateTimeKind.Local => value.ToUniversalTime(),
        _ => DateTime.SpecifyKind(value, DateTimeKind.Utc)
    };
}
