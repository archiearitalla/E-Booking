using VeskaFlow.Web.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace VeskaFlow.Web.Pages.Admin;

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

        var ends = await settings.TrialEndsAtAsync(id, ct);
        HttpContext.Items[UserClaims.TrialEndsItemKey] = ends; // the header bell reads the database value

        // Someone whose trial is still running has nothing to see here.
        if (!settings.IsEnded(ends)) return Redirect("/Admin");
        return Page();
    }
}
