using VeskaFlow.Core.Domain;
using VeskaFlow.Core.Scheduling;
using VeskaFlow.Web.Data;
using Microsoft.EntityFrameworkCore;

namespace VeskaFlow.Web.Services;

public sealed record ServiceRow(Guid Id, string Name, int DurationMinutes, int BufferMinutes, decimal Price, bool IsActive, int SortOrder);

public sealed record ServiceInput(Guid? Id, string? Name, int DurationMinutes, int BufferMinutes, decimal Price, bool IsActive, int SortOrder);

public sealed record StaffRow(Guid Id, string Name, bool IsActive, int SortOrder, IReadOnlyList<string> ServiceNames);

public sealed record StaffInput(Guid? Id, string? Name, bool IsActive, int SortOrder, IReadOnlyList<Guid> ServiceIds);

public sealed record HourWindow(short DayOfWeek, TimeOnly Open, TimeOnly Close);

public sealed record StaffEditView(
    Guid Id, string Name, bool IsActive, int SortOrder, string TimeZone, string Plan,
    IReadOnlyList<ServiceRow> AllServices, IReadOnlySet<Guid> ServiceIds, IReadOnlyList<HourWindow> Hours);

/// <summary>A save result that also says how many upcoming bookings the change leaves affected (never cancelled).</summary>
public sealed record SaveOutcome(Guid Id, int AffectedBookings);

/// <summary>
/// Services, staff, staff-to-service links and weekly hours. Every method takes the business id from the signed-in
/// user and filters by it; an id belonging to another business is "not found" and changes nothing.
/// </summary>
public sealed class CatalogAdminService(IDbContextFactory<AppDbContext> dbFactory, TimeProvider time)
{
    // ------------------------------------------------------------------ services

    public async Task<IReadOnlyList<ServiceRow>> ListServicesAsync(Guid businessId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await ServiceRows(db, businessId, ct);
    }

    public async Task<ServiceRow?> GetServiceAsync(Guid businessId, Guid id, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.Services.AsNoTracking().Where(s => s.BusinessId == businessId && s.Id == id)
            .Select(s => new ServiceRow(s.Id, s.Name, s.DurationMinutes, s.BufferMinutes, s.Price, s.IsActive, s.SortOrder))
            .FirstOrDefaultAsync(ct);
    }

    public async Task<ServiceResult<Guid>> SaveServiceAsync(Guid businessId, ServiceInput input, CancellationToken ct = default)
    {
        var errors = new Dictionary<string, string>();
        var name = (input.Name ?? "").Trim();
        if (name.Length is < 1 or > 80) errors["name"] = Text.ServiceNameInvalid;
        if (input.DurationMinutes is < 5 or > 600) errors["durationMinutes"] = Text.DurationInvalid;
        if (input.BufferMinutes is < 0 or > 240) errors["bufferMinutes"] = Text.BufferInvalid;
        if (input.Price < 0 || input.Price > 1_000_000m) errors["price"] = Text.PriceInvalid;
        if (input.SortOrder is < 0 or > 1000) errors["sortOrder"] = Text.SortOrderInvalid;
        if (errors.Count > 0) return ServiceResult<Guid>.Invalid(errors);

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        Service service;
        if (input.Id is null)
        {
            service = new Service { BusinessId = businessId };
            db.Services.Add(service);
        }
        else
        {
            var found = await db.Services.FirstOrDefaultAsync(s => s.Id == input.Id && s.BusinessId == businessId, ct);
            if (found is null) return ServiceResult<Guid>.Fail(FailureKind.NotFound, Text.NotFound);
            service = found;
        }

        service.Name = name;
        service.DurationMinutes = input.DurationMinutes;
        service.BufferMinutes = input.BufferMinutes;
        service.Price = Math.Round(input.Price, 2);
        service.IsActive = input.IsActive;
        service.SortOrder = input.SortOrder;
        await db.SaveChangesAsync(ct);
        return ServiceResult<Guid>.Ok(service.Id);
    }

