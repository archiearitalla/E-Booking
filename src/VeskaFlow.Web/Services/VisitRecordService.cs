using VeskaFlow.Core.Domain;
using VeskaFlow.Core.Scheduling;
using VeskaFlow.Web.Data;
using Microsoft.EntityFrameworkCore;

namespace VeskaFlow.Web.Services;

public sealed record VisitHistoryRow(Guid BookingId, DateOnly Date, string ServiceName, string StaffName, string? Notes);

public sealed record CustomerHistory(Guid CustomerId, string Name, string Phone, string? Email, IReadOnlyList<VisitHistoryRow> Visits);

/// <summary>
/// The optional medical record (visit notes) written on the day of a completed service, and a customer's history of
/// completed visits. Staff only: customers never see it, and it is never copied into messages or the audit log.
/// Every query is scoped by the caller's business id.
/// </summary>
public sealed class VisitRecordService(IDbContextFactory<AppDbContext> dbFactory, TimeProvider? clock = null)
{
    public const int MaxNotesLength = 4000;

    /// <summary>Saves (or clears, when blank) the visit notes of a completed booking.</summary>
    public async Task<ServiceResult<bool>> SetNotesAsync(Guid businessId, Guid bookingId, string? notes, CancellationToken ct = default, Guid? onlyStaffId = null)
    {
        var clean = string.IsNullOrWhiteSpace(notes) ? null : notes.Trim();
        if (clean is { Length: > MaxNotesLength })
            return ServiceResult<bool>.Invalid(new Dictionary<string, string> { ["visitNotes"] = Text.VisitNotesTooLong });

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var booking = await db.Bookings.FirstOrDefaultAsync(b => b.Id == bookingId && b.BusinessId == businessId && (onlyStaffId == null || b.StaffId == onlyStaffId), ct);
        if (booking is null) return ServiceResult<bool>.Fail(FailureKind.NotFound, Text.NotFound);
        if (booking.Status != BookingStatus.Completed)
            return ServiceResult<bool>.Fail(FailureKind.PolicyViolation, Text.VisitNotesOnlyCompleted);

        booking.VisitNotes = clean;
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            return ServiceResult<bool>.Fail(FailureKind.Stale, Text.BookingChanged);
        }
        return ServiceResult<bool>.Ok(true);
    }

    public const decimal MaxPaidAmount = 10_000_000m;

    /// <summary>
    /// Saves the payment total of a completed visit: what the customer actually paid, which may differ from the quoted price (something
    /// added, another discount, a tip). Null clears it, so the quoted price counts again. Completed visits only; scoped to the business
    /// (and, for a limited staff login, to that person's own bookings).
    /// </summary>
    public async Task<ServiceResult<bool>> SetPaidAmountAsync(
        Guid businessId, Guid bookingId, decimal? amount, CancellationToken ct = default, Guid? onlyStaffId = null, BookingActor? actor = null)
    {
        if (amount is { } a && (a < 0 || a > MaxPaidAmount))
            return ServiceResult<bool>.Invalid(new Dictionary<string, string> { ["paidAmount"] = Text.AfterVisitPaymentInvalid });

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var booking = await db.Bookings.FirstOrDefaultAsync(b => b.Id == bookingId && b.BusinessId == businessId && (onlyStaffId == null || b.StaffId == onlyStaffId), ct);
        if (booking is null) return ServiceResult<bool>.Fail(FailureKind.NotFound, Text.NotFound);
        if (booking.Status != BookingStatus.Completed)
            return ServiceResult<bool>.Fail(FailureKind.PolicyViolation, Text.AfterVisitPaymentOnlyCompleted);

        var before = booking.PaidAmount;
        booking.PaidAmount = amount is { } value ? Math.Round(value, 2, MidpointRounding.AwayFromZero) : null;
        // Money changes are written to the audit log: who set what, and what it was before.
        var who = actor ?? BookingActor.System();
        db.AuditLogs.Add(new AuditLog
        {
            BusinessId = businessId, BookingId = bookingId, ActorType = who.TypeCode, ActorId = who.Id, ActorLabel = who.Label, Action = "PAYMENT_SET",
            Reason = $"{(booking.PaidAmount is { } now ? DisplayFormat.Price(now) : "cleared")} (was {(before is { } was ? DisplayFormat.Price(was) : "not set")})",
            AtUtc = (clock ?? TimeProvider.System).GetUtcNow().UtcDateTime,
        });
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            return ServiceResult<bool>.Fail(FailureKind.Stale, Text.BookingChanged);
        }
        return ServiceResult<bool>.Ok(true);
    }

    /// <summary>A customer's completed visits, newest first, with their notes. Null for an unknown id or another business's customer.</summary>
    public async Task<CustomerHistory?> GetHistoryAsync(Guid businessId, Guid customerId, CancellationToken ct = default, Guid? onlyStaffId = null)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var customer = await db.Customers.AsNoTracking().FirstOrDefaultAsync(c => c.Id == customerId && c.BusinessId == businessId, ct);
        if (customer is null) return null;

        var rows = await db.Bookings.AsNoTracking()
            .Where(b => b.BusinessId == businessId && b.CustomerId == customerId && (onlyStaffId == null || b.StaffId == onlyStaffId) && b.Status == BookingStatus.Completed)
            .OrderByDescending(b => b.StartsAtUtc)
            .Select(b => new { b.Id, b.StartsAtUtc, b.LocationTimeZone, b.ServiceNameSnapshot, StaffName = b.Staff.Name, b.VisitNotes })
            .ToListAsync(ct);

        return new CustomerHistory(customer.Id, customer.Name, customer.Phone, customer.Email,
            rows.Select(r => new VisitHistoryRow(r.Id, LocalTime.LocalDateOf(r.StartsAtUtc, r.LocationTimeZone), r.ServiceNameSnapshot, r.StaffName, r.VisitNotes)).ToList());
    }
}
