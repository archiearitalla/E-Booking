using VeskaFlow.Core.Domain;
using VeskaFlow.Core.Scheduling;
using VeskaFlow.Web.Data;
using Microsoft.EntityFrameworkCore;

namespace VeskaFlow.Web.Services;

public sealed record ReportLine(string Label, int Completed, decimal Revenue);

public sealed record Report(
    DateOnly From, DateOnly To, int Total, int Completed, int NoShows, int Cancelled, int Upcoming, decimal Revenue,
    IReadOnlyList<ReportLine> ByService, IReadOnlyList<ReportLine> ByStaff, decimal Discounts = 0m);

/// <summary>
/// Simple numbers for a date range in the shop's time zone: bookings by outcome, revenue (completed bookings only: the payment total
/// staff entered, otherwise the price the customer was quoted after any promo), the discounts given by promos, and the same split by
/// service and by staff member. Scoped by the caller's business id.
/// </summary>
public sealed class ReportService(IDbContextFactory<AppDbContext> dbFactory, TimeProvider time)
{
    public enum Period { ThisMonth, LastMonth, Last30Days, ThisYear }

    public async Task<Report?> BuildAsync(Guid businessId, Period period, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var tz = await db.Businesses.AsNoTracking().Where(b => b.Id == businessId).Select(b => b.TimeZone).FirstOrDefaultAsync(ct);
        if (tz is null) return null;
        var (from, to) = Range(period, LocalTime.LocalDateOf(time.GetUtcNow().UtcDateTime, tz));
        return await BuildRangeAsync(businessId, from, to, ct);
    }

    /// <summary>Longest range a report may cover.</summary>
    public const int MaxDays = 366;

    /// <summary>The report for any dates (inclusive, shop time). Swapped dates are put in order and the range is capped at a year.</summary>
    public async Task<Report?> BuildRangeAsync(Guid businessId, DateOnly from, DateOnly to, CancellationToken ct = default)
    {
        if (to < from) (from, to) = (to, from);
        if (to.DayNumber - from.DayNumber >= MaxDays) from = to.AddDays(-(MaxDays - 1));
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var tz = await db.Businesses.AsNoTracking().Where(b => b.Id == businessId).Select(b => b.TimeZone).FirstOrDefaultAsync(ct);
        if (tz is null) return null;

        var startUtc = LocalTime.DayRangeUtc(from, tz).StartUtc;
        var endUtc = LocalTime.DayRangeUtc(to, tz).EndUtc;

        var rows = await db.Bookings.AsNoTracking()
            .Where(b => b.BusinessId == businessId && b.StartsAtUtc >= startUtc && b.StartsAtUtc < endUtc)
            .Select(b => new { b.Status, b.ServiceNameSnapshot, Staff = b.Staff.Name, Amount = b.PaidAmount ?? b.PriceSnapshot, b.PriceSnapshot, b.RegularPriceSnapshot })
            .ToListAsync(ct);

        var done = rows.Where(r => r.Status == BookingStatus.Completed).ToList();
        return new Report(from, to, rows.Count, done.Count,
            rows.Count(r => r.Status == BookingStatus.NoShow), rows.Count(r => r.Status == BookingStatus.Cancelled),
            rows.Count(r => r.Status == BookingStatus.Confirmed), done.Sum(r => r.Amount),
            done.GroupBy(r => r.ServiceNameSnapshot).Select(g => new ReportLine(g.Key, g.Count(), g.Sum(r => r.Amount)))
                .OrderByDescending(l => l.Revenue).ThenBy(l => l.Label).ToList(),
            done.GroupBy(r => r.Staff).Select(g => new ReportLine(g.Key, g.Count(), g.Sum(r => r.Amount)))
                .OrderByDescending(l => l.Revenue).ThenBy(l => l.Label).ToList(),
            done.Sum(r => r.RegularPriceSnapshot is { } regular ? regular - r.PriceSnapshot : 0m));
    }

    public static (DateOnly From, DateOnly To) Range(Period period, DateOnly today) => period switch
    {
        Period.LastMonth => (new DateOnly(today.Year, today.Month, 1).AddMonths(-1), new DateOnly(today.Year, today.Month, 1).AddDays(-1)),
        Period.Last30Days => (today.AddDays(-29), today),
        Period.ThisYear => (new DateOnly(today.Year, 1, 1), today),
        _ => (new DateOnly(today.Year, today.Month, 1), today),
    };
}
