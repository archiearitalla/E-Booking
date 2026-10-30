using VeskaFlow.Web.Services;
using Microsoft.AspNetCore.Mvc;

namespace VeskaFlow.Web.Pages.Admin;

/// <summary>The owner's promo banner for the public booking page (BUSINESS_RULES 6.y18). Owner only; the shop is the signed-in one.</summary>
[AdminAccess(AdminLevel.Owner)]
[RequestSizeLimit(1_048_576)] // a banner is at most 512 KB; nothing bigger is read
public class PromoModel(PromoBannerService promo) : AdminPageModel
{
    public sealed class PromoInput
    {
        public string? Caption { get; set; }
        public DateOnly? EndsOn { get; set; }
        /// <summary>A browser sends nothing for an unticked box, so "not sent" must mean off: the default is false, and the form is filled from the saved banner.</summary>
        public bool Active { get; set; }
    }

    [BindProperty] public PromoInput Input { get; set; } = new();

    public PromoBannerAdmin Banner { get; private set; } = new(false, "", true, null, 0, PromoState.None, default);

    public async Task<IActionResult> OnGetAsync(CancellationToken ct)
    {
        if (!await LoadAsync(ct)) return NotFound();
        Input = new PromoInput { Caption = Banner.Caption, EndsOn = Banner.EndsOn, Active = Banner.IsActive };
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

        var result = await promo.SaveAsync(BusinessId, bytes, Input.Caption, Input.Active, Input.EndsOn, ct);
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
        return true;
    }
}
