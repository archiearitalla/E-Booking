using VeskaFlow.Web.Services;
using Microsoft.AspNetCore.Mvc;

namespace VeskaFlow.Web.Pages.Admin.Billing;

/// <summary>A confirmed payment, to print or keep. Only the shop's own owner, and only a confirmed payment, is "found".</summary>
[AdminAccess(AdminLevel.Owner)]
public class ReceiptModel(PaymentService payments, ShopSettingsService settings, IConfiguration config) : AdminPageModel
{
    protected override bool AllowWhenEnded => true;

    public PaymentRow? Payment { get; private set; }
    public string TimeZone { get; private set; } = "";
    public string Seller => config["Billing:SellerName"] is { Length: > 0 } s ? s : "VeskaFlow";

    public async Task<IActionResult> OnGetAsync(Guid id, CancellationToken ct)
    {
        var shop = await settings.GetSettingsAsync(BusinessId, ct);
        Payment = await payments.GetReceiptAsync(BusinessId, id, ct);
        if (shop is null || Payment is null) return NotFound();
        TimeZone = shop.TimeZone;
        return Page();
    }
}
