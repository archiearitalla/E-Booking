using VeskaFlow.Core.Domain;

namespace VeskaFlow.Core.Scheduling;

public enum ActorKind { Customer, Staff, System }

public readonly record struct PolicyResult(bool Allowed, string? Reason = null)
{
    public static PolicyResult Ok() => new(true);
    public static PolicyResult No(string reason) => new(false, reason);
}

/// <summary>Rules about who may change a booking and when. All times are UTC instants.</summary>
public static class BookingPolicy
{
    public const int NoShowGraceMinutes = 15;

    public static PolicyResult CanCancel(
        BookingStatus status, DateTime startsAtUtc, DateTime nowUtc, ActorKind actor, int cutoffHours)
    {
        if (!BookingStateMachine.CanTransition(status, BookingStatus.Cancelled))
            return PolicyResult.No("This booking can no longer be cancelled.");

        // Staff and the system may cancel any time (the caller records who and why).
        if (actor != ActorKind.Customer)
            return PolicyResult.Ok();

        if (nowUtc >= startsAtUtc)
            return PolicyResult.No("This appointment has already started.");

        if (nowUtc > startsAtUtc.AddHours(-cutoffHours))
            return PolicyResult.No(
                $"Online cancellation closes {cutoffHours} hour(s) before the appointment. Please contact the shop.");

        return PolicyResult.Ok();
    }

    public const string NotToday = "Only today's bookings can be marked. This booking is on another day.";

    /// <summary>
    /// Completed: only from confirmed, only after the start time, and (when the business time zone is given) only on the
    /// day of the appointment: not the day before, not the day after.
    /// </summary>
    public static PolicyResult CanMarkCompleted(BookingStatus status, DateTime startsAtUtc, DateTime nowUtc, string? timeZone = null)
    {
        if (!BookingStateMachine.CanTransition(status, BookingStatus.Completed))
            return PolicyResult.No("Only confirmed bookings can be completed.");
        if (timeZone is not null && !IsToday(startsAtUtc, nowUtc, timeZone))
            return PolicyResult.No(NotToday);
        if (nowUtc < startsAtUtc)
            return PolicyResult.No("The appointment has not started yet.");
        return PolicyResult.Ok();
    }

    /// <summary>No-show: only from confirmed, 15 minutes after the start, and (with a time zone) only on the day of the appointment.</summary>
    public static PolicyResult CanMarkNoShow(BookingStatus status, DateTime startsAtUtc, DateTime nowUtc, string? timeZone = null)
    {
        if (!BookingStateMachine.CanTransition(status, BookingStatus.NoShow))
            return PolicyResult.No("Only confirmed bookings can be marked as no-show.");
        if (timeZone is not null && !IsToday(startsAtUtc, nowUtc, timeZone))
            return PolicyResult.No(NotToday);
        if (nowUtc < startsAtUtc.AddMinutes(NoShowGraceMinutes))
            return PolicyResult.No($"Wait {NoShowGraceMinutes} minutes past the start time before marking a no-show.");
        return PolicyResult.Ok();
    }

    /// <summary>True when the appointment starts on the same calendar day as now, in the business time zone.</summary>
    public static bool IsToday(DateTime startsAtUtc, DateTime nowUtc, string timeZone) =>
        LocalTime.LocalDateOf(startsAtUtc, timeZone) == LocalTime.LocalDateOf(nowUtc, timeZone);
}
