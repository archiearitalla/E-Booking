using System.Text;
using VeskaFlow.Web.Services;
using Microsoft.AspNetCore.Mvc;

namespace VeskaFlow.Web.Pages.Admin;

/// <summary>The shop's customers with visits and spend, searchable, and a CSV download. Staff only.</summary>
[AdminAccess(AdminLevel.Manager)]
public class CustomersModel(CustomerListService customers, ShopSettingsService settings) : AdminPageModel
{
    public IReadOnlyList<CustomerRow> Rows { get; private set; } = [];
    public string TimeZone { get; private set; } = "";
    [BindProperty(SupportsGet = true, Name = "q")] public string? Query { get; set; }

    public async Task<IActionResult> OnGetAsync(CancellationToken ct)
    {
        var shop = await settings.GetSettingsAsync(BusinessId, ct);
        if (shop is null) return NotFound();
        TimeZone = shop.TimeZone;
        Rows = await customers.ListAsync(BusinessId, Query, ct);
        return Page();
    }

    public async Task<IActionResult> OnGetExportAsync(CancellationToken ct)
    {
        var shop = await settings.GetSettingsAsync(BusinessId, ct);
        if (shop is null) return NotFound();
        var rows = await customers.ListAsync(BusinessId, Query, ct);
        var bytes = new UTF8Encoding(true).GetPreamble().Concat(Encoding.UTF8.GetBytes(CustomerListService.ToCsv(rows, shop.TimeZone))).ToArray();
        return File(bytes, "text/csv; charset=utf-8", "customers.csv");
    }
}
