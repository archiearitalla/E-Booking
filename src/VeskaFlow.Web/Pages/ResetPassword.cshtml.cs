using VeskaFlow.Web.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace VeskaFlow.Web.Pages;

/// <summary>The page the link in the reset email opens. The link is single use and works for one hour.</summary>
[EnableRateLimiting(RateLimitPolicies.Auth)]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public class ResetPasswordModel(AccountControlService accounts) : FormPageModel
{
    [BindProperty(SupportsGet = true)] public string? Token { get; set; }
    [BindProperty] public string? Password { get; set; }
    [BindProperty] public string? Confirm { get; set; }
    public bool LinkValid { get; private set; }

    public async Task OnGetAsync(CancellationToken ct) => LinkValid = await accounts.IsResetTokenValidAsync(Token, ct);

    public async Task<IActionResult> OnPostAsync(CancellationToken ct)
    {
        if (Password != Confirm)
        {
            LinkValid = await accounts.IsResetTokenValidAsync(Token, ct);
            Errors = new Dictionary<string, string> { ["confirm"] = Text.ResetMismatch };
            return Page();
        }
        var result = await accounts.ResetPasswordAsync(Token, Password, ct);
        if (result.IsSuccess) return Redirect("/Login?reset=true");
        LinkValid = result.Failure != FailureKind.NotFound;
        Errors = result.Failure == FailureKind.Validation ? result.FieldErrors : new Dictionary<string, string>();
        return Page();
    }
}
