using VeskaFlow.Web.Services;
using Microsoft.AspNetCore.Mvc;

namespace VeskaFlow.Web.Pages.Platform;

/// <summary>The platform owner's walkthroughs: scripts for showing VeskaFlow to a client and setting up a shop with them. Not found for anyone else.</summary>
public class WalkthroughsModel(AccountControlService accounts) : ChecklistPageModel(accounts)
{
    public IActionResult OnGet() => Page();
}
