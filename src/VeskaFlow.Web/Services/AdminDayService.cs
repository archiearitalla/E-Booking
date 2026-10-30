using VeskaFlow.Core.Domain;
using VeskaFlow.Core.Scheduling;
using VeskaFlow.Web.Data;
using Microsoft.EntityFrameworkCore;

namespace VeskaFlow.Web.Services;

public sealed record DayBooking(
    Guid Id, string StaffName, string ServiceName, string CustomerName, string CustomerPhone,
    DateTime StartsAtUtc, DateTime EndsAtUtc, BookingStatus Status, decimal Price, uint Version,
    bool CanComplete, bool CanNoShow, bool CanCancel,
    DateOnly? FollowUpOn = null, string? FollowUpNote = null, bool FollowUpDone = false, DateOnly? SuggestedFollowUp = null,
    string? CustomerEmail = null, string? FollowUpService = null, string? VisitNotes = null, Guid CustomerId = default,
    bool CanMove = false, decimal? RegularPrice = null, int? DiscountPercent = null, decimal? PaidAmount = null)
{
    /// <summary>The money for this visit: the payment total staff entered, otherwise the price the customer was quoted (after any promo).</summary>
    public decimal Amount => PaidAmount ?? Price;
}

/// <summary>A customer's contact details for saving to the staff phone's contacts (a .vcf file).</summary>
public sealed record CustomerContact(string Name, string Phone, string? Email);

public sealed record DayView(DateOnly Date, string TimeZone, IReadOnlyList<DayBooking> Bookings, DateOnly Today = default)
{
    /// <summary>Only today's bookings can be marked completed or no-show.</summary>
    public bool IsToday => Date == Today;

    public int ActiveCount => Bookings.Count(b => b.Status.OccupiesSlot());
}

public sealed record AuditRow(
    DateTime AtUtc, string ActorType, string ActorLabel, string Action, string? FromStatus, string? ToStatus, string? Reason, Guid? BookingId);

public sealed record AuditView(string TimeZone, IReadOnlyList<AuditRow> Rows);

/// <summary>Read models for the back office day view and audit log. Every query is scoped by the caller's business id.</summary>
public sealed class AdminDayService(IDbContextFactory<AppDbContext> dbFactory, TimeProvider time)
{
    public async Task<DayView?> GetDayAsync(Guid businessId, DateOnly? date, CancellationToken ct = default, Guid? onlyStaffId = null)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var business = await db.Businesses.AsNoTracking().FirstOrDefaultAsync(b => b.Id == businessId, ct);
        if (business is null) return null;

        var now = time.GetUtcNow().UtcDateTime;
        var day = date ?? LocalTime.LocalDateOf(now, business.TimeZone);
        var (start, end) = LocalTime.DayRangeUtc(day, business.TimeZone);

        var rows = await db.Bookings.AsNoTracking()
            .Where(b => b.BusinessId == businessId && b.StartsAtUtc >= start && b.StartsAtUtc < end && (onlyStaffId == null || b.StaffId == onlyStaffId))
            .OrderBy(b => b.StartsAtUtc).ThenBy(b => b.Staff.Name)
            .Select(b => new
            {
                b.Id, StaffName = b.Staff.Name, b.ServiceNameSnapshot, CustomerName = b.Customer.Name,
                CustomerPhone = b.Customer.Phone, b.StartsAtUtc, b.EndsAtUtc, b.Status, b.PriceSnapshot,
                Version = EF.Property<uint>(b, "xmin"),
                b.FollowUpOn, b.FollowUpNote, b.FollowUpDone, ServiceFollowUpDays = b.Service.FollowUpDays,
                CustomerEmail = b.Customer.Email, b.FollowUpService, b.VisitNotes, b.CustomerId,
                b.RegularPriceSnapshot, b.DiscountPercentSnapshot, b.PaidAmount,
            })
            .ToListAsync(ct);

