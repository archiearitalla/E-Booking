using VeskaFlow.Web.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace VeskaFlow.Web.Pages.Platform;

/// <summary>The platform owner's pilot checklist (before charging, setting up a pilot shop, what to ask, what to watch). Not found for anyone else.</summary>
public class PilotModel(AccountControlService accounts) : PageModel
{
    public IReadOnlyDictionary<string, DateTime> Done { get; private set; } = new Dictionary<string, DateTime>();

    public override async Task OnPageHandlerExecutionAsync(PageHandlerExecutingContext context, PageHandlerExecutionDelegate next)
    {
        if (!await accounts.IsPlatformAdminAsync(User.UserId(), context.HttpContext.RequestAborted))
        {
            context.Result = NotFound();
            return;
        }
        await next();
    }

    public async Task<IActionResult> OnGetAsync(CancellationToken ct)
    {
        Done = await accounts.GetPilotChecksAsync(ct);
        return Page();
    }

    /// <summary>Ticks or unticks one item and comes back to the same place on the page.</summary>
    public async Task<IActionResult> OnPostToggleAsync(string? id, bool done, CancellationToken ct)
    {
        if (!await accounts.SetPilotCheckAsync(id, done, ct)) return NotFound();
        return Redirect("/Platform/Pilot#" + id);
    }

    public (int Done, int Total) Progress(PilotSection section) =>
        (section.Items.Count(i => Done.ContainsKey(i.Id)), section.Items.Count);
}
