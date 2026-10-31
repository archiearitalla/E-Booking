using System.Globalization;
using VeskaFlow.Core.Domain;
using VeskaFlow.Core.Pricing;
using VeskaFlow.Core.Scheduling;
using VeskaFlow.Web.Data;
using Microsoft.EntityFrameworkCore;

namespace VeskaFlow.Web.Services;

/// <summary>One service done on top of the booked one, as a line on the receipt.</summary>
public sealed record ExtraLine(Guid Id, string Name, decimal Price, decimal? RegularPrice = null, int? DiscountPercent = null);

/// <summary>The outcome of adding or removing a line. <c>PaymentReset</c> is true when a saved payment total went back to the items total.</summary>
public sealed record ExtraChange(ExtraLine? Line, bool PaymentReset);

/// <summary>
/// Services done on top of the booked one (BUSINESS_RULES 6.y19): after a visit is done, staff add what else was done, each as its
/// own line with its own price. A service in the list takes its own price, less the promo discount when a promo is running today and the
/// service is part of it; the "Other" service takes a description and a price typed by staff, and any service may take a typed amount instead when the shop allows it. Only a completed visit can have extras,
/// at most <see cref="MaxExtras"/>. Changing the lines sends a saved payment total back to the items total (so it can never be stale),
/// and every change is written to the audit log. Every query is scoped by the caller's business id.
/// </summary>
public sealed class BookingExtraService(IDbContextFactory<AppDbContext> dbFactory, TimeProvider time)
{
    public const int MaxExtras = 10;
    public const int MaxNote = 60;
    public const decimal MaxPrice = 1_000_000m;

    public async Task<IReadOnlyList<ExtraLine>> ListAsync(Guid businessId, Guid bookingId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.BookingExtras.AsNoTracking().Where(e => e.BusinessId == businessId && e.BookingId == bookingId)
            .OrderBy(e => e.CreatedAtUtc).ThenBy(e => e.ServiceNameSnapshot)
            .Select(e => new ExtraLine(e.Id, e.ServiceNameSnapshot, e.PriceSnapshot, e.RegularPriceSnapshot, e.DiscountPercentSnapshot))
            .ToListAsync(ct);
    }

    public async Task<ServiceResult<ExtraChange>> AddAsync(
        Guid businessId, Guid bookingId, Guid serviceId, string? note, decimal? typedPrice, BookingActor actor,
        CancellationToken ct = default, Guid? onlyStaffId = null)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var booking = await db.Bookings.FirstOrDefaultAsync(
            b => b.Id == bookingId && b.BusinessId == businessId && (onlyStaffId == null || b.StaffId == onlyStaffId), ct);
        if (booking is null) return ServiceResult<ExtraChange>.Fail(FailureKind.NotFound, Text.NotFound);
        if (booking.Status != BookingStatus.Completed)
            return ServiceResult<ExtraChange>.Fail(FailureKind.PolicyViolation, Text.ExtraOnlyCompleted);
        if (await db.BookingExtras.CountAsync(e => e.BookingId == bookingId && e.BusinessId == businessId, ct) >= MaxExtras)
            return ServiceResult<ExtraChange>.Fail(FailureKind.PolicyViolation, Text.ExtraTooMany(MaxExtras));

        var service = await db.Services.AsNoTracking().FirstOrDefaultAsync(s => s.Id == serviceId && s.BusinessId == businessId && s.IsActive, ct);
        if (service is null) return ServiceResult<ExtraChange>.Fail(FailureKind.NotFound, Text.NotFound);

