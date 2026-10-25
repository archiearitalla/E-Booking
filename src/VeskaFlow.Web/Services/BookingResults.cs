using VeskaFlow.Core.Domain;
using VeskaFlow.Core.Scheduling;

namespace VeskaFlow.Web.Services;

/// <summary>Stable failure kinds; they map to the API error codes in BUSINESS_RULES section 8.4.</summary>
public enum FailureKind
{
    Validation,       // validation_failed (422)
    SlotTaken,        // slot_taken (409)
    PolicyViolation,  // policy_violation (409)
    Stale,            // stale (409)
    NotFound          // not_found (404), also for another tenant's data
}

public sealed class ServiceResult<T>
{
    private ServiceResult() { }

    public bool IsSuccess { get; private init; }
    public T? Value { get; private init; }
    public FailureKind? Failure { get; private init; }
    public string? Message { get; private init; }
    public IReadOnlyDictionary<string, string> FieldErrors { get; private init; } = new Dictionary<string, string>();

    public static ServiceResult<T> Ok(T value) => new() { IsSuccess = true, Value = value };

    public static ServiceResult<T> Fail(FailureKind kind, string message) =>
        new() { Failure = kind, Message = message };

    public static ServiceResult<T> Invalid(IReadOnlyDictionary<string, string> fieldErrors) =>
        new() { Failure = FailureKind.Validation, Message = Text.ValidationFailed, FieldErrors = fieldErrors };
}

/// <summary>Who performs an action. Customers have no id; they are identified by the manage token.</summary>
/// <summary>Abuse limits for the open online booking page (BUSINESS_RULES 8.6). 0 turns the per-customer cap off.</summary>
public sealed record BookingLimits(int MaxActivePerCustomer = 2);

public sealed record BookingActor(ActorKind Kind, Guid? Id, string Label)
{
    public static BookingActor Customer(string label) => new(ActorKind.Customer, null, label);
    public static BookingActor Staff(Guid adminUserId, string label) => new(ActorKind.Staff, adminUserId, label);
    public static BookingActor System() => new(ActorKind.System, null, "System");

    public string TypeCode => Kind.ToString().ToUpperInvariant();
}

public sealed record CreateBookingRequest(
    Guid BusinessId,
    Guid ServiceId,
    Guid? StaffId,           // null = any available
    DateTime StartUtc,
    string CustomerName,
    string Phone,
    string? Email,
    string IdempotencyKey,
    BookingActor Actor);

/// <summary>Read model of a booking. <c>Version</c> is the row's xmin; pass it back to detect a stale edit.</summary>
public sealed record BookingSummary(
    Guid Id,
    Guid BusinessId,
    Guid StaffId,
    string StaffName,
    Guid ServiceId,
    string ServiceName,
    DateTime StartsAtUtc,
    DateTime EndsAtUtc,
    BookingStatus Status,
    string ManageToken,
    decimal Price,
    uint Version);

public sealed record CreateOutcome(BookingSummary Booking, bool AlreadyExisted);

/// <summary>
/// What the customer sees on their manage page. <c>CancelRefusal</c> explains why self-service cancel is closed
/// (null when it is allowed or when the booking is already finished).
/// </summary>
public sealed record BookingDetails(
    BookingSummary Booking,
    string BusinessName,
    string TimeZone,
    int CancelCutoffHours,
    string CustomerName,
    bool CanCancel,
    string? CancelRefusal,
    string BrandColor = BrandColors.Default);
