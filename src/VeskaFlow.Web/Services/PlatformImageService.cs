using VeskaFlow.Web.Data;
using Microsoft.EntityFrameworkCore;

namespace VeskaFlow.Web.Services;

/// <summary>
/// Pictures the platform owner keeps for the whole system. Today there is one: the GCash QR code (<see cref="GcashQr"/>)
/// that clients scan to pay. Only PNG, JPEG or WebP up to 512 KB, recognised by the file's own first bytes.
/// </summary>
public sealed class PlatformImageService(IDbContextFactory<AppDbContext> dbFactory, TimeProvider time)
{
    public const string GcashQr = "gcash_qr";
    public const int MaxBytes = 512 * 1024;

    public async Task<LogoImage?> GetAsync(string key, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.PlatformImages.AsNoTracking().Where(i => i.Key == key)
            .Select(i => new LogoImage(i.ContentType, i.Data, i.UpdatedAtUtc)).FirstOrDefaultAsync(ct);
    }

    /// <summary>When the picture last changed (ticks), or null when there is none. Does not read the picture.</summary>
    public async Task<long?> VersionAsync(string key, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var at = await db.PlatformImages.AsNoTracking().Where(i => i.Key == key).Select(i => (DateTime?)i.UpdatedAtUtc).FirstOrDefaultAsync(ct);
        return at?.Ticks;
    }

    public async Task<ServiceResult<bool>> SaveAsync(string key, byte[]? data, CancellationToken ct = default)
    {
        if (key.Length is < 1 or > 60) return ServiceResult<bool>.Fail(FailureKind.NotFound, Text.NotFound);
        if (data is null || data.Length == 0)
            return ServiceResult<bool>.Invalid(new Dictionary<string, string> { ["image"] = Text.LogoMissing });
        if (data.Length > MaxBytes)
            return ServiceResult<bool>.Invalid(new Dictionary<string, string> { ["image"] = Text.PayQrTooBig });
        var type = LogoService.Sniff(data);
        if (type is null)
            return ServiceResult<bool>.Invalid(new Dictionary<string, string> { ["image"] = Text.LogoWrongType });

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var now = time.GetUtcNow().UtcDateTime;
        var row = await db.PlatformImages.FirstOrDefaultAsync(i => i.Key == key, ct);
        if (row is null) db.PlatformImages.Add(new PlatformImage { Key = key, ContentType = type, Data = data, UpdatedAtUtc = now });
        else
        {
            row.ContentType = type;
            row.Data = data;
            row.UpdatedAtUtc = now;
        }
        await db.SaveChangesAsync(ct);
        return ServiceResult<bool>.Ok(true);
    }

    public async Task RemoveAsync(string key, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        await db.PlatformImages.Where(i => i.Key == key).ExecuteDeleteAsync(ct);
    }
}
