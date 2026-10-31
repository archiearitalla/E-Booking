using System.Security.Cryptography;
using System.Text.RegularExpressions;
using VeskaFlow.Core.Domain;
using VeskaFlow.Core.Scheduling;
using VeskaFlow.Web.Data;
using Microsoft.EntityFrameworkCore;

namespace VeskaFlow.Web.Services;

/// <summary>What the person typed for a sale done without a booking. Date and time are in the shop's time zone.</summary>
public sealed record ManualSaleInput(
    Guid ServiceId, string? Note, DateOnly Date, TimeOnly? Time, decimal? Amount, string? CustomerName, string? Phone, Guid? StaffId, string IdempotencyKey);

/// <summary>
/// "Record a sale" (BUSINESS_RULES 6.y20): a service that was done and paid without a booking in the system is saved as a completed
/// visit with its payment total, so reports and the customer's history count it. Only when the shop switched it on in Settings. A customer
/// with no number is saved as a walk-in. It never touches the schedule: a completed visit holds no time. Every query is scoped by the
/// caller's business id, and the entry is written to the audit log.
/// </summary>
public sealed partial class ManualSaleService(IDbContextFactory<AppDbContext> dbFactory, TimeProvider time)
{
    public const int MaxDaysBack = 366;

    [GeneratedRegex(@"^\+?\d{7,15}$")]
    private static partial Regex PhonePattern();

    public async Task<ServiceResult<Guid>> RecordAsync(Guid businessId, ManualSaleInput input, BookingActor actor, CancellationToken ct = default, Guid? onlyStaffId = null)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var business = await db.Businesses.AsNoTracking().Where(b => b.Id == businessId)
            .Select(b => new { b.TimeZone, b.AllowManualSales }).FirstOrDefaultAsync(ct);
        if (business is null) return ServiceResult<Guid>.Fail(FailureKind.NotFound, Text.NotFound);
        if (!business.AllowManualSales) return ServiceResult<Guid>.Fail(FailureKind.PolicyViolation, Text.ManualSaleOff);

        var key = (input.IdempotencyKey ?? "").Trim();
        if (key.Length is < 1 or > 100) return ServiceResult<Guid>.Invalid(new Dictionary<string, string> { ["idempotencyKey"] = Text.IdempotencyKeyInvalid });
        // The same form sent twice (a double tap) is one sale.
        var again = await db.Bookings.AsNoTracking().Where(b => b.BusinessId == businessId && b.IdempotencyKey == key).Select(b => (Guid?)b.Id).FirstOrDefaultAsync(ct);
        if (again is { } already) return ServiceResult<Guid>.Ok(already);

        var service = await db.Services.AsNoTracking().FirstOrDefaultAsync(s => s.Id == input.ServiceId && s.BusinessId == businessId && s.IsActive, ct);
        if (service is null) return ServiceResult<Guid>.Fail(FailureKind.NotFound, Text.NotFound);

        var errors = new Dictionary<string, string>();
        var name = service.Name;
        if (service.IsOther)
        {
            var words = (input.Note ?? "").Trim();
            if (words.Length is < 1 or > BookingExtraService.MaxNote) errors["note"] = Text.ExtraNoteInvalid;
            else name = $"{service.Name}: {words}";
        }

        if (input.Amount is not { } typed || typed < 0 || typed > BookingExtraService.MaxPrice) errors["amount"] = Text.ManualSaleAmountInvalid;
        var amount = input.Amount is { } a ? Math.Round(a, 2, MidpointRounding.AwayFromZero) : 0m;

        var now = time.GetUtcNow().UtcDateTime;
        var today = LocalTime.LocalDateOf(now, business.TimeZone);
        if (input.Date > today || input.Date < today.AddDays(-MaxDaysBack)) errors["date"] = Text.ManualSaleDateInvalid;

        var who = (input.CustomerName ?? "").Trim();
        if (who.Length > 80) errors["name"] = Text.NameInvalid;
        var phone = PhoneNumber.Normalize(Regex.Replace(input.Phone ?? "", @"[\s\-().]", ""));
        if ((phone.Length > 0 || !string.IsNullOrWhiteSpace(input.Phone)) && !PhonePattern().IsMatch(phone)) errors["phone"] = Text.PhoneInvalid;

        var staffId = onlyStaffId ?? input.StaffId;
        var staff = staffId is { } sid
            ? await db.StaffMembers.AsNoTracking().FirstOrDefaultAsync(s => s.Id == sid && s.BusinessId == businessId && s.IsActive, ct)
            : await db.StaffMembers.AsNoTracking().Where(s => s.BusinessId == businessId && s.IsActive).OrderBy(s => s.SortOrder).ThenBy(s => s.Name).FirstOrDefaultAsync(ct);
        if (staff is null) return ServiceResult<Guid>.Fail(FailureKind.NotFound, Text.NotFound);
        if (errors.Count > 0) return ServiceResult<Guid>.Invalid(errors);

        var isWalkIn = phone.Length == 0;
        if (isWalkIn) phone = DisplayFormat.WalkInPhonePrefix + Guid.NewGuid().ToString("N");
        if (who.Length == 0) who = Text.WalkInNameDefault;

        var local = input.Date.ToDateTime(input.Time ?? new TimeOnly(12, 0));
        var start = LocalTime.ToUtc(local, LocalTime.Zone(business.TimeZone));
        if (start > now) start = now; // a time later today than now is still today's sale
        var end = start.AddMinutes(Math.Max(1, service.DurationMinutes));

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        // An existing customer with this number keeps the name on file.
        var customerId = (await db.Database.SqlQuery<Guid>($"""
            INSERT INTO customer (id, business_id, name, phone)
            VALUES ({Guid.NewGuid()}, {businessId}, {who}, {phone})
            ON CONFLICT (business_id, phone) DO UPDATE SET name = customer.name
            RETURNING id AS "Value"
            """).ToListAsync(ct)).Single();

        var booking = new Booking
        {
            BusinessId = businessId, CustomerId = customerId, StaffId = staff.Id, ServiceId = service.Id,
            StartsAtUtc = start, EndsAtUtc = end, OccupiedUntilUtc = end, LocationTimeZone = business.TimeZone, Status = BookingStatus.Completed,
            PriceSnapshot = service.IsOther ? amount : service.Price, ServiceNameSnapshot = name, PaidAmount = amount,
            ManageToken = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).Replace('+', '-').Replace('/', '_').TrimEnd('='),
            IdempotencyKey = key, CreatedAtUtc = now, UpdatedAtUtc = now,
        };
        db.Bookings.Add(booking);
        db.AuditLogs.Add(new AuditLog
        {
            BusinessId = businessId, BookingId = booking.Id, ActorType = actor.TypeCode, ActorId = actor.Id, ActorLabel = actor.Label,
            Action = "MANUAL_SALE", ToStatus = "COMPLETED", AtUtc = now,
            Reason = $"{name}: {DisplayFormat.Price(amount)} on {input.Date:yyyy-MM-dd}{(isWalkIn ? " (walk-in)" : "")}",
        });
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException ex) when (ex.InnerException is Npgsql.PostgresException { SqlState: "23505" })
        {
            // A parallel send of the same form won: hand back its sale.
            var won = await db.Bookings.AsNoTracking().Where(b => b.BusinessId == businessId && b.IdempotencyKey == key).Select(b => b.Id).FirstAsync(ct);
            return ServiceResult<Guid>.Ok(won);
        }
        await tx.CommitAsync(ct);
        return ServiceResult<Guid>.Ok(booking.Id);
    }
}
