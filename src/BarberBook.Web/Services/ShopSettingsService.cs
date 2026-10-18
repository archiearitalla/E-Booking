using BarberBook.Core.Domain;
using BarberBook.Core.Scheduling;
using BarberBook.Web.Data;
using Microsoft.EntityFrameworkCore;

namespace BarberBook.Web.Services;

public sealed record ShopSettings(
    string Slug, string Plan, string Name, string TimeZone, int SlotMinutes, int LeadTimeMinutes, int HorizonDays, int CancelCutoffHours);

public sealed record SettingsInput(
    string? Name, string? TimeZone, int SlotMinutes, int LeadTimeMinutes, int HorizonDays, int CancelCutoffHours);

public sealed record ClosureRow(Guid Id, Guid? StaffId, string? StaffName, DateOnly FromDate, DateOnly ToDate, string? Reason, int AffectedBookings);

public sealed record ClosureInput(Guid? StaffId, DateOnly FromDate, DateOnly ToDate, string? Reason);

/// <summary>Business settings and closures (blackouts). Business id always comes from the signed-in user.</summary>
public sealed class ShopSettingsService(IDbContextFactory<AppDbContext> dbFactory, TimeProvider time)
{
    public static readonly int[] AllowedSlotMinutes = [5, 10, 15, 20, 30, 60];

    // ------------------------------------------------------------------ settings

    public async Task<ShopSettings?> GetSettingsAsync(Guid businessId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.Businesses.AsNoTracking().Where(b => b.Id == businessId)
            .Select(b => new ShopSettings(b.Slug, b.Plan, b.Name, b.TimeZone, b.SlotMinutes, b.LeadTimeMinutes, b.HorizonDays, b.CancelCutoffHours))
            .FirstOrDefaultAsync(ct);
    }

    /// <summary>Saving never touches existing bookings; a changed time zone is only reported so the owner can review hours.</summary>
    public async Task<ServiceResult<bool>> SaveSettingsAsync(Guid businessId, SettingsInput input, CancellationToken ct = default)
    {
        var errors = new Dictionary<string, string>();
        var name = (input.Name ?? "").Trim();
        if (name.Length is < 1 or > 80) errors["name"] = Text.BusinessNameInvalid;
        var zone = (input.TimeZone ?? "").Trim();
        if (zone.Length == 0 || !LocalTime.IsValidZone(zone)) errors["timeZone"] = Text.TimeZoneInvalid;
        if (!AllowedSlotMinutes.Contains(input.SlotMinutes)) errors["slotMinutes"] = Text.SlotMinutesInvalid;
        if (input.LeadTimeMinutes is < 0 or > 525_600) errors["leadTimeMinutes"] = Text.LeadTimeInvalid;
        if (input.HorizonDays is < 1 or > 365) errors["horizonDays"] = Text.HorizonInvalid;
        if (input.CancelCutoffHours is < 0 or > 8_760) errors["cancelCutoffHours"] = Text.CutoffInvalid;
        if (errors.Count > 0) return ServiceResult<bool>.Invalid(errors);

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var business = await db.Businesses.FirstOrDefaultAsync(b => b.Id == businessId, ct);
        if (business is null) return ServiceResult<bool>.Fail(FailureKind.NotFound, Text.NotFound);

        var zoneChanged = !string.Equals(business.TimeZone, zone, StringComparison.Ordinal);
        business.Name = name;
        business.TimeZone = zone;
        business.SlotMinutes = input.SlotMinutes;
        business.LeadTimeMinutes = input.LeadTimeMinutes;
        business.HorizonDays = input.HorizonDays;
        business.CancelCutoffHours = input.CancelCutoffHours;
        await db.SaveChangesAsync(ct);
        return ServiceResult<bool>.Ok(zoneChanged);
    }

    // ------------------------------------------------------------------ closures

