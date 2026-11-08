using VeskaFlow.Web.Services;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace VeskaFlow.Web.Pages.Platform;

/// <summary>Base of the platform owner's tickable pages (the pilot checklist and the Meta App Review guide): not found for anyone else, and the ticks are read here.</summary>
public abstract class ChecklistPageModel(AccountControlService accounts) : PageModel
{
    protected AccountControlService Accounts => accounts;

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

    protected async Task LoadAsync(CancellationToken ct) => Done = await accounts.GetPilotChecksAsync(ct);
}

/// <summary>What the shared section view needs: the section and which of its items are ticked.</summary>
public sealed record ChecklistSectionView(PilotSection Section, IReadOnlyDictionary<string, DateTime> Done);
