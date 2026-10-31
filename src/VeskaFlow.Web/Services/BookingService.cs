using System.Net.Mail;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using VeskaFlow.Core.Domain;
using VeskaFlow.Core.Pricing;
using VeskaFlow.Core.Scheduling;
using VeskaFlow.Web.Data;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace VeskaFlow.Web.Services;

/// <summary>
/// Creates and changes bookings (BUSINESS_RULES sections 5 to 8). Every query is scoped by the business id the
/// caller resolved from the slug or the signed-in user's claims. Availability comes from
/// <see cref="AvailabilityCalculator"/> and is re-derived here at confirmation; the database exclusion constraint
/// <c>no_double_booking</c> is the final guarantee, and its violation (SQLSTATE 23P01) is a normal "slot taken" result.
/// </summary>
public sealed partial class BookingService(IDbContextFactory<AppDbContext> dbFactory, TimeProvider time, BookingLimits? limits = null)
{
    private readonly BookingLimits caps = limits ?? new BookingLimits();

    private const string ExclusionViolation = "23P01";
    private const string UniqueViolation = "23505";
    private const string DeadlockDetected = "40P01";

    [GeneratedRegex(@"^\+?\d{7,15}$")]
    private static partial Regex PhonePattern();

    // ---------------------------------------------------------------- availability

    /// <summary>Free start times (UTC) for one local date. <paramref name="staffId"/> null means any available staff.</summary>
    public async Task<ServiceResult<IReadOnlyList<DateTime>>> GetFreeStartsAsync(
        Guid businessId, Guid serviceId, Guid? staffId, DateOnly localDate, bool walkIn = false, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var business = await LoadBusinessAsync(db, businessId, ct);
        var service = business is null ? null : await LoadServiceAsync(db, businessId, serviceId, ct);
        if (business is null || service is null)
            return ServiceResult<IReadOnlyList<DateTime>>.Fail(FailureKind.NotFound, Text.NotFound);

        var candidates = await LoadEligibleStaffAsync(db, businessId, serviceId, staffId, ct);
        if (staffId is not null && candidates.Count == 0)
            return ServiceResult<IReadOnlyList<DateTime>>.Fail(FailureKind.NotFound, Text.NotFound);

        var all = new SortedSet<DateTime>();
        foreach (var staff in candidates)
            foreach (var start in await FreeStartsForStaffAsync(db, business, service, staff.Id, localDate, walkIn, ct))
                all.Add(start);

        return ServiceResult<IReadOnlyList<DateTime>>.Ok(all.ToList());
    }

    // ---------------------------------------------------------------- create

    /// <summary>Longest text a person can type to say what "Other" is.</summary>
    public const int MaxServiceNote = 60;

