using BarberBook.Web.Services;
using Microsoft.AspNetCore.Mvc;

namespace BarberBook.Web.Pages.Admin.Services;

public class IndexModel(CatalogAdminService catalog) : AdminPageModel
{
    public IReadOnlyList<ServiceRow> Rows { get; private set; } = [];

    public async Task<IActionResult> OnGetAsync(CancellationToken ct)
    {
        Rows = await catalog.ListServicesAsync(BusinessId, ct);
        return Page();
    }
}
