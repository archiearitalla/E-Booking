using VeskaFlow.Web.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.RateLimiting;

namespace VeskaFlow.Web.Pages;

/// <summary>The page the link in the confirmation email opens. The token is single use and expires after 48 hours.</summary>
[EnableRateLimiting(RateLimitPolicies.Auth)]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public class ConfirmEmailModel(AccountControlService accounts) : PageModel
{
    public bool Confirmed { get; private set; }

    public async Task<IActionResult> OnGetAsync(string? token, CancellationToken ct)
    {
        Confirmed = (await accounts.ConfirmEmailAsync(token, ct)).IsSuccess;
        return Page();
    }
}