    /// <summary>Closures that have not ended yet, soonest first, with the number of active bookings each one overlaps.</summary>
    public async Task<IReadOnlyList<ClosureRow>> ListClosuresAsync(Guid businessId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var business = await db.Businesses.AsNoTracking().FirstOrDefaultAsync(b => b.Id == businessId, ct);
        if (business is null) return [];

        var now = time.GetUtcNow().UtcDateTime;
        var rows = await db.Blackouts.AsNoTracking()
            .Where(b => b.BusinessId == businessId && b.EndsAtUtc > now)
            .OrderBy(b => b.StartsAtUtc)
            .Select(b => new
            {
                b.Id, b.StaffId, StaffName = b.StaffId == null ? null : db.StaffMembers.Where(s => s.Id == b.StaffId).Select(s => s.Name).FirstOrDefault(),
                b.StartsAtUtc, b.EndsAtUtc, b.Reason,
            })
            .ToListAsync(ct);

        var result = new List<ClosureRow>();
        foreach (var r in rows)
        {
            var affected = await CountAffectedAsync(db, businessId, r.StaffId, r.StartsAtUtc, r.EndsAtUtc, ct);
            result.Add(new ClosureRow(r.Id, r.StaffId, r.StaffName,
                LocalTime.LocalDateOf(r.StartsAtUtc, business.TimeZone),
                LocalTime.LocalDateOf(r.EndsAtUtc.AddMinutes(-1), business.TimeZone), r.Reason, affected));
        }
        return result;
    }

    /// <summary>
    /// Closes whole local days, for the whole shop or one staff member. Existing bookings are NOT cancelled;
    /// the outcome tells the owner how many are affected so they can handle each one deliberately.
    /// </summary>
    public async Task<ServiceResult<SaveOutcome>> AddClosureAsync(Guid businessId, ClosureInput input, CancellationToken ct = default)
    {
        var errors = new Dictionary<string, string>();
        var reason = string.IsNullOrWhiteSpace(input.Reason) ? null : input.Reason.Trim();
        if (reason is { Length: > 120 }) errors["reason"] = Text.ReasonTooLong;
        if (input.ToDate < input.FromDate) errors["toDate"] = Text.ClosureRangeInvalid;
        else if (input.ToDate.DayNumber - input.FromDate.DayNumber > 365) errors["toDate"] = Text.ClosureTooLong;
        if (errors.Count > 0) return ServiceResult<SaveOutcome>.Invalid(errors);

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var business = await db.Businesses.AsNoTracking().FirstOrDefaultAsync(b => b.Id == businessId, ct);
        if (business is null) return ServiceResult<SaveOutcome>.Fail(FailureKind.NotFound, Text.NotFound);

        if (input.StaffId is { } staffId
            && !await db.StaffMembers.AnyAsync(s => s.Id == staffId && s.BusinessId == businessId, ct))
            return ServiceResult<SaveOutcome>.Fail(FailureKind.NotFound, Text.NotFound);

        var start = LocalTime.DayRangeUtc(input.FromDate, business.TimeZone).StartUtc;
        var end = LocalTime.DayRangeUtc(input.ToDate, business.TimeZone).EndUtc;
        var blackout = new Blackout { BusinessId = businessId, StaffId = input.StaffId, StartsAtUtc = start, EndsAtUtc = end, Reason = reason };
        db.Blackouts.Add(blackout);
        await db.SaveChangesAsync(ct);

        return ServiceResult<SaveOutcome>.Ok(new SaveOutcome(blackout.Id, await CountAffectedAsync(db, businessId, input.StaffId, start, end, ct)));
    }

    public async Task<ServiceResult<bool>> DeleteClosureAsync(Guid businessId, Guid id, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var removed = await db.Blackouts.Where(b => b.Id == id && b.BusinessId == businessId).ExecuteDeleteAsync(ct);
        return removed == 0 ? ServiceResult<bool>.Fail(FailureKind.NotFound, Text.NotFound) : ServiceResult<bool>.Ok(true);
    }

    private static Task<int> CountAffectedAsync(AppDbContext db, Guid businessId, Guid? staffId, DateTime start, DateTime end, CancellationToken ct) =>
        db.Bookings.CountAsync(b => b.BusinessId == businessId
            && (staffId == null || b.StaffId == staffId)
            && b.StartsAtUtc < end && b.EndsAtUtc > start
            && (b.Status == BookingStatus.Held || b.Status == BookingStatus.Pending || b.Status == BookingStatus.Confirmed), ct);
}
