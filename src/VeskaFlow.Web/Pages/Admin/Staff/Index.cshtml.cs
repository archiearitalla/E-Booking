using VeskaFlow.Web.Services;
using Microsoft.AspNetCore.Mvc;

namespace VeskaFlow.Web.Pages.Admin.Staff;

[AdminAccess(AdminLevel.Manager)]
public class IndexModel(CatalogAdminService catalog, ShopSettingsService settings) : AdminPageModel
{
    public IReadOnlyList<StaffRow> Rows { get; private set; } = [];
    public string Plan { get; private set; } = "FREE";
    public bool InTrial { get; private set; }

    public async Task<IActionResult> OnGetAsync(CancellationToken ct)
    {
        Rows = await catalog.ListStaffAsync(BusinessId, ct);
        var current = await settings.GetSettingsAsync(BusinessId, ct);
        Plan = current?.Plan ?? "FREE";
        InTrial = current?.TrialEndsAtUtc is not null;
        return Page();
    }
}
