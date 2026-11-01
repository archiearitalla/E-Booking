using VeskaFlow.Web.Services;
using Microsoft.AspNetCore.Mvc;

namespace VeskaFlow.Web.Pages.Platform;

/// <summary>The platform owner's guide to Meta App Review, on its own page so it does not mix with the pilot checklist. Not found for anyone else.</summary>
public class MetaReviewModel(AccountControlService accounts) : ChecklistPageModel(accounts)
{
    public async Task<IActionResult> OnGetAsync(CancellationToken ct)
    {
        await LoadAsync(ct);
        return Page();
    }

    public async Task<IActionResult> OnPostToggleAsync(string? id, bool done, CancellationToken ct)
    {
        if (!MetaReviewGuide.TickableItems.Any(i => i.Id == id) || !await Accounts.SetPilotCheckAsync(id, done, ct)) return NotFound();
        return Redirect("/Platform/MetaReview#" + id);
    }
}
