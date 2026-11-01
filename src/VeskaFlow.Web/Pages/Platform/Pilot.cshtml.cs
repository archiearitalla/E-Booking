using VeskaFlow.Web.Services;
using Microsoft.AspNetCore.Mvc;

namespace VeskaFlow.Web.Pages.Platform;

/// <summary>The platform owner's pilot checklist (before charging, setting up a pilot shop, what to ask, what to watch). Not found for anyone else.</summary>
public class PilotModel(AccountControlService accounts) : ChecklistPageModel(accounts)
{
    public async Task<IActionResult> OnGetAsync(CancellationToken ct)
    {
        await LoadAsync(ct);
        return Page();
    }

    /// <summary>Ticks or unticks one item and comes back to the same place on the page.</summary>
    public async Task<IActionResult> OnPostToggleAsync(string? id, bool done, CancellationToken ct)
    {
        if (!PilotChecklist.TickableItems.Any(i => i.Id == id) || !await Accounts.SetPilotCheckAsync(id, done, ct)) return NotFound();
        return Redirect("/Platform/Pilot#" + id);
    }
}
