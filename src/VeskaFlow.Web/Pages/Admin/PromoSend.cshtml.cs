using VeskaFlow.Web.Services;
using Microsoft.AspNetCore.Mvc;

namespace VeskaFlow.Web.Pages.Admin;

/// <summary>Send the running promo to customers who said yes to promo messages (BUSINESS_RULES 6.y21). The system sends nothing: staff send each message from their own phone.</summary>
[AdminAccess(AdminLevel.Manager)]
public class PromoSendModel(PromoMessageService messages, ShopSettingsService settings, IConfiguration config) : AdminPageModel
{
    [BindProperty(SupportsGet = true, Name = "q")] public string? Query { get; set; }

    public PromoSendView View { get; private set; } = null!;
    public string ShopName { get; private set; } = "";
    public string BookingUrl { get; private set; } = "";

    public async Task<IActionResult> OnGetAsync(CancellationToken ct)
    {
        var shop = await settings.GetSettingsAsync(BusinessId, ct);
        if (shop is null) return NotFound();
        ShopName = shop.Name;
        BookingUrl = QrCodes.BookingUrl(config["App:PublicBaseUrl"], Request.Scheme, Request.Host.Value, shop.Slug);
        View = await messages.GetViewAsync(BusinessId, shop.TimeZone, Query, ct);
        return Page();
    }

    /// <summary>The message for one customer: the first name, the promo and the booking link.</summary>
    public string MessageFor(PromoRecipient r)
    {
        var p = View.Promo!;
        var first = r.Name.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? r.Name;
        return Text.PromoMessage(first, ShopName, p.Headline, p.Details, p.DiscountPercent, p.EndsOn, BookingUrl);
    }

    public async Task<IActionResult> OnPostMarkAsync(Guid id, bool sent, string? q, CancellationToken ct)
    {
        var result = await messages.MarkSentAsync(BusinessId, id, sent, ct);
        if (result.Failure == FailureKind.NotFound) return NotFound();
        return RedirectToPage(new { q });
    }

    public async Task<IActionResult> OnPostResetAsync(CancellationToken ct)
    {
        await messages.ResetSentAsync(BusinessId, ct);
        Success = Text.PromoSendResetDone;
        return RedirectToPage();
    }
}
