using VeskaFlow.Core.Domain;

namespace VeskaFlow.Web.Data;

// Column names are derived from property names in snake_case (see AppDbContext) and must match Migrations/*.sql.

public class Business
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Slug { get; set; } = "";
    public string Name { get; set; } = "";
    public string TimeZone { get; set; } = "Asia/Manila";
    public int SlotMinutes { get; set; } = 30;
    public int LeadTimeMinutes { get; set; } = 60;
    public int HorizonDays { get; set; } = 30;
    public int CancelCutoffHours { get; set; } = 2;
    public string Plan { get; set; } = "FREE";
    public string BrandColor { get; set; } = "teal";
    public string Industry { get; set; } = "barbershop";
    public DateTime? TrialEndsAtUtc { get; set; }
    /// <summary>Email the shop's customers a booking confirmation and a reminder (when the customer gave an email).</summary>
    public bool CustomerEmails { get; set; } = true;
    /// <summary>May staff type the payment total by hand when a visit is done (migration 026)? When false it is always what the services come to.</summary>
    public bool AllowManualTotal { get; set; } = true;
    /// <summary>Is the "Record a sale" page switched on? Off by default (migration 027).</summary>
    public bool AllowManualSales { get; set; }
    /// <summary>Until when the paid "more emails" add-on is active; null when it was never bought.</summary>
    public DateTime? EmailAddonUntilUtc { get; set; }
    /// <summary>Until when the paid AI receptionist add-on is switched on; null when it was never bought.</summary>
    public DateTime? AiAddonUntilUtc { get; set; }
    /// <summary>The days left (7, 3 or 1) the last expiry reminder email was sent for, and for which end date.</summary>
    public int? ExpiryNoticeDays { get; set; }
    public DateTime? ExpiryNoticeForEndUtc { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    /// <summary>Staff are reminded to message a customer this many days before the follow-up date.</summary>
    public int FollowUpNoticeDays { get; set; } = 3;
    /// <summary>Switched off by the platform owner: nobody signs in and online booking is closed. Nothing is deleted.</summary>
    public bool IsEnabled { get; set; } = true;
    /// <summary>When the owner agreed to the Terms and Privacy Notice, and which version. Null for shops that signed up before this existed.</summary>
    public DateTime? TermsAcceptedAtUtc { get; set; }
    public string? TermsVersion { get; set; }
    /// <summary>Customers are due a reminder when their appointment is within this many hours (default 24).</summary>
    public int ReminderHours { get; set; } = 24;
}

public class AdminUser
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid BusinessId { get; set; }
    public string Email { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string PasswordHash { get; set; } = "";
    public string Role { get; set; } = "OWNER";
    /// <summary>For a STAFF user: the bookable staff member they are. They then see only that person's bookings. Null = the whole shop.</summary>
    public Guid? StaffId { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    /// <summary>When the owner proved the email address is theirs. Null means not confirmed: no sign-in yet.</summary>
    public DateTime? EmailConfirmedAtUtc { get; set; }
    /// <summary>SHA-256 (hex) of the confirmation link token. The token itself is only ever in the email.</summary>
    public string? EmailTokenHash { get; set; }
    public DateTime? EmailTokenExpiresUtc { get; set; }
    /// <summary>SHA-256 (hex) of the forgot-password link token. The token itself is only ever in the email.</summary>
    public string? PasswordResetHash { get; set; }
    public DateTime? PasswordResetExpiresUtc { get; set; }
}

public class Staff
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid BusinessId { get; set; }
    public string Name { get; set; } = "";
    public bool IsActive { get; set; } = true;
    public int SortOrder { get; set; }
    public List<WorkingHour> WorkingHours { get; set; } = [];
    public List<StaffService> StaffServices { get; set; } = [];
}

public class Service
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid BusinessId { get; set; }
    public string Name { get; set; } = "";
    public int DurationMinutes { get; set; } = 30;
    public int BufferMinutes { get; set; }
    public decimal Price { get; set; }
    public bool IsActive { get; set; } = true;
    public int SortOrder { get; set; }
    /// <summary>Suggested days until a follow-up visit after this service. Null means no follow-up is suggested.</summary>
    public int? FollowUpDays { get; set; }
    /// <summary>The "Other" service: for a visit whose service is not in the list. The booking keeps the text that was typed.</summary>
    public bool IsOther { get; set; }
    /// <summary>Part of the promo discount while one runs (migration 024). On by default; the owner can leave a service out.</summary>
    public bool PromoEligible { get; set; } = true;
}