    public async Task<ServiceResult<CreateOutcome>> CreateAsync(CreateBookingRequest request, CancellationToken ct = default)
    {
        var errors = ValidateInput(request, out var name, out var phone, out var email);
        if (errors.Count > 0) return ServiceResult<CreateOutcome>.Invalid(errors);

        var start = LocalTime.AsUtc(request.StartUtc);
        var key = request.IdempotencyKey.Trim();

        await using var db = await dbFactory.CreateDbContextAsync(ct);

        // A retry with the same key returns the booking that already exists.
        var existing = await FindByIdempotencyKeyAsync(db, request.BusinessId, key, ct);
        if (existing is not null)
            return ServiceResult<CreateOutcome>.Ok(new CreateOutcome(existing, true));

        var business = await LoadBusinessAsync(db, request.BusinessId, ct);
        var service = business is null ? null : await LoadServiceAsync(db, request.BusinessId, request.ServiceId, ct);
        if (business is null || service is null)
            return ServiceResult<CreateOutcome>.Fail(FailureKind.NotFound, Text.NotFound);

        // "Other": the person must say what they need, and the booking keeps that text as its service name.
        if (service.IsOther)
        {
            var note = (request.ServiceNote ?? "").Trim();
            if (note.Length is < 1 or > MaxServiceNote)
                return ServiceResult<CreateOutcome>.Invalid(new Dictionary<string, string> { ["serviceNote"] = Text.OtherServiceRequired });
        }

        // A finished trial closes online booking for customers; staff can still record bookings.
        if (request.Actor.Kind != ActorKind.Staff && business.TrialEndsAtUtc is { } trialEnds
            && time.GetUtcNow().UtcDateTime > DateTime.SpecifyKind(trialEnds, DateTimeKind.Utc))
            return ServiceResult<CreateOutcome>.Fail(FailureKind.PolicyViolation, Text.TrialEndedPublic);

        // A shop switched off by the platform owner takes no online bookings either.
        if (request.Actor.Kind != ActorKind.Staff && !business.IsEnabled)
            return ServiceResult<CreateOutcome>.Fail(FailureKind.PolicyViolation, Text.TrialEndedPublic);

        var candidates = await LoadEligibleStaffAsync(db, business.Id, service.Id, request.StaffId, ct);
        if (request.StaffId is not null && candidates.Count == 0)
            return ServiceResult<CreateOutcome>.Fail(FailureKind.NotFound, Text.NotFound);

        // Re-derive availability on the server; the posted start time may come from a stale page.
        var walkIn = request.Actor.Kind == ActorKind.Staff;
        var localDate = LocalTime.LocalDateOf(start, business.TimeZone);
        var free = new List<(Staff Staff, int Load)>();
        foreach (var staff in candidates)
        {
            var starts = await FreeStartsForStaffAsync(db, business, service, staff.Id, localDate, walkIn, ct);
            if (starts.Contains(start))
                free.Add((staff, await CountActiveOnDayAsync(db, business, staff.Id, localDate, ct)));
        }

        // "Any available": fewest bookings that day, then sort order.
        var ordered = free.OrderBy(f => f.Load).ThenBy(f => f.Staff.SortOrder).Select(f => f.Staff).ToList();

        foreach (var staff in ordered)
        {
            var attempt = await TryInsertAsync(request, business, service, staff, start, name, phone, email, key, ct);
            switch (attempt.Outcome)
            {
                case InsertOutcome.Inserted:
                    return ServiceResult<CreateOutcome>.Ok(new CreateOutcome(
                        (await LoadSummaryAsync(business.Id, attempt.BookingId!.Value, ct))!, false));
                case InsertOutcome.CustomerLimit:
                    // A parallel request with this very key may have just created the booking that now "overlaps":
                    // that is a double click or a retry, so return it instead of refusing.
                    await using (var limitDb = await dbFactory.CreateDbContextAsync(ct))
                    {
                        var sameKey = await FindByIdempotencyKeyAsync(limitDb, business.Id, key, ct);
                        if (sameKey is not null)
                            return ServiceResult<CreateOutcome>.Ok(new CreateOutcome(sameKey, true));
                    }
                    // Otherwise it is the same person with an overlapping or too many open bookings: a field error on the phone.
                    return ServiceResult<CreateOutcome>.Invalid(new Dictionary<string, string> { ["phone"] = attempt.Message! });
                case InsertOutcome.Duplicate:
                    // A concurrent request with the same key won; return its booking.
                    await using (var winnerDb = await dbFactory.CreateDbContextAsync(ct))
                        return ServiceResult<CreateOutcome>.Ok(new CreateOutcome(
                            (await FindByIdempotencyKeyAsync(winnerDb, business.Id, key, ct))!, true));
                case InsertOutcome.Conflict:
                    // The slot may have been taken by a concurrent request with this very key (a retry). If so, return it.
                    await using (var retryDb = await dbFactory.CreateDbContextAsync(ct))
                    {
                        var sameKey = await FindByIdempotencyKeyAsync(retryDb, business.Id, key, ct);
                        if (sameKey is not null)
                            return ServiceResult<CreateOutcome>.Ok(new CreateOutcome(sameKey, true));
                    }
                    continue; // lost the race for this staff member; try the next one
            }
        }

        // Nothing was free. If a concurrent request with this very key just created the booking, this is a retry.
        await using (var lastDb = await dbFactory.CreateDbContextAsync(ct))
        {
            var sameKey = await FindByIdempotencyKeyAsync(lastDb, business.Id, key, ct);
            if (sameKey is not null)
                return ServiceResult<CreateOutcome>.Ok(new CreateOutcome(sameKey, true));
        }
        return ServiceResult<CreateOutcome>.Fail(FailureKind.SlotTaken, Text.SlotTaken);
    }

    private enum InsertOutcome { Inserted, Conflict, Duplicate, CustomerLimit }

    private sealed record InsertAttempt(InsertOutcome Outcome, Guid? BookingId, string? Message = null);

