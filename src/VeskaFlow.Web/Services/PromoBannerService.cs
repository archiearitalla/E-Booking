using VeskaFlow.Core.Pricing;
using VeskaFlow.Core.Scheduling;
using VeskaFlow.Web.Data;
using Microsoft.EntityFrameworkCore;

namespace VeskaFlow.Web.Services;

public enum PromoState { None, Showing, Paused, Ended }

/// <summary>What the booking page needs to show the banner. <c>Version</c> changes whenever the banner does.</summary>
/// <summary><c>Badge</c> is what the card shows: the owner's own, else "N% OFF" when the promo has a discount. <c>DiscountPercent</c> is the discount in force today (null when none).
public sealed record PromoBannerView(string Headline, string? Details, string? Badge, string Theme, bool HasPicture, DateOnly? EndsOn, long Version, int? DiscountPercent = null);

/// <summary>What the owner's page needs: the state, the settings and "today" in the shop's time zone.</summary>
public sealed record PromoBannerAdmin(
    bool Exists, bool HasPicture, string Caption, string? Details, string? Badge, string Theme, bool IsActive, DateOnly? EndsOn, long Version, PromoState State, DateOnly Today,
    int? DiscountPercent = null);

public sealed record PromoImage(string ContentType, byte[] Data, DateTime UpdatedAtUtc)
{
    public long Version => UpdatedAtUtc.Ticks;
}