public class StaffService
{
    public Guid StaffId { get; set; }
    public Guid ServiceId { get; set; }
    public Guid BusinessId { get; set; }
}

public class WorkingHour
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid StaffId { get; set; }
    /// <summary>0 = Sunday ... 6 = Saturday (matches System.DayOfWeek).</summary>
    public short DayOfWeek { get; set; }
    public TimeOnly OpenTime { get; set; }
    public TimeOnly CloseTime { get; set; }
}

/// <summary>The shop's opening hours, set once in Settings. They are copied to every staff member's weekly hours (and to new staff).</summary>
public class BusinessHour
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid BusinessId { get; set; }
    /// <summary>0 = Sunday ... 6 = Saturday (matches System.DayOfWeek).</summary>
    public short DayOfWeek { get; set; }
    public TimeOnly OpenTime { get; set; }
    public TimeOnly CloseTime { get; set; }
}

public class Blackout
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid BusinessId { get; set; }
    /// <summary>Null means the whole shop is closed.</summary>
    public Guid? StaffId { get; set; }
    public DateTime StartsAtUtc { get; set; }
    public DateTime EndsAtUtc { get; set; }
    public string? Reason { get; set; }
}

public class WeeklyClosure
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid BusinessId { get; set; }
    /// <summary>Null means the whole shop.</summary>
    public Guid? StaffId { get; set; }
    /// <summary>0 = Sunday ... 6 = Saturday, as System.DayOfWeek. Null means every day.</summary>
    public short? DayOfWeek { get; set; }
    /// <summary>Local times in the business time zone. Both null means the whole day.</summary>
    public TimeOnly? StartTime { get; set; }
    public TimeOnly? EndTime { get; set; }
    public string? Reason { get; set; }
    /// <summary>Switched off, the rule stays but closes nothing.</summary>
    public bool IsActive { get; set; } = true;
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
}

public class Customer
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid BusinessId { get; set; }
    public string Name { get; set; } = "";
    public string Phone { get; set; } = "";
    public string? Email { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
}

public class Booking
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid BusinessId { get; set; }
    public Guid CustomerId { get; set; }
    public Guid StaffId { get; set; }
    public Guid ServiceId { get; set; }
    public DateTime StartsAtUtc { get; set; }
    public DateTime EndsAtUtc { get; set; }
    /// <summary>EndsAtUtc plus the service buffer, snapshotted at booking time. Drives the exclusion constraint.</summary>
    public DateTime OccupiedUntilUtc { get; set; }
    public string LocationTimeZone { get; set; } = "";
    public BookingStatus Status { get; set; }
    public DateTime? HoldExpiresAtUtc { get; set; }
    /// <summary>What the customer pays: the service's price, or its price after the promo discount (then <c>RegularPriceSnapshot</c> is set too).</summary>
    public decimal PriceSnapshot { get; set; }
    /// <summary>The service's regular price when a promo discount applied to this booking (migration 024); null otherwise.</summary>
    public decimal? RegularPriceSnapshot { get; set; }
    public int? DiscountPercentSnapshot { get; set; }
    /// <summary>The payment total staff entered when the visit was done; null means the net price was paid.</summary>
    public decimal? PaidAmount { get; set; }
    public string ServiceNameSnapshot { get; set; } = "";
    public string ManageToken { get; set; } = "";
    public string? IdempotencyKey { get; set; }
    public string? CancelledBy { get; set; }
    public string? CancelReason { get; set; }
    public DateTime? CancelledAtUtc { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
    /// <summary>Local date (business time zone) the customer should come back. Set by staff after a completed visit.</summary>
    public DateOnly? FollowUpOn { get; set; }
    public string? FollowUpNote { get; set; }
    public bool FollowUpDone { get; set; }
    /// <summary>The service the follow-up is for (a name snapshot). Null means the same service as this visit.</summary>
    public string? FollowUpService { get; set; }
    /// <summary>Optional medical record or notes of the visit, for the customer's history. Staff only: never shown to customers, never in messages or the audit log.</summary>
    public string? VisitNotes { get; set; }
    /// <summary>When staff marked that this customer was reminded of the appointment. Null means not yet.</summary>
    public DateTime? RemindedAtUtc { get; set; }

    public Staff Staff { get; set; } = null!;
    public Service Service { get; set; } = null!;
    public Customer Customer { get; set; } = null!;
}

