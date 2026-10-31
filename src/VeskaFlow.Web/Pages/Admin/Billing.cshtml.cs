using VeskaFlow.Web.Services;
using Microsoft.AspNetCore.Mvc;

namespace VeskaFlow.Web.Pages.Admin;

/// <summary>The owner's plan and billing (BUSINESS_RULES 6.y22): the price list, how to pay, "I paid", and the history of payments. Still open when access has ended, so the owner can pay.</summary>
[AdminAccess(AdminLevel.Owner)]
public class BillingModel(ShopSettingsService settings, PaymentService payments, PricingInfo pricing, BillingInfo billing, PlatformImageService images) : AdminPageModel
{
    protected override bool AllowWhenEnded => true;

    [BindProperty] public PayForm Input { get; set; } = new();

    public PricingInfo Pricing => pricing;
    public BillingInfo Billing => billing;
    public IReadOnlyList<PaymentRow> History { get; private set; } = [];
    public long? PayQrVersion { get; private set; }
    public string TimeZone { get; private set; } = "";
    public DateTime? AccessEndsUtc { get; private set; }
    public string Plan { get; private set; } = "FREE";
    public DateTime Now { get; } = DateTime.UtcNow;

    public async Task<IActionResult> OnGetAsync(CancellationToken ct)
    {
        if (!await LoadAsync(ct)) return NotFound();
        Input = new PayForm { Option = pricing.Options.FirstOrDefault()?.Key };
        return Page();
    }

    public async Task<IActionResult> OnPostAsync(CancellationToken ct)
    {
        if (!await LoadAsync(ct)) return NotFound();
        var result = await payments.SubmitAsync(BusinessId, Input.Option, Input.Reference, Input.Note, StaffActor.Label, ct);
        if (result.IsSuccess)
        {
            Success = Text.BillingSubmitted;
            return RedirectToPage();
        }
        if (result.Failure == FailureKind.NotFound) return NotFound();
        Errors = result.FieldErrors;
        return Page();
    }

    private async Task<bool> LoadAsync(CancellationToken ct)
    {
        var shop = await settings.GetSettingsAsync(BusinessId, ct);
        if (shop is null) return false;
        TimeZone = shop.TimeZone;
        Plan = shop.Plan;
        AccessEndsUtc = shop.TrialEndsAtUtc is { } e ? DateTime.SpecifyKind(e, DateTimeKind.Utc) : null;
        History = await payments.ListForShopAsync(BusinessId, ct);
        PayQrVersion = await images.VersionAsync(PlatformImageService.GcashQr, ct);
        return true;
    }

    /// <summary>One line about where the shop stands: trial, paid, ended or no end date.</summary>
    public string StatusLine()
    {
        if (AccessEndsUtc is not { } end) return Text.BillingNoEnd;
        var days = ShopExpiry.DaysLeft(end, Now);
        var date = DisplayFormat.Date(end, TimeZone);
        if (end <= Now) return Text.BillingEnded(date);
        return Plan == "PRO" ? Text.BillingProUntil(date, days) : Text.BillingTrialUntil(date, days);
    }

    public bool Ended => AccessEndsUtc is { } e && e <= Now;

    public class PayForm
    {
        public string? Option { get; set; }
        public string? Reference { get; set; }
        public string? Note { get; set; }
    }
}