        string name;
        decimal price;
        decimal? regular = null;
        int? percent = null;
        if (service.IsOther)
        {
            var errors = new Dictionary<string, string>();
            var words = (note ?? "").Trim();
            if (words.Length is < 1 or > MaxNote) errors["note"] = Text.ExtraNoteInvalid;
            if (typedPrice is not { } typed || typed < 0 || typed > MaxPrice) errors["price"] = Text.ExtraPriceInvalid;
            if (errors.Count > 0) return ServiceResult<ExtraChange>.Invalid(errors);
            name = $"{service.Name}: {words}";
            price = Math.Round(typedPrice!.Value, 2, MidpointRounding.AwayFromZero);
        }
        else
        {
            name = service.Name;
            price = service.Price;
            if (typedPrice is not null)
            {
                // A price typed by staff for a listed service, when the shop allows typing amounts: it is what was charged, so no promo is applied on top.
                if (!await db.Businesses.AsNoTracking().Where(b => b.Id == businessId).Select(b => b.AllowManualTotal).FirstAsync(ct))
                    return ServiceResult<ExtraChange>.Fail(FailureKind.PolicyViolation, Text.PaymentManualOff);
                if (typedPrice is < 0 or > MaxPrice) return ServiceResult<ExtraChange>.Invalid(new Dictionary<string, string> { ["price"] = Text.ExtraPriceInvalid });
                price = Math.Round(typedPrice.Value, 2, MidpointRounding.AwayFromZero);
            }
            else
            {
            // A promo running today takes its discount off a service that is part of it, like any booking made while it runs.
            var now = time.GetUtcNow().UtcDateTime;
            var zone = await db.Businesses.AsNoTracking().Where(b => b.Id == businessId).Select(b => b.TimeZone).FirstAsync(ct);
            var promo = await db.BusinessBanners.AsNoTracking().Where(b => b.BusinessId == businessId)
                .Select(b => new { b.IsActive, b.EndsOn, b.DiscountPercent }).FirstOrDefaultAsync(ct);
            percent = PromoPricing.PercentFor(false, service.PromoEligible, service.Price,
                promo is null ? null : PromoPricing.ActivePercent(promo.IsActive, promo.EndsOn, promo.DiscountPercent, LocalTime.LocalDateOf(now, zone)));
            if (percent is { } off) { regular = service.Price; price = PromoPricing.Net(service.Price, off); }
            }
        }

        var line = new BookingExtra
        {
            BusinessId = businessId, BookingId = bookingId, ServiceId = service.Id, ServiceNameSnapshot = name, PriceSnapshot = price,
            RegularPriceSnapshot = regular, DiscountPercentSnapshot = percent, CreatedAtUtc = time.GetUtcNow().UtcDateTime,
        };
        db.BookingExtras.Add(line);
        var reset = booking.PaidAmount is not null;
        booking.PaidAmount = null; // the payment total follows the items until staff type a different one
        db.AuditLogs.Add(Audit(booking, actor, "EXTRA_ADDED", $"{name}: {DisplayFormat.Price(price)}{(reset ? " (payment total reset)" : "")}"));
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateConcurrencyException) { return ServiceResult<ExtraChange>.Fail(FailureKind.Stale, Text.BookingChanged); }
        return ServiceResult<ExtraChange>.Ok(new ExtraChange(new ExtraLine(line.Id, name, price, regular, percent), reset));
    }

    public async Task<ServiceResult<ExtraChange>> RemoveAsync(
        Guid businessId, Guid bookingId, Guid extraId, BookingActor actor, CancellationToken ct = default, Guid? onlyStaffId = null)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var booking = await db.Bookings.FirstOrDefaultAsync(
            b => b.Id == bookingId && b.BusinessId == businessId && (onlyStaffId == null || b.StaffId == onlyStaffId), ct);
        if (booking is null) return ServiceResult<ExtraChange>.Fail(FailureKind.NotFound, Text.NotFound);
        if (booking.Status != BookingStatus.Completed)
            return ServiceResult<ExtraChange>.Fail(FailureKind.PolicyViolation, Text.ExtraOnlyCompleted);
        var line = await db.BookingExtras.FirstOrDefaultAsync(e => e.Id == extraId && e.BookingId == bookingId && e.BusinessId == businessId, ct);
        if (line is null) return ServiceResult<ExtraChange>.Fail(FailureKind.NotFound, Text.NotFound);

        db.BookingExtras.Remove(line);
        var reset = booking.PaidAmount is not null;
        booking.PaidAmount = null;
        db.AuditLogs.Add(Audit(booking, actor, "EXTRA_REMOVED", $"{line.ServiceNameSnapshot}: {DisplayFormat.Price(line.PriceSnapshot)}{(reset ? " (payment total reset)" : "")}"));
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateConcurrencyException) { return ServiceResult<ExtraChange>.Fail(FailureKind.Stale, Text.BookingChanged); }
        return ServiceResult<ExtraChange>.Ok(new ExtraChange(null, reset));
    }

    private AuditLog Audit(Booking booking, BookingActor actor, string action, string reason) => new()
    {
        BusinessId = booking.BusinessId, BookingId = booking.Id, ActorType = actor.TypeCode, ActorId = actor.Id, ActorLabel = actor.Label,
        Action = action, Reason = reason, AtUtc = time.GetUtcNow().UtcDateTime,
    };
}
