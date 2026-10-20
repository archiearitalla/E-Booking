using BarberBook.Core.Scheduling;
using BarberBook.Web.Data;
using Microsoft.EntityFrameworkCore;

namespace BarberBook.Web.Services;

public sealed record PublicService(Guid Id, string Name, int DurationMinutes, decimal Price);

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
            .Select(s => new PublicService(s.Id, s.Name, s.DurationMinutes, s.Price))
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
            business.TrialEndsAtUtc is not { } ends
                || time.GetUtcNow().UtcDateTime <= DateTime.SpecifyKind(ends, DateTimeKind.Utc),
            business.Industry);
    }
}
