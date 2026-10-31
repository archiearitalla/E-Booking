using System.Globalization;
using VeskaFlow.Core.Scheduling;
using VeskaFlow.Web.Services;
using Microsoft.AspNetCore.Mvc;

namespace VeskaFlow.Web.Pages.Admin;

/// <summary>"Record a sale" (BUSINESS_RULES 6.y20): a service and a payment entered by hand for the sales and customer records. Only when the shop switched it on.</summary>
[AdminAccess(AdminLevel.Manager)]
public class RecordSaleModel(ManualSaleService sales, CatalogAdminService catalog, ShopSettingsService settings, TimeProvider time) : AdminPageModel
{
    [BindProperty] public SaleForm Input { get; set; } = new();

    public IReadOnlyList<ServiceRow> Services { get; private set; } = [];
    public IReadOnlyList<StaffRow> Staff { get; private set; } = [];
    public string Today { get; private set; } = "";
    public string Earliest { get; private set; } = "";

    public async Task<IActionResult> OnGetAsync(CancellationToken ct)
    {
        if (!await LoadAsync(ct)) return NotFound();
        Input = new SaleForm { Date = Today, IdempotencyKey = Guid.NewGuid().ToString("N") };
        return Page();
    }

    public async Task<IActionResult> OnPostAsync(CancellationToken ct)
    {
        if (!await LoadAsync(ct)) return NotFound();
        if (!DateOnly.TryParseExact(Input.Date, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
        {
            Errors = new Dictionary<string, string> { ["date"] = Text.ManualSaleDateInvalid };
            return Page();
        }
        TimeOnly? at = TimeOnly.TryParseExact(Input.Time, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var t) ? t : null;
        var text = (Input.Amount ?? "").Replace(",", "").Replace("₱", "").Trim();
        decimal? amount = decimal.TryParse(text, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var parsed) ? parsed : null;

        var result = await sales.RecordAsync(BusinessId,
            new ManualSaleInput(Input.ServiceId, Input.Note, date, at, amount, Input.Name, Input.Phone, Input.StaffId, Input.IdempotencyKey ?? ""),
            StaffActor, ct, ScopeStaffId);
        if (result.IsSuccess)
        {
            Success = Text.ManualSaleSaved;
            return RedirectToPage();
        }
        if (result.Failure == FailureKind.NotFound) return NotFound();
        if (result.Failure == FailureKind.Validation) Errors = result.FieldErrors;
        else Failure = result.Message;
        return Page();
    }

    private async Task<bool> LoadAsync(CancellationToken ct)
    {
        var shop = await settings.GetSettingsAsync(BusinessId, ct);
        if (shop is null || !shop.AllowManualSales) return false; // switched off: the page does not exist
        Services = (await catalog.ListServicesAsync(BusinessId, ct)).Where(s => s.IsActive).ToList();
        Staff = (await catalog.ListStaffAsync(BusinessId, ct)).Where(s => s.IsActive).ToList();
        var today = LocalTime.LocalDateOf(time.GetUtcNow().UtcDateTime, shop.TimeZone);
        Today = DisplayFormat.DateParam(today);
        Earliest = DisplayFormat.DateParam(today.AddDays(-ManualSaleService.MaxDaysBack));
        return true;
    }

    public class SaleForm
    {
        public Guid ServiceId { get; set; }
        public string? Note { get; set; }
        public string? Date { get; set; }
        public string? Time { get; set; }
        public string? Amount { get; set; }
        public string? Name { get; set; }
        public string? Phone { get; set; }
        public Guid? StaffId { get; set; }
        public string? IdempotencyKey { get; set; }
    }
}
