using VeskaFlow.Web.Services;
using Microsoft.AspNetCore.Mvc;

namespace VeskaFlow.Web.Pages.Admin;

/// <summary>The owner's promo banner for the public booking page (BUSINESS_RULES 6.y18). Owner only; the shop is the signed-in one.</summary>
[AdminAccess(AdminLevel.Owner)]
[RequestSizeLimit(1_048_576)] // a banner is at most 512 KB; nothing bigger is read
public class PromoModel(PromoBannerService promo, CatalogAdminService catalog) : AdminPageModel
{
    public sealed class PromoInput
    {
        public string? Caption { get; set; }
        public string? Details { get; set; }
        public string? Badge { get; set; }
        /// <summary>The discount in percent (1 to 90), or empty for a banner only.</summary>
        public int? Discount { get; set; }
        public string Theme { get; set; } = PromoThemes.Default;
        /// <summary>Drop the saved picture and keep the words. A browser sends nothing for an unticked box, so not sent means keep.</summary>
        public bool RemovePicture { get; set; }
        public DateOnly? EndsOn { get; set; }
        /// <summary>A browser sends nothing for an unticked box, so "not sent" must mean off: the default is false, and the form is filled from the saved banner.</summary>
        public bool Active { get; set; }
    }

    [BindProperty] public PromoInput Input { get; set; } = new();

    /// <summary>One service with its regular price and whether it is part of the promo, for the price list on the page.</summary>
    public sealed record PriceRow(string Name, decimal Regular, bool Eligible);

    public IReadOnlyList<PriceRow> Prices { get; private set; } = [];

    public PromoBannerAdmin Banner { get; private set; } = new(false, false, "", null, null, PromoThemes.Default, true, null, 0, PromoState.None, default);

    public async Task<IActionResult> OnGetAsync(CancellationToken ct)
    {
        if (!await LoadAsync(ct)) return NotFound();
        Input = new PromoInput { Caption = Banner.Caption, Details = Banner.Details, Badge = Banner.Badge, Discount = Banner.DiscountPercent, Theme = Banner.Theme, EndsOn = Banner.EndsOn, Active = Banner.IsActive };
        return Page();
    }

    /// <summary>The owner's own picture for the preview (also when the banner is paused or ended).</summary>
    public async Task<IActionResult> OnGetImageAsync(CancellationToken ct)
    {
        var image = await promo.GetOwnImageAsync(BusinessId, ct);
        if (image is null) return NotFound();
        Response.Headers.CacheControl = "private, no-store";
        return File(image.Data, image.ContentType);
    }

    public async Task<IActionResult> OnPostAsync(IFormFile? picture, CancellationToken ct)
    {
        byte[]? bytes = null;
        if (picture is { Length: > PromoBannerService.MaxBytes })
        {
            Failure = Text.PromoTooBig;
            return RedirectToPage();
        }
        if (picture is { Length: > 0 })
        {
            await using var stream = picture.OpenReadStream();
            using var buffer = new MemoryStream();
            await stream.CopyToAsync(buffer, ct);
            bytes = buffer.ToArray();
        }

        // Text that is not a whole number cannot be bound, which would quietly mean "no discount"; say so instead.
        if (ModelState.TryGetValue("Input.Discount", out var typed) && typed.Errors.Count > 0)
        {
            await LoadAsync(ct);
            Input.Discount = null;
            Failure = Text.PromoDiscountInvalid;
            return Page();
        }

        var result = await promo.SaveAsync(BusinessId, bytes, Input.RemovePicture, Input.Caption, Input.Details, Input.Badge, Input.Theme, Input.Active, Input.EndsOn, Input.Discount, ct);
        if (result.Failure == FailureKind.NotFound) return NotFound();
        if (result.IsSuccess)
        {
            Success = Text.PromoSaved;
            return RedirectToPage();
        }
        await LoadAsync(ct);
        Failure = result.FieldErrors.Values.FirstOrDefault() ?? result.Message; // keep what was typed
        return Page();
    }

    public async Task<IActionResult> OnPostRemoveAsync(CancellationToken ct)
    {
        await promo.RemoveAsync(BusinessId, ct);
        Success = Text.PromoRemoved;
        return RedirectToPage();
    }

    private async Task<bool> LoadAsync(CancellationToken ct)
    {
        var banner = await promo.GetForAdminAsync(BusinessId, ct);
        if (banner is null) return false;
        Banner = banner;
        Prices = (await catalog.ListServicesAsync(BusinessId, ct)).Where(s => s.IsActive && !s.IsOther && s.Price > 0)
            .Select(s => new PriceRow(s.Name, s.Price, s.PromoEligible)).ToList();
        return true;
    }
}
