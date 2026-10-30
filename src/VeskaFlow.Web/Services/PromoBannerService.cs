using VeskaFlow.Core.Scheduling;
using VeskaFlow.Web.Data;
using Microsoft.EntityFrameworkCore;

namespace VeskaFlow.Web.Services;

public enum PromoState { None, Showing, Paused, Ended }

/// <summary>What the booking page needs to show the banner. <c>Version</c> changes whenever the banner does.</summary>
public sealed record PromoBannerView(string Caption, long Version);

/// <summary>What the owner's page needs: the state, the settings and "today" in the shop's time zone.</summary>
public sealed record PromoBannerAdmin(bool HasPicture, string Caption, bool IsActive, DateOnly? EndsOn, long Version, PromoState State, DateOnly Today);

public sealed record PromoImage(string ContentType, byte[] Data, DateTime UpdatedAtUtc)
{
    public long Version => UpdatedAtUtc.Ticks;
}

/// <summary>
/// A shop's promo banner (BUSINESS_RULES 6.y18): one picture on the public booking page while the owner has it switched on and
/// its last day has not passed. The picture's type comes from its own first bytes (PNG, JPEG, WebP; never SVG), exactly like the
/// logo. The public address serves the picture only while it is showing, so a paused or ended promo stops being reachable.
/// </summary>
public sealed class PromoBannerService(IDbContextFactory<AppDbContext> dbFactory, TimeProvider time)
{
    public const int MaxBytes = 512 * 1024;
    public const int MaxCaption = 100;
    /// <summary>How far ahead the last day may be set.</summary>
    public const int MaxDaysAhead = 365;

    private DateTime Now => time.GetUtcNow().UtcDateTime;

    private static PromoState StateOf(bool isActive, DateOnly? endsOn, DateOnly today) =>
        !isActive ? PromoState.Paused : endsOn is { } last && today > last ? PromoState.Ended : PromoState.Showing;

    /// <summary>The banner to show on the booking page of this shop right now, or null. Does not read the picture.</summary>
    public async Task<PromoBannerView?> GetForPageAsync(Guid businessId, string timeZone, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var row = await db.BusinessBanners.AsNoTracking().Where(b => b.BusinessId == businessId)
            .Select(b => new { b.Caption, b.IsActive, b.EndsOn, b.UpdatedAtUtc }).FirstOrDefaultAsync(ct);
        if (row is null) return null;
        var today = LocalTime.LocalDateOf(Now, timeZone);
        return StateOf(row.IsActive, row.EndsOn, today) == PromoState.Showing ? new PromoBannerView(row.Caption, row.UpdatedAtUtc.Ticks) : null;
    }

    /// <summary>The picture of the shop with this booking address, only while the banner is showing. Public.</summary>
    public async Task<PromoImage?> GetImageBySlugAsync(string? slug, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(slug) || slug.Length > 60) return null;
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var s = slug.Trim().ToLowerInvariant();
        var shop = await db.Businesses.AsNoTracking().Where(b => b.Slug == s).Select(b => new { b.Id, b.TimeZone }).FirstOrDefaultAsync(ct);
        if (shop is null) return null;
        var banner = await db.BusinessBanners.AsNoTracking().FirstOrDefaultAsync(b => b.BusinessId == shop.Id, ct);
        if (banner is null || StateOf(banner.IsActive, banner.EndsOn, LocalTime.LocalDateOf(Now, shop.TimeZone)) != PromoState.Showing) return null;
        return new PromoImage(banner.ContentType, banner.Data, banner.UpdatedAtUtc);
    }

    /// <summary>The owner's own picture for the preview on their page, whether or not it is showing. Scoped to the signed-in shop.</summary>
    public async Task<PromoImage?> GetOwnImageAsync(Guid businessId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.BusinessBanners.AsNoTracking().Where(b => b.BusinessId == businessId)
            .Select(b => new PromoImage(b.ContentType, b.Data, b.UpdatedAtUtc)).FirstOrDefaultAsync(ct);
    }

    /// <summary>The settings for the owner's page, or null for an unknown shop.</summary>
    public async Task<PromoBannerAdmin?> GetForAdminAsync(Guid businessId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var zone = await db.Businesses.AsNoTracking().Where(b => b.Id == businessId).Select(b => b.TimeZone).FirstOrDefaultAsync(ct);
        if (zone is null) return null;
        var today = LocalTime.LocalDateOf(Now, zone);
        var row = await db.BusinessBanners.AsNoTracking().Where(b => b.BusinessId == businessId)
            .Select(b => new { b.Caption, b.IsActive, b.EndsOn, b.UpdatedAtUtc }).FirstOrDefaultAsync(ct);
        return row is null
            ? new PromoBannerAdmin(false, "", true, null, 0, PromoState.None, today)
            : new PromoBannerAdmin(true, row.Caption, row.IsActive, row.EndsOn, row.UpdatedAtUtc.Ticks, StateOf(row.IsActive, row.EndsOn, today), today);
    }

    /// <summary>
    /// Saves the banner. <paramref name="picture"/> null keeps the picture already saved (so the owner can change the words or the
    /// last day without uploading again); a shop with none must give one. Errors are keyed "picture", "caption" and "endsOn".
    /// </summary>
    public async Task<ServiceResult<bool>> SaveAsync(Guid businessId, byte[]? picture, string? caption, bool isActive, DateOnly? endsOn, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var zone = await db.Businesses.AsNoTracking().Where(b => b.Id == businessId).Select(b => b.TimeZone).FirstOrDefaultAsync(ct);
        if (zone is null) return ServiceResult<bool>.Fail(FailureKind.NotFound, Text.NotFound);

        var existing = await db.BusinessBanners.FirstOrDefaultAsync(b => b.BusinessId == businessId, ct);
        var errors = new Dictionary<string, string>();

        var words = (caption ?? "").Trim();
        if (words.Length is < 1 or > MaxCaption) errors["caption"] = Text.PromoCaptionInvalid;

        string? type = null;
        if (picture is { Length: > 0 })
        {
            if (picture.Length > MaxBytes) errors["picture"] = Text.PromoTooBig;
            else if ((type = LogoService.Sniff(picture)) is null) errors["picture"] = Text.PromoWrongType;
        }
        else if (existing is null) errors["picture"] = Text.PromoMissingPicture;

        if (endsOn is { } last)
        {
            var today = LocalTime.LocalDateOf(Now, zone);
            if (last < today) errors["endsOn"] = Text.PromoEndsPast;
            else if (last > today.AddDays(MaxDaysAhead)) errors["endsOn"] = Text.PromoEndsFar;
        }
        if (errors.Count > 0) return ServiceResult<bool>.Invalid(errors);

        if (existing is null)
            db.BusinessBanners.Add(new BusinessBanner
            {
                BusinessId = businessId, ContentType = type!, Data = picture!, Caption = words, IsActive = isActive, EndsOn = endsOn, UpdatedAtUtc = Now,
            });
        else
        {
            if (type is not null) { existing.ContentType = type; existing.Data = picture!; }
            existing.Caption = words;
            existing.IsActive = isActive;
            existing.EndsOn = endsOn;
            existing.UpdatedAtUtc = Now;
        }
        await db.SaveChangesAsync(ct);
        return ServiceResult<bool>.Ok(true);
    }

    public async Task RemoveAsync(Guid businessId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        await db.BusinessBanners.Where(b => b.BusinessId == businessId).ExecuteDeleteAsync(ct);
    }
}