    /// <summary>
    /// Adds the suggested services for the shop's industry that it does not have yet (matched by name, ignoring case).
    /// New ones are linked to every current staff member so they can be booked at once. Returns how many were added.
    /// </summary>
    public async Task<int> AddSuggestedServicesAsync(Guid businessId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var industry = await db.Businesses.AsNoTracking().Where(b => b.Id == businessId).Select(b => b.Industry).FirstOrDefaultAsync(ct);
        if (industry is null) return 0;

        var have = (await db.Services.AsNoTracking().Where(s => s.BusinessId == businessId).Select(s => s.Name).ToListAsync(ct))
            .Select(n => n.Trim().ToLowerInvariant()).ToHashSet();
        var nextSort = (await db.Services.Where(s => s.BusinessId == businessId).MaxAsync(s => (int?)s.SortOrder, ct) ?? -1) + 1;
        var staffIds = await db.StaffMembers.Where(s => s.BusinessId == businessId).Select(s => s.Id).ToListAsync(ct);

        var added = 0;
        foreach (var suggestion in Industries.Suggested(industry))
        {
            if (!have.Add(suggestion.Name.ToLowerInvariant())) continue;
            var service = new Service
            {
                BusinessId = businessId, Name = suggestion.Name, DurationMinutes = suggestion.DurationMinutes,
                Price = 0, SortOrder = Math.Min(nextSort++, 1000),
            };
            db.Services.Add(service);
            foreach (var staffId in staffIds)
                db.StaffServices.Add(new StaffService { StaffId = staffId, ServiceId = service.Id, BusinessId = businessId });
            added++;
        }
        if (added > 0) await db.SaveChangesAsync(ct);
        return added;
    }

    private static Task<List<ServiceRow>> ServiceRows(AppDbContext db, Guid businessId, CancellationToken ct) =>
        db.Services.AsNoTracking().Where(s => s.BusinessId == businessId)
            .OrderBy(s => s.SortOrder).ThenBy(s => s.Name)
            .Select(s => new ServiceRow(s.Id, s.Name, s.DurationMinutes, s.BufferMinutes, s.Price, s.IsActive, s.SortOrder))
            .ToListAsync(ct);

    // ------------------------------------------------------------------ staff

    public async Task<IReadOnlyList<StaffRow>> ListStaffAsync(Guid businessId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var rows = await db.StaffMembers.AsNoTracking().Where(s => s.BusinessId == businessId)
            .OrderBy(s => s.SortOrder).ThenBy(s => s.Name)
            .Select(s => new
            {
                s.Id, s.Name, s.IsActive, s.SortOrder,
                Services = db.StaffServices.Where(x => x.StaffId == s.Id && x.BusinessId == businessId)
                    .Join(db.Services, x => x.ServiceId, v => v.Id, (x, v) => v.Name).OrderBy(n => n).ToList(),
            })
            .ToListAsync(ct);
        return rows.Select(r => new StaffRow(r.Id, r.Name, r.IsActive, r.SortOrder, r.Services)).ToList();
    }

    public async Task<StaffEditView?> GetStaffAsync(Guid businessId, Guid staffId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var staff = await db.StaffMembers.AsNoTracking().FirstOrDefaultAsync(s => s.Id == staffId && s.BusinessId == businessId, ct);
        if (staff is null) return null;

        var business = await db.Businesses.AsNoTracking().FirstAsync(b => b.Id == businessId, ct);
        var links = await db.StaffServices.AsNoTracking().Where(x => x.StaffId == staffId && x.BusinessId == businessId)
            .Select(x => x.ServiceId).ToListAsync(ct);
        var hours = await db.WorkingHours.AsNoTracking().Where(h => h.StaffId == staffId)
            .OrderBy(h => h.DayOfWeek).ThenBy(h => h.OpenTime)
            .Select(h => new HourWindow(h.DayOfWeek, h.OpenTime, h.CloseTime)).ToListAsync(ct);

        return new StaffEditView(staff.Id, staff.Name, staff.IsActive, staff.SortOrder, business.TimeZone, business.Plan,
            await ServiceRows(db, businessId, ct), links.ToHashSet(), hours);
    }

