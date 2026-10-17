using BarberBook.Core.Domain;

namespace BarberBook.Core.Scheduling;

public sealed class InvalidBookingTransitionException(BookingStatus from, BookingStatus to)
    : InvalidOperationException($"Illegal booking transition {from.ToDb()} -> {to.ToDb()}.");

/// <summary>
/// Explicit booking lifecycle. Cancelling or expiring a booking frees its slot because the database
/// exclusion constraint only considers HELD/PENDING/CONFIRMED rows.
/// </summary>
public static class BookingStateMachine
{
    private static readonly Dictionary<BookingStatus, BookingStatus[]> Allowed = new()
    {
        [BookingStatus.Held] = [BookingStatus.Pending, BookingStatus.Confirmed, BookingStatus.Cancelled],
        [BookingStatus.Pending] = [BookingStatus.Confirmed, BookingStatus.Cancelled],
        [BookingStatus.Confirmed] = [BookingStatus.Completed, BookingStatus.Cancelled, BookingStatus.NoShow],
        [BookingStatus.Completed] = [],
        [BookingStatus.Cancelled] = [],
        [BookingStatus.NoShow] = []
    };

    public static bool CanTransition(BookingStatus from, BookingStatus to) =>
        Allowed.TryGetValue(from, out var next) && next.Contains(to);

    public static void EnsureCanTransition(BookingStatus from, BookingStatus to)
    {
        if (!CanTransition(from, to))
            throw new InvalidBookingTransitionException(from, to);
    }
}
