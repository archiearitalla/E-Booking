namespace VeskaFlow.Core.Scheduling;

/// <summary>A daily open window in the location's local time. Close &lt;= Open means it crosses midnight.</summary>
public sealed record WorkWindow(TimeOnly Open, TimeOnly Close);

/// <summary>A half-open UTC interval [StartUtc, EndUtc).</summary>
public sealed record TimeRange(DateTime StartUtc, DateTime EndUtc)
{
    public bool Overlaps(DateTime startUtc, DateTime endUtc) => StartUtc < endUtc && startUtc < EndUtc;
}

/// <summary>Everything needed to derive availability for ONE staff member on ONE local date.</summary>
public sealed record AvailabilityRequest(
    string TimeZoneId,
    DateOnly LocalDate,
    IReadOnlyList<WorkWindow> Windows,
    int DurationMinutes,
    int BufferMinutes,
    int SlotMinutes,
    IReadOnlyList<TimeRange> Occupied,   // active bookings (range already includes their buffer)
    IReadOnlyList<TimeRange> Blackouts,  // shop-wide or staff-specific closures
    DateTime NowUtc,
    int LeadTimeMinutes,
    int HorizonDays);

/// <summary>
/// Availability is derived, never stored. A start time is free iff it is inside a working window,
/// outside blackouts, clear of existing bookings (including buffers), after the lead time and within the horizon.
/// The same function is used to render slots AND to re-validate at confirmation time.
/// </summary>
public static class AvailabilityCalculator
{
    public static IReadOnlyList<DateTime> FreeStarts(AvailabilityRequest r)
    {
        if (r.DurationMinutes <= 0) throw new ArgumentOutOfRangeException(nameof(r), "Duration must be positive.");
        if (r.SlotMinutes <= 0) throw new ArgumentOutOfRangeException(nameof(r), "Slot size must be positive.");
        if (r.BufferMinutes < 0 || r.LeadTimeMinutes < 0 || r.HorizonDays < 0)
            throw new ArgumentOutOfRangeException(nameof(r), "Buffer, lead time and horizon cannot be negative.");

        var tz = LocalTime.Zone(r.TimeZoneId);
        var duration = TimeSpan.FromMinutes(r.DurationMinutes);
        var occupiedSpan = TimeSpan.FromMinutes(r.DurationMinutes + r.BufferMinutes);
        var step = TimeSpan.FromMinutes(r.SlotMinutes);
        var earliest = LocalTime.AsUtc(r.NowUtc).AddMinutes(r.LeadTimeMinutes);
        var latest = LocalTime.AsUtc(r.NowUtc).AddDays(r.HorizonDays);

        var result = new SortedSet<DateTime>();

        foreach (var window in r.Windows)
        {
            if (window.Open == window.Close) continue;

            var openLocal = r.LocalDate.ToDateTime(window.Open);
            var closeDate = window.Close > window.Open ? r.LocalDate : r.LocalDate.AddDays(1);
            var closeLocal = closeDate.ToDateTime(window.Close);

            // Generate in UTC so DST transitions never skip or duplicate a slot.
            var openUtc = LocalTime.ToUtc(openLocal, tz);
            var closeUtc = LocalTime.ToUtc(closeLocal, tz);

            for (var start = openUtc; start + duration <= closeUtc; start += step)
            {
                if (start < earliest || start > latest) continue;

                var end = start + duration;
                var occupiedEnd = start + occupiedSpan;

                if (r.Blackouts.Any(b => b.Overlaps(start, end))) continue;
                if (r.Occupied.Any(o => o.Overlaps(start, occupiedEnd))) continue;

                result.Add(start);
            }
        }

        return result.ToList();
    }

    public static bool IsStartFree(AvailabilityRequest r, DateTime startUtc) =>
        FreeStarts(r).Contains(LocalTime.AsUtc(startUtc));
}