    /// <summary>Creates or updates a staff member and replaces their service links. Enforces the plan's staff limit.</summary>
    public async Task<ServiceResult<SaveOutcome>> SaveStaffAsync(Guid businessId, StaffInput input, CancellationToken ct = default)
    {
        var errors = new Dictionary<string, string>();
        var name = (input.Name ?? "").Trim();
        if (name.Length is < 1 or > 80) errors["name"] = Text.StaffNameInvalid;
        if (input.SortOrder is < 0 or > 1000) errors["sortOrder"] = Text.SortOrderInvalid;
        if (errors.Count > 0) return ServiceResult<SaveOutcome>.Invalid(errors);

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        // Serialize staff changes per business so two simultaneous adds cannot both slip under the plan limit.
        await db.Database.ExecuteSqlAsync($"SELECT pg_advisory_xact_lock(hashtextextended({"staff:" + businessId}, 0))", ct);

        var biz = await db.Businesses.Where(b => b.Id == businessId).Select(b => new { b.Plan, b.TrialEndsAtUtc }).FirstOrDefaultAsync(ct);
        var plan = biz?.Plan;
        var inTrial = biz?.TrialEndsAtUtc is not null;
        if (plan is null) return ServiceResult<SaveOutcome>.Fail(FailureKind.NotFound, Text.NotFound);

        var isNew = input.Id is null;
        Staff staff;
        if (isNew)
        {
            staff = new Staff { BusinessId = businessId };
            db.StaffMembers.Add(staff);
        }
        else
        {
            var found = await db.StaffMembers.Include(s => s.StaffServices)
                .FirstOrDefaultAsync(s => s.Id == input.Id && s.BusinessId == businessId, ct);
            if (found is null) return ServiceResult<SaveOutcome>.Fail(FailureKind.NotFound, Text.NotFound);
            staff = found;
        }

        var wasActive = !isNew && staff.IsActive;
        if (input.IsActive && !wasActive && PlanLimits.MaxStaff(plan!, inTrial) is { } limit)
        {
            var activeOthers = await db.StaffMembers.CountAsync(s => s.BusinessId == businessId && s.IsActive && s.Id != staff.Id, ct);
            if (activeOthers >= limit)
                return ServiceResult<SaveOutcome>.Invalid(new Dictionary<string, string> { ["isActive"] = string.Format(Text.StaffLimit, limit) });
        }

        // Every service id must belong to this business; one foreign id rejects the whole request.
        var wanted = input.ServiceIds.Distinct().ToList();
        var valid = await db.Services.CountAsync(s => s.BusinessId == businessId && wanted.Contains(s.Id), ct);
        if (valid != wanted.Count) return ServiceResult<SaveOutcome>.Fail(FailureKind.NotFound, Text.NotFound);

        staff.Name = name;
        staff.IsActive = input.IsActive;
        staff.SortOrder = input.SortOrder;

        if (isNew)
        {
            // New staff start with the shop's opening hours from Settings (Monday to Saturday 9 to 6 when none are set).
            var shopHours = await db.BusinessHours.AsNoTracking().Where(h => h.BusinessId == businessId).ToListAsync(ct);
            if (shopHours.Count > 0)
                foreach (var h in shopHours)
                    staff.WorkingHours.Add(new WorkingHour { DayOfWeek = h.DayOfWeek, OpenTime = h.OpenTime, CloseTime = h.CloseTime });
            else
                for (short day = 1; day <= 6; day++)
                    staff.WorkingHours.Add(new WorkingHour { DayOfWeek = day, OpenTime = new TimeOnly(9, 0), CloseTime = new TimeOnly(18, 0) });
        }
        foreach (var link in staff.StaffServices.Where(l => !wanted.Contains(l.ServiceId)).ToList())
            staff.StaffServices.Remove(link);
        foreach (var serviceId in wanted.Where(id => staff.StaffServices.All(l => l.ServiceId != id)))
            staff.StaffServices.Add(new StaffService { ServiceId = serviceId, BusinessId = businessId });

        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);

