using BarberBook.Web.Services;
using Microsoft.AspNetCore.Mvc;

namespace BarberBook.Web.Pages.Admin.Staff;

public class IndexModel(CatalogAdminService catalog, ShopSettingsService settings) : AdminPageModel
{
    public IReadOnlyList<StaffRow> Rows { get; private set; } = [];
    public string Plan { get; private set; } = "FREE";

    public async Task<IActionResult> OnGetAsync(CancellationToken ct)
    {
        Rows = await catalog.ListStaffAsync(BusinessId, ct);
        Plan = (await settings.GetSettingsAsync(BusinessId, ct))?.Plan ?? "FREE";
        return Page();
    }
}
