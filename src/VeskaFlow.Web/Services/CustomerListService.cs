using System.Globalization;
using System.Text;
using VeskaFlow.Core.Domain;
using VeskaFlow.Web.Data;
using Microsoft.EntityFrameworkCore;

namespace VeskaFlow.Web.Services;

public sealed record CustomerRow(
    Guid Id, string Name, string Phone, string? Email, int Visits, int NoShows, decimal Spent, DateTime? LastVisitUtc, DateTime CreatedAtUtc);

/// <summary>
/// The shop's customers with how often they came. Visits are completed bookings; spent is the sum of their prices.
/// Every query is scoped by the caller's business id.
/// </summary>
public sealed class CustomerListService(IDbContextFactory<AppDbContext> dbFactory)
{
    /// <summary>Customers matching <paramref name="search"/> (name, phone or email), most recent visit first.</summary>
    public async Task<IReadOnlyList<CustomerRow>> ListAsync(Guid businessId, string? search = null, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var query = db.Customers.AsNoTracking().Where(c => c.BusinessId == businessId);
        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = search.Trim().ToLowerInvariant();
            query = query.Where(c => c.Name.ToLower().Contains(term) || c.Phone.Contains(term) || (c.Email != null && c.Email.ToLower().Contains(term)));
        }

        var customers = await query.Select(c => new { c.Id, c.Name, c.Phone, c.Email, c.CreatedAtUtc }).ToListAsync(ct);
        var stats = (await db.Bookings.AsNoTracking()
            .Where(b => b.BusinessId == businessId && (b.Status == BookingStatus.Completed || b.Status == BookingStatus.NoShow))
            .GroupBy(b => b.CustomerId)
            .Select(g => new
            {
                CustomerId = g.Key,
                Visits = g.Count(b => b.Status == BookingStatus.Completed),
                NoShows = g.Count(b => b.Status == BookingStatus.NoShow),
                Spent = g.Where(b => b.Status == BookingStatus.Completed).Sum(b => (decimal?)(b.PaidAmount ?? b.PriceSnapshot)) ?? 0m,
                Last = g.Where(b => b.Status == BookingStatus.Completed).Max(b => (DateTime?)b.StartsAtUtc),
            }).ToListAsync(ct)).ToDictionary(s => s.CustomerId);

        return customers.Select(c =>
            {
                stats.TryGetValue(c.Id, out var s);
                return new CustomerRow(c.Id, c.Name, c.Phone, c.Email, s?.Visits ?? 0, s?.NoShows ?? 0, s?.Spent ?? 0m, s?.Last, c.CreatedAtUtc);
            })
            .OrderByDescending(r => r.LastVisitUtc ?? DateTime.MinValue).ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>The list as CSV text (Excel-safe: cells that look like formulas are prefixed with an apostrophe).</summary>
    public static string ToCsv(IEnumerable<CustomerRow> rows, string timeZone)
    {
        var sb = new StringBuilder("Name,Phone,Email,Completed visits,No-shows,Total spent,Last visit\r\n");
        foreach (var r in rows)
        {
            var phone = DisplayFormat.IsPlaceholderPhone(r.Phone) ? "" : r.Phone;
            sb.Append(Cell(r.Name)).Append(',').Append(Cell(phone)).Append(',').Append(Cell(r.Email ?? "")).Append(',')
              .Append(r.Visits).Append(',').Append(r.NoShows).Append(',').Append(r.Spent.ToString("0.00", CultureInfo.InvariantCulture)).Append(',')
              .Append(r.LastVisitUtc is { } last ? DisplayFormat.Date(last, timeZone) : "").Append("\r\n");
        }
        return sb.ToString();
    }

    private static string Cell(string value)
    {
        if (value.Length > 0 && "=+-@\t\r".Contains(value[0])) value = "'" + value;
        return value.Contains(',') || value.Contains('"') || value.Contains('\n') ? "\"" + value.Replace("\"", "\"\"") + "\"" : value;
    }
}