/// <summary>A service done on top of the booked one (migration 025): its own line on the receipt, with its price as it was when added.</summary>
public class BookingExtra
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid BusinessId { get; set; }
    public Guid BookingId { get; set; }
    public Guid ServiceId { get; set; }
    public string ServiceNameSnapshot { get; set; } = "";
    public decimal PriceSnapshot { get; set; }
    /// <summary>The regular price when a promo discount applied to this line (then <c>DiscountPercentSnapshot</c> is set too); null otherwise.</summary>
    public decimal? RegularPriceSnapshot { get; set; }
    public int? DiscountPercentSnapshot { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
}

public class AuditLog
{
    public long Id { get; set; }
    public Guid BusinessId { get; set; }
    public Guid? BookingId { get; set; }
    public string ActorType { get; set; } = "";
    public Guid? ActorId { get; set; }
    public string ActorLabel { get; set; } = "";
    public string Action { get; set; } = "";
    public string? FromStatus { get; set; }
    public string? ToStatus { get; set; }
    public string? Reason { get; set; }
    public DateTime AtUtc { get; set; } = DateTime.UtcNow;
}

/// <summary>The shop's logo image, at most one per shop (see migration 013).</summary>
public class BusinessLogo
{
    public Guid BusinessId { get; set; }
    public string ContentType { get; set; } = "";
    public byte[] Data { get; set; } = [];
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
}

/// <summary>A shop's promo banner for its booking page (migration 022): one picture, what it says, a switch and an optional last day.</summary>
public class BusinessBanner
{
    public Guid BusinessId { get; set; }
    /// <summary>Null (with <c>Data</c>) for a words-only banner (migration 023).</summary>
    public string? ContentType { get; set; }
    public byte[]? Data { get; set; }
    /// <summary>The headline: what the promo says, in a few words.</summary>
    public string Caption { get; set; } = "";
    /// <summary>An optional second line under the headline.</summary>
    public string? Details { get; set; }
    /// <summary>An optional short, big label such as "20% OFF".</summary>
    public string? Badge { get; set; }
    /// <summary>The look: one of <c>PromoThemes.All</c>.</summary>
    public string Theme { get; set; } = "plain";
    public bool IsActive { get; set; } = true;
    /// <summary>The last day (the shop's local date) the banner shows; null means until the owner turns it off.</summary>
    public DateOnly? EndsOn { get; set; }
    /// <summary>The promo's discount in percent (1 to 90), taken off the services that are part of the promo; null means no discount.</summary>
    public int? DiscountPercent { get; set; }
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
}

/// <summary>A note the platform owner keeps about the whole system, for example when a backup copy was last saved (migration 015).</summary>
public class PlatformSetting
{
    public string Key { get; set; } = "";
    public string Value { get; set; } = "";
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
}

/// <summary>How many emails were sent on one day for one shop (an all-zero business id is the system's own emails to owners). Migration 018.</summary>
public class EmailDaily
{
    public DateOnly Day { get; set; }
    public Guid BusinessId { get; set; }
    public int Sent { get; set; }
}

/// <summary>What the AI receptionist used for one shop in one UTC month (migration 021). <c>Month</c> is the first day of the month.</summary>
public class AiUsage
{
    public DateOnly Month { get; set; }
    public Guid BusinessId { get; set; }
    public int Conversations { get; set; }
    public long InputTokens { get; set; }
    public long OutputTokens { get; set; }
}

/// <summary>One customer conversation with the AI receptionist (migration 021). The transcript is JSON text of what was said, nothing else.</summary>
public class AiChat
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid BusinessId { get; set; }
    public string Transcript { get; set; } = "[]";
    public int Turns { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
}

/// <summary>A picture the platform owner keeps for the whole system, such as the GCash QR code (migration 019).</summary>
public class PlatformImage
{
    public string Key { get; set; } = "";
    public string ContentType { get; set; } = "";
    public byte[] Data { get; set; } = [];
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
}
