using VeskaFlow.Core.Domain;
using VeskaFlow.Core.Scheduling;
using VeskaFlow.Web.Data;
using Microsoft.EntityFrameworkCore;

namespace VeskaFlow.Web.Services;

public sealed record ShopSettings(
    string Slug, string Plan, string Name, string TimeZone, int SlotMinutes, int LeadTimeMinutes, int HorizonDays, int CancelCutoffHours,
    string BrandColor = BrandColors.Default, DateTime? TrialEndsAtUtc = null);

/// <summary><c>BrandColor</c> null leaves the saved color unchanged.</summary>
public sealed record SettingsInput(
    string? Name, string? TimeZone, int SlotMinutes, int LeadTimeMinutes, int HorizonDays, int CancelCutoffHours,
    string? BrandColor = null);

/// <summary>The accent colors a shop can pick. The keys match the CHECK constraint and the <c>data-theme</c> rules in site.css.</summary>
public static class BrandColors
{
    public const string Default = "teal";

    public static readonly IReadOnlyList<(string Key, string Label)> All =
    [
        ("teal", Text.ColorTeal), ("blue", Text.ColorBlue), ("orange", Text.ColorOrange),
        ("rose", Text.ColorRose), ("violet", Text.ColorViolet), ("slate", Text.ColorSlate),
    ];

    public static bool IsValid(string? key) => key is not null && All.Any(c => c.Key == key);

    /// <summary>Theme key for a page: unknown or missing values fall back to the default.</summary>
    public static string Normalize(string? key) => IsValid(key) ? key! : Default;
}

/// <summary>A one-off closure. With <c>StartTime</c>/<c>EndTime</c> it runs from the first day at the start time to the last day at the end time; without them it closes whole days.</summary>
public sealed record ClosureRow(Guid Id, Guid? StaffId, string? StaffName, DateOnly FromDate, DateOnly ToDate, string? Reason, int AffectedBookings,
    TimeOnly? StartTime = null, TimeOnly? EndTime = null);

public sealed record ClosureInput(Guid? StaffId, DateOnly FromDate, DateOnly ToDate, string? Reason, TimeOnly? StartTime = null, TimeOnly? EndTime = null);

/// <summary>
/// A closure that repeats: on one weekday or every day (<c>DayOfWeek</c> null), for the whole day or a time window
/// (<c>StartTime</c>/<c>EndTime</c>). <c>AffectedBookings</c> counts upcoming active bookings inside it (never cancelled).
/// </summary>
public sealed record WeeklyClosureRow(Guid Id, Guid? StaffId, string? StaffName, short? DayOfWeek, string? Reason, bool IsActive, int AffectedBookings,
    TimeOnly? StartTime = null, TimeOnly? EndTime = null);

public sealed record WeeklyClosureInput(Guid? StaffId, int? DayOfWeek, string? Reason, TimeOnly? StartTime = null, TimeOnly? EndTime = null);

/// <summary>Business settings and closures (blackouts). Business id always comes from the signed-in user.</summary>
public sealed class ShopSettingsService(IDbContextFactory<AppDbContext> dbFactory, TimeProvider time)
{
    public static readonly int[] AllowedSlotMinutes = [5, 10, 15, 20, 30, 60];

    // ------------------------------------------------------------------ settings

    public async Task<ShopSettings?> GetSettingsAsync(Guid businessId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.Businesses.AsNoTracking().Where(b => b.Id == businessId)
            .Select(b => new ShopSettings(b.Slug, b.Plan, b.Name, b.TimeZone, b.SlotMinutes, b.LeadTimeMinutes, b.HorizonDays, b.CancelCutoffHours, b.BrandColor, b.TrialEndsAtUtc))
            .FirstOrDefaultAsync(ct);
    }

    /// <summary>True when the business has a trial end date that has passed. Read from the database on every call; the cookie claim is only for display.</summary>
    public async Task<bool> TrialEndedAsync(Guid businessId, CancellationToken ct = default) =>
        IsEnded(await TrialEndsAtAsync(businessId, ct));