    private async Task<InsertAttempt> TryInsertAsync(
        CreateBookingRequest request, Business business, Service service, Staff staff, DateTime start,
        string name, string phone, string? email, string key, CancellationToken ct)
    {
        // A fresh context per attempt so a failed save never leaves tracked state behind.
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var now = time.GetUtcNow().UtcDateTime;
        try
        {
            // Queue concurrent attempts for the same staff member instead of letting them all insert at once, which
            // makes PostgreSQL resolve mutual waits in the exclusion constraint one deadlock (1 s) at a time. This is
            // only a throughput aid: the no_double_booking constraint remains the guarantee and still decides.
            await db.Database.ExecuteSqlAsync($"SELECT pg_advisory_xact_lock(hashtextextended({staff.Id.ToString()}, 0))", ct);

            // Customers are per business, matched by phone. A repeat booking updates name and email on file.
            var customerRows = await db.Database.SqlQuery<Guid>($"""
                INSERT INTO customer (id, business_id, name, phone, email, marketing_ok, marketing_ok_at_utc)
                VALUES ({Guid.NewGuid()}, {business.Id}, {name}, {phone}, {email}, {request.MarketingOk}, {(request.MarketingOk ? now : (DateTime?)null)})
                ON CONFLICT (business_id, phone)
                DO UPDATE SET name = EXCLUDED.name, email = COALESCE(EXCLUDED.email, customer.email),
                    marketing_ok_at_utc = CASE WHEN EXCLUDED.marketing_ok AND NOT customer.marketing_ok THEN EXCLUDED.marketing_ok_at_utc ELSE customer.marketing_ok_at_utc END,
                    marketing_ok = customer.marketing_ok OR EXCLUDED.marketing_ok
                RETURNING id AS "Value"
                """).ToListAsync(ct);
            var customerId = customerRows.Single();

            var end = start.AddMinutes(service.DurationMinutes);

            // The online booking page is open to everyone, so one person cannot hoard or double-book time.
            // Checked inside this transaction under a per-customer lock, so two parallel requests cannot both slip
            // through. Staff-created bookings (walk-ins, phone-ins) are exempt: staff know the situation.
            if (request.Actor.Kind != ActorKind.Staff)
            {
                await db.Database.ExecuteSqlAsync($"SELECT pg_advisory_xact_lock(hashtextextended({"cust:" + customerId}, 0))", ct);

                var active = db.Bookings.Where(b => b.BusinessId == business.Id && b.CustomerId == customerId
                    && (b.Status == BookingStatus.Held || b.Status == BookingStatus.Pending || b.Status == BookingStatus.Confirmed));

                if (await active.AnyAsync(b => b.StartsAtUtc < end && b.EndsAtUtc > start, ct))
                    return new InsertAttempt(InsertOutcome.CustomerLimit, null, Text.AlreadyBookedThatTime);

                if (caps.MaxActivePerCustomer > 0 && await active.CountAsync(b => b.EndsAtUtc > now, ct) >= caps.MaxActivePerCustomer)
                    return new InsertAttempt(InsertOutcome.CustomerLimit, null, string.Format(Text.TooManyActiveBookings, caps.MaxActivePerCustomer));
            }

            // A running promo takes its percentage off a service that is part of it, at the moment the booking is made: what the
            // customer saw is what they pay, whatever the day of the appointment, and a later change of the promo never touches it.
            var promo = await db.BusinessBanners.AsNoTracking().Where(b => b.BusinessId == business.Id)
                .Select(b => new { b.IsActive, b.EndsOn, b.DiscountPercent }).FirstOrDefaultAsync(ct);
            var percent = PromoPricing.PercentFor(service.IsOther, service.PromoEligible, service.Price,
                promo is null ? null : PromoPricing.ActivePercent(promo.IsActive, promo.EndsOn, promo.DiscountPercent, LocalTime.LocalDateOf(now, business.TimeZone)));

            var booking = new Booking
            {
                BusinessId = business.Id,
                CustomerId = customerId,
                StaffId = staff.Id,
                ServiceId = service.Id,
                StartsAtUtc = start,
                EndsAtUtc = end,
                OccupiedUntilUtc = end.AddMinutes(service.BufferMinutes),
                LocationTimeZone = business.TimeZone,
                Status = BookingStatus.Confirmed, // MVP: pay on site, instant confirmation
                PriceSnapshot = percent is { } off ? PromoPricing.Net(service.Price, off) : service.Price,
                RegularPriceSnapshot = percent is null ? null : service.Price,
                DiscountPercentSnapshot = percent,
                // The deposit is part of the price, so after a promo discount it can never be more than what is owed.
                DepositSnapshot = service.IsOther ? 0m : Math.Min(service.DepositAmount, percent is { } off2 ? PromoPricing.Net(service.Price, off2) : service.Price),
                ServiceNameSnapshot = service.IsOther ? $"{service.Name}: {(request.ServiceNote ?? "").Trim()}" : service.Name,
                ManageToken = NewManageToken(),
                IdempotencyKey = key,
                CreatedAtUtc = now,
                UpdatedAtUtc = now,
            };
            db.Bookings.Add(booking);
            db.AuditLogs.Add(new AuditLog
            {
                BusinessId = business.Id,
                BookingId = booking.Id,
                ActorType = request.Actor.TypeCode,
                ActorId = request.Actor.Id,
                ActorLabel = request.Actor.Label,
                Action = "CREATED",
                ToStatus = BookingStatus.Confirmed.ToDb(),
                AtUtc = now,
            });
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);

            return new InsertAttempt(InsertOutcome.Inserted, booking.Id);
        }
        catch (Exception ex) when (FindPostgres(ex) is { SqlState: ExclusionViolation or DeadlockDetected })
        {
            // 23P01: the constraint rejected the overlap. 40P01: two transactions inserted overlapping ranges at the
            // same instant and each waited on the other; PostgreSQL aborts one, which lost the slot just the same.
            return new InsertAttempt(InsertOutcome.Conflict, null);
        }
        catch (Exception ex) when (FindPostgres(ex) is { SqlState: UniqueViolation, ConstraintName: "ux_booking_idempotency" })
        {
            return new InsertAttempt(InsertOutcome.Duplicate, null);
        }
    }

    // ---------------------------------------------------------------- transitions

    /// <summary>
    /// Moves a booking to a new status. The booking must belong to <paramref name="businessId"/>.
    /// <paramref name="expectedVersion"/> (the Version the caller last saw) makes a stale edit fail with <see cref="FailureKind.Stale"/>.
    /// </summary>
    public async Task<ServiceResult<BookingSummary>> TransitionAsync(
        Guid businessId, Guid bookingId, BookingStatus to, BookingActor actor,
        string? reason = null, uint? expectedVersion = null, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var booking = await db.Bookings.FirstOrDefaultAsync(b => b.Id == bookingId && b.BusinessId == businessId, ct);
        if (booking is null) return ServiceResult<BookingSummary>.Fail(FailureKind.NotFound, Text.NotFound);

        // Staff must say why they cancel: the customer is affected and the audit log keeps the reason.
        if (to == BookingStatus.Cancelled && actor.Kind == ActorKind.Staff && string.IsNullOrWhiteSpace(reason))
            return ServiceResult<BookingSummary>.Invalid(new Dictionary<string, string> { ["reason"] = Text.CancelReasonRequired });

        return await ApplyTransitionAsync(db, booking, to, actor, reason, expectedVersion, ct);
    }

    /// <summary>Customer self-service cancellation. The manage token authorizes exactly one booking.</summary>
    public async Task<ServiceResult<BookingSummary>> CancelByTokenAsync(
        string manageToken, uint? expectedVersion = null, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(manageToken))
            return ServiceResult<BookingSummary>.Fail(FailureKind.NotFound, Text.NotFound);

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var booking = await db.Bookings.FirstOrDefaultAsync(b => b.ManageToken == manageToken, ct);
        if (booking is null) return ServiceResult<BookingSummary>.Fail(FailureKind.NotFound, Text.NotFound);
        // The audit trail names the customer the same way creation did, not a generic "Customer".
        var who = await db.Customers.Where(c => c.Id == booking.CustomerId).Select(c => c.Name).FirstOrDefaultAsync(ct) ?? "Customer";
        return await ApplyTransitionAsync(db, booking, BookingStatus.Cancelled, BookingActor.Customer(who),
            null, expectedVersion, ct);
    }

    // ---------------------------------------------------------------- reschedule

    /// <summary>
    /// Free start times for moving a confirmed booking to another local date, with the same staff member and service.
    /// The booking's own current slot counts as free, so a small shift (10:00 to 10:30) is possible. Staff rules apply
    /// (no lead time).
    /// </summary>
    public async Task<ServiceResult<IReadOnlyList<DateTime>>> GetRescheduleStartsAsync(
        Guid businessId, Guid bookingId, DateOnly localDate, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var booking = await db.Bookings.AsNoTracking().FirstOrDefaultAsync(b => b.Id == bookingId && b.BusinessId == businessId, ct);
        var business = booking is null ? null : await LoadBusinessAsync(db, businessId, ct);
        var service = booking is null ? null : await db.Services.AsNoTracking().FirstOrDefaultAsync(s => s.Id == booking.ServiceId && s.BusinessId == businessId, ct);
        if (booking is null || business is null || service is null)
            return ServiceResult<IReadOnlyList<DateTime>>.Fail(FailureKind.NotFound, Text.NotFound);
        return ServiceResult<IReadOnlyList<DateTime>>.Ok(
            await FreeStartsForStaffAsync(db, business, service, booking.StaffId, localDate, true, ct, booking.Id));
    }

    /// <summary>
    /// Moves a confirmed booking that has not started to a new start time (same staff member, same length). Staff only: the
    /// customer is told by the shop. The time is re-checked on the server and the database constraint stays the final guard.
    /// </summary>
    public async Task<ServiceResult<BookingSummary>> RescheduleAsync(
        Guid businessId, Guid bookingId, DateTime newStartUtc, BookingActor actor, CancellationToken ct = default)
    {
        if (actor.Kind != ActorKind.Staff) return ServiceResult<BookingSummary>.Fail(FailureKind.PolicyViolation, Text.NotAllowed);
        return await RescheduleCoreAsync(businessId, bookingId, newStartUtc, actor, ct);
    }

    /// <summary>The booking the manage token points at, as (shop, booking, customer name), or null.</summary>
    private async Task<(Guid BusinessId, Guid BookingId, string Who)?> ResolveTokenAsync(string? manageToken, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(manageToken)) return null;
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var row = await db.Bookings.AsNoTracking().Where(b => b.ManageToken == manageToken)
            .Select(b => new { b.BusinessId, b.Id, Who = b.Customer.Name }).FirstOrDefaultAsync(ct);
        return row is null ? null : (row.BusinessId, row.Id, row.Who);
    }

    /// <summary>
    /// Free start times for the customer moving their own booking to a local date (same staff member and service).
    /// The customer rules apply: lead time and booking horizon. The booking's own slot counts as free.
    /// </summary>
    public async Task<ServiceResult<IReadOnlyList<DateTime>>> GetRescheduleStartsByTokenAsync(
        string manageToken, DateOnly localDate, CancellationToken ct = default)
    {
        if (await ResolveTokenAsync(manageToken, ct) is not { } target)
            return ServiceResult<IReadOnlyList<DateTime>>.Fail(FailureKind.NotFound, Text.NotFound);

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var booking = await db.Bookings.AsNoTracking().FirstAsync(b => b.Id == target.BookingId, ct);
        var business = await LoadBusinessAsync(db, target.BusinessId, ct);
        var service = await db.Services.AsNoTracking().FirstOrDefaultAsync(s => s.Id == booking.ServiceId && s.BusinessId == target.BusinessId, ct);
        if (business is null || service is null)
            return ServiceResult<IReadOnlyList<DateTime>>.Fail(FailureKind.NotFound, Text.NotFound);
        var allowed = BookingPolicy.CanReschedule(booking.Status, booking.StartsAtUtc, time.GetUtcNow().UtcDateTime, ActorKind.Customer, business.CancelCutoffHours);
        if (!allowed.Allowed)
            return ServiceResult<IReadOnlyList<DateTime>>.Fail(FailureKind.PolicyViolation, allowed.Reason ?? Text.NotAllowed);
        return ServiceResult<IReadOnlyList<DateTime>>.Ok(
            await FreeStartsForStaffAsync(db, business, service, booking.StaffId, localDate, false, ct, booking.Id));
    }

    /// <summary>
    /// Customer self-service move. The manage token authorizes exactly one booking; the cutoff is the same as for
    /// cancelling. The time is checked again here and the database constraint stays the final guard.
    /// </summary>
    public async Task<ServiceResult<BookingSummary>> RescheduleByTokenAsync(
        string manageToken, DateTime newStartUtc, CancellationToken ct = default)
    {
        if (await ResolveTokenAsync(manageToken, ct) is not { } target)
            return ServiceResult<BookingSummary>.Fail(FailureKind.NotFound, Text.NotFound);
        return await RescheduleCoreAsync(target.BusinessId, target.BookingId, newStartUtc, BookingActor.Customer(target.Who), ct);
    }

    private async Task<ServiceResult<BookingSummary>> RescheduleCoreAsync(
        Guid businessId, Guid bookingId, DateTime newStartUtc, BookingActor actor, CancellationToken ct)
    {
        var byCustomer = actor.Kind == ActorKind.Customer;
        var start = LocalTime.AsUtc(newStartUtc);

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var booking = await db.Bookings.FirstOrDefaultAsync(b => b.Id == bookingId && b.BusinessId == businessId, ct);
        if (booking is null) return ServiceResult<BookingSummary>.Fail(FailureKind.NotFound, Text.NotFound);
        var business = await LoadBusinessAsync(db, businessId, ct);
        var service = await db.Services.AsNoTracking().FirstOrDefaultAsync(s => s.Id == booking.ServiceId && s.BusinessId == businessId, ct);
        if (business is null || service is null) return ServiceResult<BookingSummary>.Fail(FailureKind.NotFound, Text.NotFound);

        var now = time.GetUtcNow().UtcDateTime;
        var policy = BookingPolicy.CanReschedule(booking.Status, booking.StartsAtUtc, now, actor.Kind, business.CancelCutoffHours);
        if (!policy.Allowed)
            return ServiceResult<BookingSummary>.Fail(FailureKind.PolicyViolation, byCustomer ? policy.Reason! : Text.RescheduleNotAllowed);

        var localDate = LocalTime.LocalDateOf(start, business.TimeZone);
        var free = await FreeStartsForStaffAsync(db, business, service, booking.StaffId, localDate, !byCustomer, ct, booking.Id);
        if (!free.Contains(start) || start <= now) return ServiceResult<BookingSummary>.Fail(FailureKind.SlotTaken, Text.SlotTaken);

        var oldStart = booking.StartsAtUtc;
        try
        {
            await db.Database.ExecuteSqlAsync($"SELECT pg_advisory_xact_lock(hashtextextended({booking.StaffId.ToString()}, 0))", ct);
            booking.StartsAtUtc = start;
            booking.EndsAtUtc = start.AddMinutes(service.DurationMinutes);
            booking.OccupiedUntilUtc = booking.EndsAtUtc.AddMinutes(service.BufferMinutes);
            booking.RemindedAtUtc = null; // the customer is due a reminder for the new time
            booking.UpdatedAtUtc = now;
            db.AuditLogs.Add(new AuditLog
            {
                BusinessId = businessId,
                BookingId = booking.Id,
                ActorType = actor.TypeCode,
                ActorId = actor.Id,
                ActorLabel = actor.Label,
                Action = "RESCHEDULED",
                FromStatus = booking.Status.ToDb(),
                ToStatus = booking.Status.ToDb(),
                Reason = $"{DisplayFormat.Date(oldStart, business.TimeZone)} {DisplayFormat.Time(oldStart, business.TimeZone)} to {DisplayFormat.Date(start, business.TimeZone)} {DisplayFormat.Time(start, business.TimeZone)}",
                AtUtc = now,
            });
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
        }
        catch (Exception ex) when (FindPostgres(ex) is { SqlState: ExclusionViolation or DeadlockDetected })
        {
            return ServiceResult<BookingSummary>.Fail(FailureKind.SlotTaken, Text.SlotTaken);
        }
        return ServiceResult<BookingSummary>.Ok((await LoadSummaryAsync(businessId, bookingId, ct))!);
    }

    /// <summary>
    /// Staff mark that the deposit arrived (or undo it). The slot is held either way: the deposit is a note on a confirmed
    /// booking, not a state. Only a confirmed booking that asks for a deposit; the audit row keeps who and when.
    /// </summary>
    public async Task<ServiceResult<BookingSummary>> SetDepositReceivedAsync(
        Guid businessId, Guid bookingId, bool received, BookingActor actor, CancellationToken ct = default)
    {
        if (actor.Kind != ActorKind.Staff) return ServiceResult<BookingSummary>.Fail(FailureKind.PolicyViolation, Text.NotAllowed);

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var booking = await db.Bookings.FirstOrDefaultAsync(b => b.Id == bookingId && b.BusinessId == businessId, ct);
        if (booking is null) return ServiceResult<BookingSummary>.Fail(FailureKind.NotFound, Text.NotFound);
        if (booking.Status != BookingStatus.Confirmed || booking.DepositSnapshot <= 0)
            return ServiceResult<BookingSummary>.Fail(FailureKind.PolicyViolation, Text.DepositNotApplicable);
        if ((booking.DepositReceivedAtUtc is not null) == received)
            return ServiceResult<BookingSummary>.Ok((await LoadSummaryAsync(businessId, bookingId, ct))!); // nothing to change

        var now = time.GetUtcNow().UtcDateTime;
        booking.DepositReceivedAtUtc = received ? now : null;
        booking.UpdatedAtUtc = now;
        db.AuditLogs.Add(new AuditLog
        {
            BusinessId = businessId,
            BookingId = booking.Id,
            ActorType = actor.TypeCode,
            ActorId = actor.Id,
            ActorLabel = actor.Label,
            Action = received ? "DEPOSIT_RECEIVED" : "DEPOSIT_UNDONE",
            FromStatus = booking.Status.ToDb(),
            ToStatus = booking.Status.ToDb(),
            Reason = DisplayFormat.Price(booking.DepositSnapshot),
            AtUtc = now,
        });
        await db.SaveChangesAsync(ct);
        return ServiceResult<BookingSummary>.Ok((await LoadSummaryAsync(businessId, bookingId, ct))!);
    }

    private async Task<ServiceResult<BookingSummary>> ApplyTransitionAsync(
        AppDbContext db, Booking booking, BookingStatus to, BookingActor actor,
        string? reason, uint? expectedVersion, CancellationToken ct)
    {
        var now = time.GetUtcNow().UtcDateTime;
        var cutoff = await db.Businesses.Where(b => b.Id == booking.BusinessId)
            .Select(b => b.CancelCutoffHours).SingleAsync(ct);

        var from = booking.Status;
        var policy = to switch
        {
            BookingStatus.Cancelled => BookingPolicy.CanCancel(from, booking.StartsAtUtc, now, actor.Kind, cutoff),
            BookingStatus.Completed when actor.Kind == ActorKind.Customer => PolicyResult.No(Text.NotAllowed),
            BookingStatus.Completed => BookingPolicy.CanMarkCompleted(from, booking.StartsAtUtc, now, booking.LocationTimeZone),
            BookingStatus.NoShow when actor.Kind == ActorKind.Customer => PolicyResult.No(Text.NotAllowed),
            BookingStatus.NoShow => BookingPolicy.CanMarkNoShow(from, booking.StartsAtUtc, now, booking.LocationTimeZone),
            _ when actor.Kind == ActorKind.Customer => PolicyResult.No(Text.NotAllowed),
            _ => BookingStateMachine.CanTransition(from, to) ? PolicyResult.Ok() : PolicyResult.No(Text.NotAllowed),
        };
        if (!policy.Allowed)
            return ServiceResult<BookingSummary>.Fail(FailureKind.PolicyViolation, policy.Reason ?? Text.NotAllowed);

        var entry = db.Entry(booking);
        if (expectedVersion is { } version)
            entry.Property<uint>("xmin").OriginalValue = version;

        booking.Status = to;
        booking.UpdatedAtUtc = now;
        if (to == BookingStatus.Cancelled)
        {
            booking.CancelledBy = actor.TypeCode;
            booking.CancelReason = reason;
            booking.CancelledAtUtc = now;
        }
        db.AuditLogs.Add(new AuditLog
        {
            BusinessId = booking.BusinessId,
            BookingId = booking.Id,
            ActorType = actor.TypeCode,
            ActorId = actor.Id,
            ActorLabel = actor.Label,
            Action = to.ToDb(),
            FromStatus = from.ToDb(),
            ToStatus = to.ToDb(),
            Reason = reason,
            AtUtc = now,
        });

        try
        {
            await db.SaveChangesAsync(ct); // booking update and audit row commit together
        }
        catch (DbUpdateConcurrencyException)
        {
            return ServiceResult<BookingSummary>.Fail(FailureKind.Stale, Text.BookingChanged);
        }

        return ServiceResult<BookingSummary>.Ok((await LoadSummaryAsync(booking.BusinessId, booking.Id, ct))!);
    }

    // ---------------------------------------------------------------- queries and helpers

    /// <summary>The booking the manage token points at, or null. The token authorizes exactly this one booking.</summary>
    public async Task<BookingDetails?> GetByTokenAsync(string? manageToken, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(manageToken)) return null;

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var row = await db.Bookings.AsNoTracking()
            .Where(b => b.ManageToken == manageToken)
            .Select(b => new
            {
                Summary = new BookingSummary(
                    b.Id, b.BusinessId, b.StaffId, b.Staff.Name, b.ServiceId, b.ServiceNameSnapshot,
                    b.StartsAtUtc, b.EndsAtUtc, b.Status, b.ManageToken, b.PriceSnapshot, EF.Property<uint>(b, "xmin"),
                    b.RegularPriceSnapshot, b.DiscountPercentSnapshot, b.PaidAmount,
                    db.BookingExtras.Where(x => x.BookingId == b.Id).Sum(x => (decimal?)x.PriceSnapshot) ?? 0m),
                BusinessName = db.Businesses.Where(x => x.Id == b.BusinessId).Select(x => x.Name).First(),
                CutoffHours = db.Businesses.Where(x => x.Id == b.BusinessId).Select(x => x.CancelCutoffHours).First(),
                CustomerName = b.Customer.Name,
                TimeZone = b.LocationTimeZone,
                BrandColor = db.Businesses.Where(x => x.Id == b.BusinessId).Select(x => x.BrandColor).First(),
                Deposit = b.DepositSnapshot,
                DepositReceived = b.DepositReceivedAtUtc != null,
                PayInstructions = db.Businesses.Where(x => x.Id == b.BusinessId).Select(x => x.PayInstructions).First(),
            })
            .FirstOrDefaultAsync(ct);
        if (row is null) return null;

        var policy = BookingPolicy.CanCancel(
            row.Summary.Status, row.Summary.StartsAtUtc, time.GetUtcNow().UtcDateTime, ActorKind.Customer, row.CutoffHours);
        var refusal = !policy.Allowed && !row.Summary.Status.IsTerminal() ? policy.Reason : null;
        var extras = await db.BookingExtras.AsNoTracking().Where(x => x.BookingId == row.Summary.Id && x.BusinessId == row.Summary.BusinessId)
            .OrderBy(x => x.CreatedAtUtc).ThenBy(x => x.ServiceNameSnapshot)
            .Select(x => new ExtraLine(x.Id, x.ServiceNameSnapshot, x.PriceSnapshot, x.RegularPriceSnapshot, x.DiscountPercentSnapshot)).ToListAsync(ct);
        var canMove = BookingPolicy.CanReschedule(
            row.Summary.Status, row.Summary.StartsAtUtc, time.GetUtcNow().UtcDateTime, ActorKind.Customer, row.CutoffHours).Allowed;
        return new BookingDetails(row.Summary, row.BusinessName, row.TimeZone, row.CutoffHours, row.CustomerName, policy.Allowed, refusal, row.BrandColor, extras, canMove, row.Deposit, row.DepositReceived, row.PayInstructions);
    }

    public async Task<BookingSummary?> LoadSummaryAsync(Guid businessId, Guid bookingId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await Summaries(db, db.Bookings.AsNoTracking().Where(b => b.BusinessId == businessId && b.Id == bookingId))
            .FirstOrDefaultAsync(ct);
    }

    private static Task<BookingSummary?> FindByIdempotencyKeyAsync(AppDbContext db, Guid businessId, string key, CancellationToken ct) =>
        Summaries(db, db.Bookings.AsNoTracking().Where(b => b.BusinessId == businessId && b.IdempotencyKey == key))
            .FirstOrDefaultAsync(ct);

    private static IQueryable<BookingSummary> Summaries(AppDbContext db, IQueryable<Booking> bookings) =>
        bookings.Select(b => new BookingSummary(
            b.Id, b.BusinessId, b.StaffId, b.Staff.Name, b.ServiceId, b.ServiceNameSnapshot,
            b.StartsAtUtc, b.EndsAtUtc, b.Status, b.ManageToken, b.PriceSnapshot,
            EF.Property<uint>(b, "xmin"), b.RegularPriceSnapshot, b.DiscountPercentSnapshot, b.PaidAmount,
            db.BookingExtras.Where(x => x.BookingId == b.Id).Sum(x => (decimal?)x.PriceSnapshot) ?? 0m));

    private static Task<Business?> LoadBusinessAsync(AppDbContext db, Guid businessId, CancellationToken ct) =>
        db.Businesses.AsNoTracking().FirstOrDefaultAsync(b => b.Id == businessId, ct);

    private static Task<Service?> LoadServiceAsync(AppDbContext db, Guid businessId, Guid serviceId, CancellationToken ct) =>
        db.Services.AsNoTracking().FirstOrDefaultAsync(s => s.Id == serviceId && s.BusinessId == businessId && s.IsActive, ct);

    /// <summary>Active staff of this business who offer the service, optionally narrowed to one staff member.</summary>
    private static Task<List<Staff>> LoadEligibleStaffAsync(
        AppDbContext db, Guid businessId, Guid serviceId, Guid? staffId, CancellationToken ct) =>
        db.StaffMembers.AsNoTracking()
            .Where(s => s.BusinessId == businessId && s.IsActive
                        && (staffId == null || s.Id == staffId)
                        && s.StaffServices.Any(ss => ss.ServiceId == serviceId && ss.BusinessId == businessId))
            .OrderBy(s => s.SortOrder).ThenBy(s => s.Name)
            .ToListAsync(ct);

    private static Task<int> CountActiveOnDayAsync(AppDbContext db, Business business, Guid staffId, DateOnly localDate, CancellationToken ct)
    {
        var (dayStart, dayEnd) = LocalTime.DayRangeUtc(localDate, business.TimeZone);
        return db.Bookings.CountAsync(b => b.BusinessId == business.Id && b.StaffId == staffId
            && b.StartsAtUtc >= dayStart && b.StartsAtUtc < dayEnd
            && (b.Status == BookingStatus.Held || b.Status == BookingStatus.Pending || b.Status == BookingStatus.Confirmed), ct);
    }

    /// <summary>
    /// Free starts for one staff member whose local date is <paramref name="localDate"/>. Includes the tail of a
    /// window that crossed midnight from the previous day. Staff-created (walk-in) bookings skip the lead time and may
    /// begin in the slot that is in progress right now (BUSINESS_RULES 8.2); everything else still applies.
    /// </summary>
    private async Task<IReadOnlyList<DateTime>> FreeStartsForStaffAsync(
        AppDbContext db, Business business, Service service, Guid staffId, DateOnly localDate, bool walkIn, CancellationToken ct,
        Guid? ignoreBookingId = null)
    {
        var (dayStart, dayEnd) = LocalTime.DayRangeUtc(localDate, business.TimeZone);
        var rangeStart = dayStart.AddDays(-1);
        var rangeEnd = dayEnd.AddDays(1);

        var occupied = await db.Bookings.AsNoTracking()
            .Where(b => b.BusinessId == business.Id && b.StaffId == staffId && b.Id != ignoreBookingId
                        && b.StartsAtUtc < rangeEnd && b.OccupiedUntilUtc > rangeStart
                        && (b.Status == BookingStatus.Held || b.Status == BookingStatus.Pending || b.Status == BookingStatus.Confirmed))
            .Select(b => new TimeRange(b.StartsAtUtc, b.OccupiedUntilUtc))
            .ToListAsync(ct);

        var blackouts = await db.Blackouts.AsNoTracking()
            .Where(b => b.BusinessId == business.Id && (b.StaffId == null || b.StaffId == staffId)
                        && b.StartsAtUtc < rangeEnd && b.EndsAtUtc > rangeStart)
            .Select(b => new TimeRange(b.StartsAtUtc, b.EndsAtUtc))
            .ToListAsync(ct);

        var previous = localDate.AddDays(-1);

        // Closures that repeat (and are switched on) block the whole local day or just their time window, like a one-off closure.
        var repeating = await db.WeeklyClosures.AsNoTracking()
            .Where(c => c.BusinessId == business.Id && c.IsActive && (c.StaffId == null || c.StaffId == staffId))
            .ToListAsync(ct);
        var zone = LocalTime.Zone(business.TimeZone);
        foreach (var date in new[] { previous, localDate })
            foreach (var rule in repeating.Where(r => r.DayOfWeek == null || r.DayOfWeek == (short)date.DayOfWeek))
            {
                if (rule.StartTime is { } from && rule.EndTime is { } to)
                    blackouts.Add(new TimeRange(
                        LocalTime.ToUtc(date.ToDateTime(from), zone), LocalTime.ToUtc(date.ToDateTime(to), zone)));
                else
                {
                    var (closedStart, closedEnd) = LocalTime.DayRangeUtc(date, business.TimeZone);
                    blackouts.Add(new TimeRange(closedStart, closedEnd));
                }
            }

        // The shop's opening hours from Settings are a hard limit: whatever a staff member's own hours say, nothing outside
        // them can be booked. Everything outside the shop windows becomes a blackout. Shops that never set them are unaffected.
        var shopHours = await db.BusinessHours.AsNoTracking().Where(h => h.BusinessId == business.Id).ToListAsync(ct);
        if (shopHours.Count > 0)
        {
            var allowed = new List<(DateTime Start, DateTime End)>();
            foreach (var date in new[] { previous, localDate })
                foreach (var h in shopHours.Where(h => h.DayOfWeek == (short)date.DayOfWeek))
                {
                    var from = date.ToDateTime(h.OpenTime);
                    var to = h.CloseTime <= h.OpenTime ? date.AddDays(1).ToDateTime(h.CloseTime) : date.ToDateTime(h.CloseTime);
                    allowed.Add((from, to));
                }
            var cursor = previous.ToDateTime(TimeOnly.MinValue);
            var spanEnd = localDate.AddDays(1).ToDateTime(TimeOnly.MinValue);
            foreach (var (from, to) in allowed.OrderBy(a => a.Start))
            {
                if (from > cursor) blackouts.Add(new TimeRange(LocalTime.ToUtc(cursor, zone), LocalTime.ToUtc(from, zone)));
                if (to > cursor) cursor = to;
            }
            if (cursor < spanEnd) blackouts.Add(new TimeRange(LocalTime.ToUtc(cursor, zone), LocalTime.ToUtc(spanEnd, zone)));
        }

        var days = new[] { (short)localDate.DayOfWeek, (short)previous.DayOfWeek };
        var hours = await db.WorkingHours.AsNoTracking()
            .Where(h => h.StaffId == staffId && days.Contains(h.DayOfWeek))
            .ToListAsync(ct);

        var now = time.GetUtcNow().UtcDateTime;
        var lead = business.LeadTimeMinutes;
        if (walkIn)
        {
            lead = 0;
            now = now.AddMinutes(-business.SlotMinutes);
        }

        var result = new SortedSet<DateTime>();
        foreach (var date in new[] { previous, localDate })
        {
            var windows = hours.Where(h => h.DayOfWeek == (short)date.DayOfWeek)
                .Select(h => new WorkWindow(h.OpenTime, h.CloseTime)).ToList();
            if (windows.Count == 0) continue;

            var starts = AvailabilityCalculator.FreeStarts(new AvailabilityRequest(
                business.TimeZone, date, windows, service.DurationMinutes, service.BufferMinutes, business.SlotMinutes,
                occupied, blackouts, now, lead, business.HorizonDays));

            foreach (var s in starts)
                if (LocalTime.LocalDateOf(s, business.TimeZone) == localDate)
                    result.Add(s);
        }
        return result.ToList();
    }

    private static Dictionary<string, string> ValidateInput(
        CreateBookingRequest r, out string name, out string phone, out string? email)
    {
        var errors = new Dictionary<string, string>();

        name = (r.CustomerName ?? "").Trim();
        if (name.Length is < 1 or > 80) errors["name"] = Text.NameInvalid;

        // One canonical form, so the same person is recognised however the number was typed (+63 917…, 0917…).
        phone = PhoneNumber.Normalize(Regex.Replace(r.Phone ?? "", @"[\s\-().]", ""));
        if (r.Actor.Kind == ActorKind.Staff && phone.Length == 0)
            phone = DisplayFormat.WalkInPhonePrefix + Guid.NewGuid().ToString("N"); // a walk-in with no number: its own customer, never matched later
        else if (!PhonePattern().IsMatch(phone)) errors["phone"] = Text.PhoneInvalid;

        var rawEmail = (r.Email ?? "").Trim();
        email = rawEmail.Length == 0 ? null : rawEmail;
        if (email is not null && (email.Length > 120 || !MailAddress.TryCreate(email, out var parsed) || parsed.Address != email))
            errors["email"] = Text.EmailInvalid;

        var key = (r.IdempotencyKey ?? "").Trim();
        if (key.Length is < 1 or > 100) errors["idempotencyKey"] = Text.IdempotencyKeyInvalid;

        return errors;
    }

    /// <summary>EF wraps some PostgreSQL errors (for example deadlocks, which it classes as transient), so search the chain.</summary>
    private static PostgresException? FindPostgres(Exception? ex)
    {
        for (; ex is not null; ex = ex.InnerException)
            if (ex is PostgresException pg) return pg;
        return null;
    }

    private static string NewManageToken() =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).Replace('+', '-').Replace('/', '_').TrimEnd('=');
}
