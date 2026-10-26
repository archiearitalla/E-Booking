using VeskaFlow.Web.Services;
using Microsoft.AspNetCore.Mvc;

namespace VeskaFlow.Web.Pages.Admin.Services;

[AdminAccess(AdminLevel.Manager)]
public class IndexModel(CatalogAdminService catalog) : AdminPageModel
{
    public IReadOnlyList<ServiceRow> Rows { get; private set; } = [];

    public async Task<IActionResult> OnGetAsync(CancellationToken ct)
    {
        Rows = await catalog.ListServicesAsync(BusinessId, ct);
        return Page();
    }

    public async Task<IActionResult> OnPostSuggestedAsync(CancellationToken ct)
    {
        var added = await catalog.AddSuggestedServicesAsync(BusinessId, ct);
        Success = added == 0 ? Text.ServicesSuggestedNone : Text.ServicesSuggestedAdded(added);
        return Redirect("/Admin/Services");
    }
}
