using BarberBook.Web.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace BarberBook.Web.Pages.Admin;

/// <summary>
/// Where a signed-in owner lands once the trial has ended. Deliberately not an AdminPageModel (that base class
/// redirects here), and it uses the plain layout so none of the locked back-office navigation appears.
/// </summary>
public class TrialEndedModel(ShopSettingsService settings, IConfiguration config) : PageModel
{
    public string ContactUrl => config["Trial:ContactUrl"] is { Length: > 0 } url ? url : "";

    public async Task<IActionResult> OnGetAsync(CancellationToken ct)
    {
        if (User.BusinessId() is not { } id) return Forbid();

        // Someone whose trial is still running has nothing to see here.
        if (!await settings.TrialEndedAsync(id, ct)) return Redirect("/Admin");
        return Page();
    }
}
