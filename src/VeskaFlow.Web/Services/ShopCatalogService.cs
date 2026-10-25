using VeskaFlow.Core.Scheduling;
using VeskaFlow.Web.Data;
using Microsoft.EntityFrameworkCore;

namespace VeskaFlow.Web.Services;

public sealed record PublicService(Guid Id, string Name, int DurationMinutes, decimal Price, bool IsOther = false);

public sealed record PublicStaff(Guid Id, string Name, IReadOnlyList<Guid> ServiceIds);

/// <summary>What the public booking page may show. No phone numbers, no emails, no customer data.</summary>
public sealed record PublicShop(
    Guid Id, string Slug, string Name, string TimeZone, int HorizonDays,
    DateOnly Today, IReadOnlyList<PublicService> Services, IReadOnlyList<PublicStaff> Staff, string BrandColor = BrandColors.Default,
    bool AcceptingBookings = true, string Industry = Industries.Default)
{
    public PublicService? FindService(Guid? id) => Services.FirstOrDefault(s => s.Id == id);
    public PublicStaff? FindStaff(Guid? id) => Staff.FirstOrDefault(s => s.Id == id);
}

public sealed class ShopCatalogService(IDbContextFactory<AppDbContext> dbFactory, TimeProvider time)
{
    /// <summary>
    /// Days (from today, for <paramref name="days"/> days) on which the whole shop is closed, so the booking page does not
    /// offer them: a weekday without opening hours, a closure that repeats every week or every day for the whole day,
    /// or a one-off closure covering the whole day. Closures for one person or part of a day do not close the shop.
    /// </summary>
    public async Task<IReadOnlySet<DateOnly>> ClosedDatesAsync(PublicShop shop, int days, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var from = shop.Today;
        var to = from.AddDays(days);

        var openDays = (await db.BusinessHours.AsNoTracking().Where(h => h.BusinessId == shop.Id).Select(h => h.DayOfWeek).Distinct().ToListAsync(ct)).ToHashSet();
        if (openDays.Count == 0) // a shop that never set opening hours: open when any active staff member works that weekday
            openDays = (await db.WorkingHours.AsNoTracking()
                .Where(h => db.StaffMembers.Any(s => s.Id == h.StaffId && s.BusinessId == shop.Id && s.IsActive))
                .Select(h => h.DayOfWeek).Distinct().ToListAsync(ct)).ToHashSet();

        var weekly = await db.WeeklyClosures.AsNoTracking()
            .Where(c => c.BusinessId == shop.Id && c.IsActive && c.StaffId == null && c.StartTime == null)
            .Select(c => c.DayOfWeek).ToListAsync(ct);
        var everyDay = weekly.Any(d => d is null);

        var (rangeStart, _) = LocalTime.DayRangeUtc(from, shop.TimeZone);
        var (_, rangeEnd) = LocalTime.DayRangeUtc(to, shop.TimeZone);
        var blackouts = await db.Blackouts.AsNoTracking()
            .Where(b => b.BusinessId == shop.Id && b.StaffId == null && b.StartsAtUtc < rangeEnd && b.EndsAtUtc > rangeStart)
            .Select(b => new { b.StartsAtUtc, b.EndsAtUtc }).ToListAsync(ct);

        var closed = new HashSet<DateOnly>();
        for (var date = from; date <= to; date = date.AddDays(1))
        {
            var dow = (short)date.DayOfWeek;
            var (dayStart, dayEnd) = LocalTime.DayRangeUtc(date, shop.TimeZone);
            if (everyDay || !openDays.Contains(dow) || weekly.Contains(dow)
                || blackouts.Any(b => b.StartsAtUtc <= dayStart && b.EndsAtUtc >= dayEnd))
                closed.Add(date);
        }
        return closed;
    }

    /// <summary>The public view of a shop, or null for an unknown slug.</summary>
    public async Task<PublicShop?> GetBySlugAsync(string? slug, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(slug)) return null;

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var business = await db.Businesses.AsNoTracking().FirstOrDefaultAsync(b => b.Slug == slug.ToLowerInvariant(), ct);
        if (business is null) return null;

        var services = await db.Services.AsNoTracking()
            .Where(s => s.BusinessId == business.Id && s.IsActive)
            .OrderBy(s => s.SortOrder).ThenBy(s => s.Name)
            .Select(s => new PublicService(s.Id, s.Name, s.DurationMinutes, s.Price, s.IsOther))
            .ToListAsync(ct);

        var staffRows = await db.StaffMembers.AsNoTracking()
            .Where(s => s.BusinessId == business.Id && s.IsActive)
            .OrderBy(s => s.SortOrder).ThenBy(s => s.Name)
            .Select(s => new { s.Id, s.Name, ServiceIds = s.StaffServices.Select(x => x.ServiceId).ToList() })
            .ToListAsync(ct);

        var today = LocalTime.LocalDateOf(time.GetUtcNow().UtcDateTime, business.TimeZone);
        return new PublicShop(
            business.Id, business.Slug, business.Name, business.TimeZone, business.HorizonDays, today,
            services, staffRows.Select(s => new PublicStaff(s.Id, s.Name, s.ServiceIds)).ToList(), business.BrandColor,
            business.IsEnabled && (business.TrialEndsAtUtc is not { } ends
                || time.GetUtcNow().UtcDateTime <= DateTime.SpecifyKind(ends, DateTimeKind.Utc)),
            business.Industry);
    }
}