        // Deactivating never cancels bookings; say how many are still on the books for this person.
        var affected = wasActive && !staff.IsActive ? await CountUpcomingAsync(businessId, staff.Id, ct) : 0;
        return ServiceResult<SaveOutcome>.Ok(new SaveOutcome(staff.Id, affected));
    }

    // ------------------------------------------------------------------ weekly hours

    /// <summary>Replaces a staff member's weekly windows. Existing bookings are never cancelled; the result counts those left outside the new hours.</summary>
    public async Task<ServiceResult<SaveOutcome>> SaveHoursAsync(
        Guid businessId, Guid staffId, IReadOnlyList<HourWindow> windows, CancellationToken ct = default)
    {
        var errors = HoursErrors(windows);
        if (errors.Count > 0) return ServiceResult<SaveOutcome>.Invalid(errors);

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var staff = await db.StaffMembers.AsNoTracking().FirstOrDefaultAsync(s => s.Id == staffId && s.BusinessId == businessId, ct);
        if (staff is null) return ServiceResult<SaveOutcome>.Fail(FailureKind.NotFound, Text.NotFound);

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await db.WorkingHours.Where(h => h.StaffId == staffId).ExecuteDeleteAsync(ct);
        db.WorkingHours.AddRange(windows.Select(w => new WorkingHour { StaffId = staffId, DayOfWeek = w.DayOfWeek, OpenTime = w.Open, CloseTime = w.Close }));
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);

        return ServiceResult<SaveOutcome>.Ok(new SaveOutcome(staffId, await CountOutsideHoursAsync(businessId, staffId, windows, ct)));
    }

    private static Dictionary<string, string> HoursErrors(IReadOnlyList<HourWindow> windows)
    {
        var errors = new Dictionary<string, string>();
        foreach (var w in windows)
        {
            if (w.DayOfWeek is < 0 or > 6 || w.Open == w.Close) { errors["hours"] = Text.HoursInvalid; break; }
        }
        if (errors.Count == 0 && HasOverlap(windows)) errors["hours"] = Text.HoursOverlap;
        return errors;
    }

    /// <summary>
    /// The shop's opening hours from Settings. A shop that never set them shows its first staff member's hours,
    /// which is what its other staff started with.
    /// </summary>
    public async Task<IReadOnlyList<HourWindow>> GetShopHoursAsync(Guid businessId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var rows = await db.BusinessHours.AsNoTracking().Where(h => h.BusinessId == businessId)
            .OrderBy(h => h.DayOfWeek).ThenBy(h => h.OpenTime)
            .Select(h => new HourWindow(h.DayOfWeek, h.OpenTime, h.CloseTime)).ToListAsync(ct);
        if (rows.Count > 0) return rows;

        var first = await db.StaffMembers.AsNoTracking().Where(s => s.BusinessId == businessId)
            .OrderBy(s => s.SortOrder).ThenBy(s => s.Name).Select(s => (Guid?)s.Id).FirstOrDefaultAsync(ct);
        if (first is null) return [];
        return await db.WorkingHours.AsNoTracking().Where(h => h.StaffId == first)
            .OrderBy(h => h.DayOfWeek).ThenBy(h => h.OpenTime)
            .Select(h => new HourWindow(h.DayOfWeek, h.OpenTime, h.CloseTime)).ToListAsync(ct);
    }

    /// <summary>
    /// Saves the shop's opening hours and copies them to every staff member (replacing their weekly hours).
    /// Individual hours can still be changed afterwards on the Staff page. Bookings are never cancelled; the result
    /// counts upcoming bookings left outside the new hours.
    /// </summary>
    public async Task<ServiceResult<SaveOutcome>> SaveShopHoursAsync(Guid businessId, IReadOnlyList<HourWindow> windows, CancellationToken ct = default)
    {
        var errors = HoursErrors(windows);
        if (errors.Count > 0) return ServiceResult<SaveOutcome>.Invalid(errors);

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        if (!await db.Businesses.AnyAsync(b => b.Id == businessId, ct)) return ServiceResult<SaveOutcome>.Fail(FailureKind.NotFound, Text.NotFound);
        var staffIds = await db.StaffMembers.Where(s => s.BusinessId == businessId).Select(s => s.Id).ToListAsync(ct);

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await db.BusinessHours.Where(h => h.BusinessId == businessId).ExecuteDeleteAsync(ct);
        db.BusinessHours.AddRange(windows.Select(w => new BusinessHour { BusinessId = businessId, DayOfWeek = w.DayOfWeek, OpenTime = w.Open, CloseTime = w.Close }));
        await db.WorkingHours.Where(h => staffIds.Contains(h.StaffId)).ExecuteDeleteAsync(ct);
        foreach (var staffId in staffIds)
            db.WorkingHours.AddRange(windows.Select(w => new WorkingHour { StaffId = staffId, DayOfWeek = w.DayOfWeek, OpenTime = w.Open, CloseTime = w.Close }));
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);

        var affected = 0;
        foreach (var staffId in staffIds) affected += await CountOutsideHoursAsync(businessId, staffId, windows, ct);
        return ServiceResult<SaveOutcome>.Ok(new SaveOutcome(businessId, affected));
    }

    /// <summary>Windows on one day may not overlap. A window that closes at or before it opens runs past midnight.</summary>
    private static bool HasOverlap(IReadOnlyList<HourWindow> windows)
    {
        foreach (var day in windows.GroupBy(w => w.DayOfWeek))
        {
            var spans = day.Select(w =>
            {
                var open = w.Open.Hour * 60 + w.Open.Minute;
                var close = w.Close.Hour * 60 + w.Close.Minute;
                return (Open: open, Close: close <= open ? close + 1440 : close);
            }).OrderBy(s => s.Open).ToList();
            for (var i = 1; i < spans.Count; i++)
                if (spans[i].Open < spans[i - 1].Close) return true;
        }
        return false;
    }

    private async Task<int> CountUpcomingAsync(Guid businessId, Guid staffId, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var now = time.GetUtcNow().UtcDateTime;
        return await db.Bookings.CountAsync(b => b.BusinessId == businessId && b.StaffId == staffId && b.StartsAtUtc >= now
            && (b.Status == BookingStatus.Held || b.Status == BookingStatus.Pending || b.Status == BookingStatus.Confirmed), ct);
    }

    private async Task<int> CountOutsideHoursAsync(Guid businessId, Guid staffId, IReadOnlyList<HourWindow> windows, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var tzId = await db.Businesses.Where(b => b.Id == businessId).Select(b => b.TimeZone).FirstAsync(ct);
        var tz = LocalTime.Zone(tzId);
        var now = time.GetUtcNow().UtcDateTime;
        var upcoming = await db.Bookings.AsNoTracking()
            .Where(b => b.BusinessId == businessId && b.StaffId == staffId && b.StartsAtUtc >= now
                        && (b.Status == BookingStatus.Held || b.Status == BookingStatus.Pending || b.Status == BookingStatus.Confirmed))
            .Select(b => new { b.StartsAtUtc, b.EndsAtUtc })
            .ToListAsync(ct);

        var outside = 0;
        foreach (var booking in upcoming)
        {
            var startDate = LocalTime.LocalDateOf(booking.StartsAtUtc, tzId);
            var fits = new[] { startDate.AddDays(-1), startDate }.Any(date =>
                windows.Where(w => w.DayOfWeek == (short)date.DayOfWeek).Any(w =>
                {
                    var open = LocalTime.ToUtc(date.ToDateTime(w.Open), tz);
                    var closeDate = w.Close > w.Open ? date : date.AddDays(1);
                    var close = LocalTime.ToUtc(closeDate.ToDateTime(w.Close), tz);
                    return booking.StartsAtUtc >= open && booking.EndsAtUtc <= close;
                }));
            if (!fits) outside++;
        }
        return outside;
    }
}
