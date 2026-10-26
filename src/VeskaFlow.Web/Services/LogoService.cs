using VeskaFlow.Web.Data;
using Microsoft.EntityFrameworkCore;

namespace VeskaFlow.Web.Services;

public sealed record LogoImage(string ContentType, byte[] Data, DateTime UpdatedAtUtc)
{
    /// <summary>Changes whenever the logo does; used as the cache validator and in the image address.</summary>
    public long Version => UpdatedAtUtc.Ticks;
}

/// <summary>
/// The shop's logo: one small PNG, JPEG or WebP per shop, kept in the database. The type is decided by the file's own first
/// bytes, never by the name or the type the browser claims (so a renamed page or script is refused). SVG is not accepted.
/// </summary>
public sealed class LogoService(IDbContextFactory<AppDbContext> dbFactory, TimeProvider time)
{
    public const int MaxBytes = 256 * 1024;

    /// <summary>The logo of the shop with this booking address, or null. Public: the booking page shows it.</summary>
    public async Task<LogoImage?> GetBySlugAsync(string? slug, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(slug) || slug.Length > 60) return null;
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var s = slug.Trim().ToLowerInvariant();
        return await db.BusinessLogos.AsNoTracking()
            .Where(l => db.Businesses.Any(b => b.Id == l.BusinessId && b.Slug == s))
            .Select(l => new LogoImage(l.ContentType, l.Data, l.UpdatedAtUtc)).FirstOrDefaultAsync(ct);
    }

    /// <summary>When the shop's logo last changed (ticks), or null when it has none. Cheap: does not read the image.</summary>
    public async Task<long?> VersionAsync(Guid businessId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var at = await db.BusinessLogos.AsNoTracking().Where(l => l.BusinessId == businessId).Select(l => (DateTime?)l.UpdatedAtUtc).FirstOrDefaultAsync(ct);
        return at?.Ticks;
    }

    public async Task<ServiceResult<bool>> SaveAsync(Guid businessId, byte[]? data, CancellationToken ct = default)
    {
        if (data is null || data.Length == 0)
            return ServiceResult<bool>.Invalid(new Dictionary<string, string> { ["logo"] = Text.LogoMissing });
        if (data.Length > MaxBytes)
            return ServiceResult<bool>.Invalid(new Dictionary<string, string> { ["logo"] = Text.LogoTooBig });
        var type = Sniff(data);
        if (type is null)
            return ServiceResult<bool>.Invalid(new Dictionary<string, string> { ["logo"] = Text.LogoWrongType });

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        if (!await db.Businesses.AnyAsync(b => b.Id == businessId, ct)) return ServiceResult<bool>.Fail(FailureKind.NotFound, Text.NotFound);
        var now = time.GetUtcNow().UtcDateTime;
        var existing = await db.BusinessLogos.FirstOrDefaultAsync(l => l.BusinessId == businessId, ct);
        if (existing is null)
            db.BusinessLogos.Add(new BusinessLogo { BusinessId = businessId, ContentType = type, Data = data, UpdatedAtUtc = now });
        else
        {
            existing.ContentType = type;
            existing.Data = data;
            existing.UpdatedAtUtc = now;
        }
        await db.SaveChangesAsync(ct);
        return ServiceResult<bool>.Ok(true);
    }

    public async Task RemoveAsync(Guid businessId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        await db.BusinessLogos.Where(l => l.BusinessId == businessId).ExecuteDeleteAsync(ct);
    }

    /// <summary>The image type from the file's first bytes, or null when it is not a PNG, JPEG or WebP picture.</summary>
    public static string? Sniff(byte[] d)
    {
        if (d.Length >= 8 && d[0] == 0x89 && d[1] == 0x50 && d[2] == 0x4E && d[3] == 0x47 && d[4] == 0x0D && d[5] == 0x0A && d[6] == 0x1A && d[7] == 0x0A)
            return "image/png";
        if (d.Length >= 3 && d[0] == 0xFF && d[1] == 0xD8 && d[2] == 0xFF) return "image/jpeg";
        if (d.Length >= 12 && d[0] == 'R' && d[1] == 'I' && d[2] == 'F' && d[3] == 'F' && d[8] == 'W' && d[9] == 'E' && d[10] == 'B' && d[11] == 'P')
            return "image/webp";
        return null;
    }
}