    /// <summary>The trial end date from the database (null when there is no trial limit).</summary>
    public async Task<DateTime?> TrialEndsAtAsync(Guid businessId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.Businesses.AsNoTracking().Where(b => b.Id == businessId)
            .Select(b => b.TrialEndsAtUtc).FirstOrDefaultAsync(ct);
    }

    public bool IsEnded(DateTime? trialEndsAtUtc) =>
        trialEndsAtUtc is { } e && time.GetUtcNow().UtcDateTime > DateTime.SpecifyKind(e, DateTimeKind.Utc);

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
        if (input.BrandColor is not null && !BrandColors.IsValid(input.BrandColor)) errors["brandColor"] = Text.BrandColorInvalid;
        if (errors.Count > 0) return ServiceResult<bool>.Invalid(errors);

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var business = await db.Businesses.FirstOrDefaultAsync(b => b.Id == businessId, ct);
        if (business is null) return ServiceResult<bool>.Fail(FailureKind.NotFound, Text.NotFound);

        if (input.BrandColor is not null) business.BrandColor = input.BrandColor;
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
            var localStart = LocalTime.ToLocal(r.StartsAtUtc, business.TimeZone);
            var localEnd = LocalTime.ToLocal(r.EndsAtUtc, business.TimeZone);
            // A closure that does not begin and end at midnight is a time window.
            var isWindow = localStart.TimeOfDay != TimeSpan.Zero || localEnd.TimeOfDay != TimeSpan.Zero;
            result.Add(new ClosureRow(r.Id, r.StaffId, r.StaffName,
                DateOnly.FromDateTime(localStart),
                DateOnly.FromDateTime(isWindow ? localEnd : localEnd.AddMinutes(-1)), r.Reason, affected,
                isWindow ? TimeOnly.FromDateTime(localStart) : null, isWindow ? TimeOnly.FromDateTime(localEnd) : null));
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
        if ((input.StartTime is null) != (input.EndTime is null)) errors["startTime"] = Text.ClosureTimesBoth;
        else if (input.StartTime is { } st && input.EndTime is { } et && input.FromDate == input.ToDate && et <= st)
            errors["endTime"] = Text.ClosureTimesOrder;
        if (errors.Count > 0) return ServiceResult<SaveOutcome>.Invalid(errors);

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var business = await db.Businesses.AsNoTracking().FirstOrDefaultAsync(b => b.Id == businessId, ct);
        if (business is null) return ServiceResult<SaveOutcome>.Fail(FailureKind.NotFound, Text.NotFound);

        if (input.StaffId is { } staffId
            && !await db.StaffMembers.AnyAsync(s => s.Id == staffId && s.BusinessId == businessId, ct))
            return ServiceResult<SaveOutcome>.Fail(FailureKind.NotFound, Text.NotFound);

        if (input.StartTime is { } ws && input.EndTime is { } we && input.FromDate == input.ToDate
            && !await TouchesOpeningHoursAsync(db, businessId, input.StaffId, [(short)input.FromDate.DayOfWeek], ws, we, ct))
            return ServiceResult<SaveOutcome>.Invalid(new Dictionary<string, string> { ["startTime"] = Text.ClosureOutsideHours });

        var zone = LocalTime.Zone(business.TimeZone);
        var start = input.StartTime is { } s
            ? LocalTime.ToUtc(input.FromDate.ToDateTime(s), zone)
            : LocalTime.DayRangeUtc(input.FromDate, business.TimeZone).StartUtc;
        var end = input.EndTime is { } e
            ? LocalTime.ToUtc(input.ToDate.ToDateTime(e), zone)
            : LocalTime.DayRangeUtc(input.ToDate, business.TimeZone).EndUtc;
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

    // ------------------------------------------------------------------ weekly closures (closed every Sunday, and so on)

