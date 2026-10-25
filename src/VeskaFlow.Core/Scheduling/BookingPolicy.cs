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

    public static PolicyResult CanMarkCompleted(BookingStatus status, DateTime startsAtUtc, DateTime nowUtc)
    {
        if (!BookingStateMachine.CanTransition(status, BookingStatus.Completed))
            return PolicyResult.No("Only confirmed bookings can be completed.");
        if (nowUtc < startsAtUtc)
            return PolicyResult.No("The appointment has not started yet.");
        return PolicyResult.Ok();
    }

    public static PolicyResult CanMarkNoShow(BookingStatus status, DateTime startsAtUtc, DateTime nowUtc)
    {
        if (!BookingStateMachine.CanTransition(status, BookingStatus.NoShow))
            return PolicyResult.No("Only confirmed bookings can be marked as no-show.");
        if (nowUtc < startsAtUtc.AddMinutes(NoShowGraceMinutes))
            return PolicyResult.No($"Wait {NoShowGraceMinutes} minutes past the start time before marking a no-show.");
        return PolicyResult.Ok();
    }
}
