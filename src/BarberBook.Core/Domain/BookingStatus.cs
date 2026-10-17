namespace BarberBook.Core.Domain;

/// <summary>Booking lifecycle. See BookingStateMachine for legal transitions.</summary>
public enum BookingStatus
{
    Held,
    Pending,
    Confirmed,
    Completed,
    Cancelled,
    NoShow
}

public static class BookingStatusExtensions
{
    /// <summary>HELD, PENDING and CONFIRMED bookings occupy their slot.</summary>
    public static bool OccupiesSlot(this BookingStatus s) =>
        s is BookingStatus.Held or BookingStatus.Pending or BookingStatus.Confirmed;

    public static bool IsTerminal(this BookingStatus s) =>
        s is BookingStatus.Completed or BookingStatus.Cancelled or BookingStatus.NoShow;

    /// <summary>Database representation (must match the CHECK constraint and the exclusion constraint).</summary>
    public static string ToDb(this BookingStatus s) => s switch
    {
        BookingStatus.Held => "HELD",
        BookingStatus.Pending => "PENDING",
        BookingStatus.Confirmed => "CONFIRMED",
        BookingStatus.Completed => "COMPLETED",
        BookingStatus.Cancelled => "CANCELLED",
        BookingStatus.NoShow => "NO_SHOW",
        _ => throw new ArgumentOutOfRangeException(nameof(s), s, null)
    };

    public static BookingStatus FromDb(string value) => value switch
    {
        "HELD" => BookingStatus.Held,
        "PENDING" => BookingStatus.Pending,
        "CONFIRMED" => BookingStatus.Confirmed,
        "COMPLETED" => BookingStatus.Completed,
        "CANCELLED" => BookingStatus.Cancelled,
        "NO_SHOW" => BookingStatus.NoShow,
        _ => throw new ArgumentException($"Unknown booking status '{value}'.", nameof(value))
    };

    public static string Label(this BookingStatus s) => s switch
    {
        BookingStatus.NoShow => "No-show",
        _ => s.ToString()
    };
}