    /// <summary>All weekly closures, Monday first, switched-off ones included (they show as off).</summary>
    public async Task<IReadOnlyList<WeeklyClosureRow>> ListWeeklyClosuresAsync(Guid businessId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var business = await db.Businesses.AsNoTracking().FirstOrDefaultAsync(b => b.Id == businessId, ct);
        if (business is null) return [];

        var rules = await db.WeeklyClosures.AsNoTracking().Where(c => c.BusinessId == businessId)
            .Select(c => new
            {
                c.Id, c.StaffId, c.DayOfWeek, c.Reason, c.IsActive, c.StartTime, c.EndTime,
                StaffName = c.StaffId == null ? null : db.StaffMembers.Where(s => s.Id == c.StaffId).Select(s => s.Name).FirstOrDefault(),
            })
            .ToListAsync(ct);

        var now = time.GetUtcNow().UtcDateTime;
        var upcoming = await db.Bookings.AsNoTracking()
            .Where(b => b.BusinessId == businessId && b.StartsAtUtc > now
                && (b.Status == BookingStatus.Held || b.Status == BookingStatus.Pending || b.Status == BookingStatus.Confirmed))
            .Select(b => new { b.StaffId, b.StartsAtUtc, b.EndsAtUtc })
            .ToListAsync(ct);
        var local = upcoming.Select(b =>
        {
            var s = LocalTime.ToLocal(b.StartsAtUtc, business.TimeZone);
            var e = LocalTime.ToLocal(b.EndsAtUtc, business.TimeZone);
            return (b.StaffId, Day: (short)s.DayOfWeek, Start: TimeOnly.FromDateTime(s), End: e.Date == s.Date ? TimeOnly.FromDateTime(e) : TimeOnly.MaxValue);
        }).ToList();

        return rules
            .OrderBy(r => r.DayOfWeek is null ? -1 : (r.DayOfWeek.Value + 6) % 7).ThenBy(r => r.StartTime).ThenBy(r => r.StaffName ?? "")
            .Select(r => new WeeklyClosureRow(r.Id, r.StaffId, r.StaffName, r.DayOfWeek, r.Reason, r.IsActive,
                r.IsActive
                    ? local.Count(w => (r.DayOfWeek is null || w.Day == r.DayOfWeek) && (r.StaffId == null || w.StaffId == r.StaffId)
                        && (r.StartTime is null || (w.Start < r.EndTime && w.End > r.StartTime)))
                    : 0,
                r.StartTime, r.EndTime))
            .ToList();
    }

    /// <summary>Adds a closed day that repeats every week. Existing bookings are not cancelled; the outcome says how many fall on that day.</summary>
    public async Task<ServiceResult<SaveOutcome>> AddWeeklyClosureAsync(Guid businessId, WeeklyClosureInput input, CancellationToken ct = default)
    {
        var errors = new Dictionary<string, string>();
        var reason = string.IsNullOrWhiteSpace(input.Reason) ? null : input.Reason.Trim();
        if (reason is { Length: > 120 }) errors["reason"] = Text.ReasonTooLong;
        if (input.DayOfWeek is < 0 or > 6) errors["weekday"] = Text.ClosureWeekdayInvalid;
        if ((input.StartTime is null) != (input.EndTime is null)) errors["startTime"] = Text.ClosureTimesBoth;
        else if (input.StartTime is { } st && input.EndTime is { } et && et <= st) errors["endTime"] = Text.ClosureTimesOrder;
        if (errors.Count > 0) return ServiceResult<SaveOutcome>.Invalid(errors);

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        if (!await db.Businesses.AnyAsync(b => b.Id == businessId, ct)) return ServiceResult<SaveOutcome>.Fail(FailureKind.NotFound, Text.NotFound);
        if (input.StaffId is { } staffId && !await db.StaffMembers.AnyAsync(s => s.Id == staffId && s.BusinessId == businessId, ct))
            return ServiceResult<SaveOutcome>.Fail(FailureKind.NotFound, Text.NotFound);

        if (input.StartTime is { } ws && input.EndTime is { } we
            && !await TouchesOpeningHoursAsync(db, businessId, input.StaffId, input.DayOfWeek is { } d ? [(short)d] : null, ws, we, ct))
            return ServiceResult<SaveOutcome>.Invalid(new Dictionary<string, string> { ["startTime"] = Text.ClosureOutsideHours });

        var rule = new WeeklyClosure { BusinessId = businessId, StaffId = input.StaffId, DayOfWeek = (short?)input.DayOfWeek, Reason = reason, StartTime = input.StartTime, EndTime = input.EndTime };
        db.WeeklyClosures.Add(rule);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (ex.InnerException is Npgsql.PostgresException { SqlState: "23505" })
        {
            return ServiceResult<SaveOutcome>.Invalid(new Dictionary<string, string> { ["weekday"] = Text.ClosureWeeklyDuplicate });
        }

        var row = (await ListWeeklyClosuresAsync(businessId, ct)).First(r => r.Id == rule.Id);
        return ServiceResult<SaveOutcome>.Ok(new SaveOutcome(rule.Id, row.AffectedBookings));
    }

