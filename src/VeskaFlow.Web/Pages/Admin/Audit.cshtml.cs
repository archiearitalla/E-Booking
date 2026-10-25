using VeskaFlow.Web.Services;
using Microsoft.AspNetCore.Mvc;

namespace VeskaFlow.Web.Pages.Admin;

public class AuditModel(AdminDayService days) : AdminPageModel
{
    public AuditView? View { get; private set; }

    public async Task<IActionResult> OnGetAsync(CancellationToken ct)
    {
        View = await days.GetAuditAsync(BusinessId, 100, ct);
        return View is null ? NotFound() : Page();
    }
}