        var bookings = rows.Select(r => new DayBooking(
            r.Id, r.StaffName, r.ServiceNameSnapshot, r.CustomerName, r.CustomerPhone,
            r.StartsAtUtc, r.EndsAtUtc, r.Status, r.PriceSnapshot, r.Version,
            BookingPolicy.CanMarkCompleted(r.Status, r.StartsAtUtc, now, business.TimeZone).Allowed,
            BookingPolicy.CanMarkNoShow(r.Status, r.StartsAtUtc, now, business.TimeZone).Allowed,
            BookingPolicy.CanCancel(r.Status, r.StartsAtUtc, now, ActorKind.Staff, business.CancelCutoffHours).Allowed,
            r.FollowUpOn, r.FollowUpNote, r.FollowUpDone,
            r.ServiceFollowUpDays is { } days ? LocalTime.LocalDateOf(r.StartsAtUtc, business.TimeZone).AddDays(days) : null,
            r.CustomerEmail, r.FollowUpService, r.VisitNotes, r.CustomerId,
            CanMove: r.Status == BookingStatus.Confirmed && r.StartsAtUtc > now,
            RegularPrice: r.RegularPriceSnapshot, DiscountPercent: r.DiscountPercentSnapshot, PaidAmount: r.PaidAmount)).ToList();

        return new DayView(day, business.TimeZone, bookings, LocalTime.LocalDateOf(now, business.TimeZone));
    }

    /// <summary>One booking of this business as the day view shows it, or null (unknown id, or another business's booking).</summary>
    public async Task<DayBooking?> GetBookingAsync(Guid businessId, Guid bookingId, CancellationToken ct = default, Guid? onlyStaffId = null)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var found = await db.Bookings.AsNoTracking().Where(b => b.Id == bookingId && b.BusinessId == businessId && (onlyStaffId == null || b.StaffId == onlyStaffId))
            .Select(b => new { b.StartsAtUtc, b.LocationTimeZone }).FirstOrDefaultAsync(ct);
        if (found is null) return null;

        var day = await GetDayAsync(businessId, LocalTime.LocalDateOf(found.StartsAtUtc, found.LocationTimeZone), ct, onlyStaffId);
        return day?.Bookings.FirstOrDefault(b => b.Id == bookingId);
    }

    /// <summary>The customer of one booking of this business, for "Save contact". Another business's booking is "not found" (null).</summary>
    public async Task<CustomerContact?> GetContactAsync(Guid businessId, Guid bookingId, CancellationToken ct = default, Guid? onlyStaffId = null)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.Bookings.AsNoTracking().Where(b => b.Id == bookingId && b.BusinessId == businessId && (onlyStaffId == null || b.StaffId == onlyStaffId))
            .Select(b => new CustomerContact(b.Customer.Name, b.Customer.Phone, b.Customer.Email)).FirstOrDefaultAsync(ct);
    }

    /// <summary>
    /// True when this booking of this business may be used by someone limited to <paramref name="onlyStaffId"/> (null: no limit,
    /// the booking just has to exist in the business). A booking of another business or another staff member is false.
    /// </summary>
    public async Task<bool> BookingInScopeAsync(Guid businessId, Guid bookingId, Guid? onlyStaffId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.Bookings.AsNoTracking().AnyAsync(b => b.Id == bookingId && b.BusinessId == businessId && (onlyStaffId == null || b.StaffId == onlyStaffId), ct);
    }

    /// <summary>True when this customer has a booking with the staff member the person is limited to (null: no limit, the customer just has to exist).</summary>
    public async Task<bool> CustomerInScopeAsync(Guid businessId, Guid customerId, Guid? onlyStaffId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return onlyStaffId is null
            ? await db.Customers.AsNoTracking().AnyAsync(c => c.Id == customerId && c.BusinessId == businessId, ct)
            : await db.Bookings.AsNoTracking().AnyAsync(b => b.BusinessId == businessId && b.CustomerId == customerId && b.StaffId == onlyStaffId, ct);
    }

    /// <summary>The latest audit entries of this business, newest first. Rows are never edited or deleted by the application.</summary>
    public async Task<AuditView?> GetAuditAsync(Guid businessId, int take = 100, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var tz = await db.Businesses.AsNoTracking().Where(b => b.Id == businessId).Select(b => b.TimeZone).FirstOrDefaultAsync(ct);
        if (tz is null) return null;

        var rows = await db.AuditLogs.AsNoTracking()
            .Where(a => a.BusinessId == businessId)
            .OrderByDescending(a => a.AtUtc).ThenByDescending(a => a.Id)
            .Take(Math.Clamp(take, 1, 500))
            .Select(a => new AuditRow(a.AtUtc, a.ActorType, a.ActorLabel, a.Action, a.FromStatus, a.ToStatus, a.Reason, a.BookingId))
            .ToListAsync(ct);
        return new AuditView(tz, rows);
    }
}