    /// <summary>Switches a weekly closure on or off without deleting it. Another business's id is "not found".</summary>
    public async Task<ServiceResult<bool>> SetWeeklyClosureActiveAsync(Guid businessId, Guid id, bool active, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var changed = await db.WeeklyClosures.Where(c => c.Id == id && c.BusinessId == businessId)
            .ExecuteUpdateAsync(s => s.SetProperty(c => c.IsActive, active), ct);
        return changed == 0 ? ServiceResult<bool>.Fail(FailureKind.NotFound, Text.NotFound) : ServiceResult<bool>.Ok(active);
    }

    public async Task<ServiceResult<bool>> DeleteWeeklyClosureAsync(Guid businessId, Guid id, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var removed = await db.WeeklyClosures.Where(c => c.Id == id && c.BusinessId == businessId).ExecuteDeleteAsync(ct);
        return removed == 0 ? ServiceResult<bool>.Fail(FailureKind.NotFound, Text.NotFound) : ServiceResult<bool>.Ok(true);
    }

    /// <summary>
    /// True when the time window overlaps the opening hours of the chosen person (or of anyone in the shop) on one of the
    /// given weekdays (any weekday when null). A closure outside the opening hours would close nothing, so it is refused.
    /// </summary>
    private static async Task<bool> TouchesOpeningHoursAsync(
        AppDbContext db, Guid businessId, Guid? staffId, short[]? days, TimeOnly start, TimeOnly end, CancellationToken ct)
    {
        var query = db.WorkingHours.AsNoTracking()
            .Where(h => db.StaffMembers.Any(s => s.Id == h.StaffId && s.BusinessId == businessId && (staffId == null || s.Id == staffId)));
        if (days is not null) query = query.Where(h => days.Contains(h.DayOfWeek));
        var windows = await query.Select(h => new { h.OpenTime, h.CloseTime }).ToListAsync(ct);

        static int Minutes(TimeOnly t) => t.Hour * 60 + t.Minute;
        return windows.Any(w =>
        {
            var open = Minutes(w.OpenTime);
            var close = Minutes(w.CloseTime);
            if (close <= open) close += 1440; // runs past midnight
            return Minutes(start) < close && Minutes(end) > open;
        });
    }

    private static Task<int> CountAffectedAsync(AppDbContext db, Guid businessId, Guid? staffId, DateTime start, DateTime end, CancellationToken ct) =>
        db.Bookings.CountAsync(b => b.BusinessId == businessId
            && (staffId == null || b.StaffId == staffId)
            && b.StartsAtUtc < end && b.EndsAtUtc > start
            && (b.Status == BookingStatus.Held || b.Status == BookingStatus.Pending || b.Status == BookingStatus.Confirmed), ct);
}