/// <summary>
/// A shop's promo, made with the Promo Creator (BUSINESS_RULES 6.y18): a headline, an optional sub-line and badge, in the look of a chosen mode (Halloween, Christmas, ...) with an
/// optional picture, on the public booking page while the owner has it switched on and its last day has not passed. A picture's
/// type comes from its own first bytes (PNG, JPEG, WebP; never SVG), exactly like the logo. The public address serves the picture
/// only while the banner is showing, so a paused or ended promo stops being reachable.
/// </summary>
public sealed class PromoBannerService(IDbContextFactory<AppDbContext> dbFactory, TimeProvider time)
{
    public const int MaxBytes = 512 * 1024;
    public const int MaxCaption = 100;
    public const int MaxDetails = 140;
    public const int MaxBadge = 16;
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
            .Select(b => new { b.Caption, b.Details, b.Badge, b.Theme, HasPicture = b.Data != null, b.IsActive, b.EndsOn, b.DiscountPercent, b.UpdatedAtUtc }).FirstOrDefaultAsync(ct);
        if (row is null) return null;
        var today = LocalTime.LocalDateOf(Now, timeZone);
        if (StateOf(row.IsActive, row.EndsOn, today) != PromoState.Showing) return null;
        var percent = PromoPricing.ActivePercent(row.IsActive, row.EndsOn, row.DiscountPercent, today);
        return new PromoBannerView(row.Caption, row.Details, row.Badge ?? (percent is { } p ? Text.PromoAutoBadge(p) : null), PromoThemes.Normalize(row.Theme),
            row.HasPicture, row.EndsOn, row.UpdatedAtUtc.Ticks, percent);
    }

    /// <summary>The discount in force for this shop today, or null (no promo, switched off, ended, or a banner without a discount).</summary>
    public async Task<int?> GetActivePercentAsync(Guid businessId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var zone = await db.Businesses.AsNoTracking().Where(b => b.Id == businessId).Select(b => b.TimeZone).FirstOrDefaultAsync(ct);
        if (zone is null) return null;
        var row = await db.BusinessBanners.AsNoTracking().Where(b => b.BusinessId == businessId)
            .Select(b => new { b.IsActive, b.EndsOn, b.DiscountPercent }).FirstOrDefaultAsync(ct);
        return row is null ? null : PromoPricing.ActivePercent(row.IsActive, row.EndsOn, row.DiscountPercent, LocalTime.LocalDateOf(Now, zone));
    }

    /// <summary>The picture of the shop with this booking address, only while the banner is showing and has one. Public.</summary>
    public async Task<PromoImage?> GetImageBySlugAsync(string? slug, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(slug) || slug.Length > 60) return null;
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var s = slug.Trim().ToLowerInvariant();
        var shop = await db.Businesses.AsNoTracking().Where(b => b.Slug == s).Select(b => new { b.Id, b.TimeZone }).FirstOrDefaultAsync(ct);
        if (shop is null) return null;
        var banner = await db.BusinessBanners.AsNoTracking().FirstOrDefaultAsync(b => b.BusinessId == shop.Id, ct);
        if (banner?.Data is null || banner.ContentType is null
            || StateOf(banner.IsActive, banner.EndsOn, LocalTime.LocalDateOf(Now, shop.TimeZone)) != PromoState.Showing) return null;
        return new PromoImage(banner.ContentType, banner.Data, banner.UpdatedAtUtc);
    }

    /// <summary>The owner's own picture for the preview on their page, whether or not it is showing. Scoped to the signed-in shop.</summary>
    public async Task<PromoImage?> GetOwnImageAsync(Guid businessId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var banner = await db.BusinessBanners.AsNoTracking().FirstOrDefaultAsync(b => b.BusinessId == businessId, ct);
        return banner?.Data is null || banner.ContentType is null ? null : new PromoImage(banner.ContentType, banner.Data, banner.UpdatedAtUtc);
    }

    /// <summary>The settings for the owner's page, or null for an unknown shop.</summary>
    public async Task<PromoBannerAdmin?> GetForAdminAsync(Guid businessId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var zone = await db.Businesses.AsNoTracking().Where(b => b.Id == businessId).Select(b => b.TimeZone).FirstOrDefaultAsync(ct);
        if (zone is null) return null;
        var today = LocalTime.LocalDateOf(Now, zone);
        var row = await db.BusinessBanners.AsNoTracking().Where(b => b.BusinessId == businessId)
            .Select(b => new { b.Caption, b.Details, b.Badge, b.Theme, HasPicture = b.Data != null, b.IsActive, b.EndsOn, b.DiscountPercent, b.UpdatedAtUtc }).FirstOrDefaultAsync(ct);
        return row is null
            ? new PromoBannerAdmin(false, false, "", null, null, PromoThemes.Default, true, null, 0, PromoState.None, today)
            : new PromoBannerAdmin(true, row.HasPicture, row.Caption, row.Details, row.Badge, PromoThemes.Normalize(row.Theme), row.IsActive, row.EndsOn,
                row.UpdatedAtUtc.Ticks, StateOf(row.IsActive, row.EndsOn, today), today, row.DiscountPercent);
    }

    /// <summary>
    /// Saves the banner. A picture is optional: <paramref name="picture"/> null keeps the one already saved (so the words, the mode
    /// or the last day can change without uploading again) and <paramref name="removePicture"/> drops it and keeps the words.
    /// <paramref name="discountPercent"/> (1 to 90, optional) is taken off the services that are part of the promo, for bookings made while it runs.
    /// Errors are keyed "caption", "details", "badge", "theme", "picture", "endsOn" and "discount".
    /// </summary>
    public async Task<ServiceResult<bool>> SaveAsync(
        Guid businessId, byte[]? picture, bool removePicture, string? caption, string? details, string? badge, string? theme, bool isActive, DateOnly? endsOn,
        int? discountPercent = null, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var zone = await db.Businesses.AsNoTracking().Where(b => b.Id == businessId).Select(b => b.TimeZone).FirstOrDefaultAsync(ct);
        if (zone is null) return ServiceResult<bool>.Fail(FailureKind.NotFound, Text.NotFound);

        var existing = await db.BusinessBanners.FirstOrDefaultAsync(b => b.BusinessId == businessId, ct);
        var errors = new Dictionary<string, string>();

        var words = (caption ?? "").Trim();
        if (words.Length is < 1 or > MaxCaption) errors["caption"] = Text.PromoCaptionInvalid;

        var more = string.IsNullOrWhiteSpace(details) ? null : details.Trim();
        if (more is { Length: > MaxDetails }) errors["details"] = Text.PromoDetailsInvalid;
        var label = string.IsNullOrWhiteSpace(badge) ? null : badge.Trim();
        if (label is { Length: > MaxBadge }) errors["badge"] = Text.PromoBadgeInvalid;

        if (discountPercent is { } d && !PromoPricing.IsValidPercent(d)) errors["discount"] = Text.PromoDiscountInvalid;

        var chosen = string.IsNullOrWhiteSpace(theme) ? PromoThemes.Default : theme.Trim();
        if (!PromoThemes.IsValid(chosen)) errors["theme"] = Text.PromoThemeInvalid;

        string? type = null;
        if (picture is { Length: > 0 })
        {
            if (picture.Length > MaxBytes) errors["picture"] = Text.PromoTooBig;
            else if ((type = LogoService.Sniff(picture)) is null) errors["picture"] = Text.PromoWrongType;
        }

        if (endsOn is { } last)
        {
            var today = LocalTime.LocalDateOf(Now, zone);
            if (last < today) errors["endsOn"] = Text.PromoEndsPast;
            else if (last > today.AddDays(MaxDaysAhead)) errors["endsOn"] = Text.PromoEndsFar;
        }
        if (errors.Count > 0) return ServiceResult<bool>.Invalid(errors);

        if (existing is null)
        {
            existing = new BusinessBanner { BusinessId = businessId };
            db.BusinessBanners.Add(existing);
        }
        if (type is not null) { existing.ContentType = type; existing.Data = picture; }
        else if (removePicture) { existing.ContentType = null; existing.Data = null; }
        existing.Caption = words;
        existing.Details = more;
        existing.Badge = label;
        existing.DiscountPercent = discountPercent;
        existing.Theme = chosen;
        existing.IsActive = isActive;
        existing.EndsOn = endsOn;
        existing.UpdatedAtUtc = Now;
        await db.SaveChangesAsync(ct);
        return ServiceResult<bool>.Ok(true);
    }

    public async Task RemoveAsync(Guid businessId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        await db.BusinessBanners.Where(b => b.BusinessId == businessId).ExecuteDeleteAsync(ct);
    }
}
