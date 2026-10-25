using VeskaFlow.Web.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace VeskaFlow.Web.Pages;

[EnableRateLimiting(RateLimitPolicies.Auth)]
public class LoginModel(AuthService auth) : FormPageModel
{
    [BindProperty]
    public LoginInput Input { get; set; } = new();

    [BindProperty(SupportsGet = true)]
    public string? ReturnUrl { get; set; }

    /// <summary>The password was right but the email is not confirmed yet: offer to send the link again.</summary>
    public bool NeedsConfirmation { get; private set; }

    /// <summary>Set when a signed-in session was ended because the shop was switched off.</summary>
    [BindProperty(SupportsGet = true)]
    public bool Disabled { get; set; }

    /// <summary>Set after sign-up when the platform owner still has to switch the shop on.</summary>
    [BindProperty(SupportsGet = true)]
    public bool Pending { get; set; }

    public IActionResult OnGet() =>
        User.Identity?.IsAuthenticated == true ? Redirect("/Admin") : Page();

    public async Task<IActionResult> OnPostAsync(CancellationToken ct)
    {
        var result = await auth.SignInAsync(Input.Email, Input.Password, ct);
        if (!result.IsSuccess)
        {
            // One message for an unknown email and a wrong password. A correct password with a closed door says why.
            NeedsConfirmation = result.Message == Text.EmailNotConfirmed;
            Errors = new Dictionary<string, string>
            {
                ["form"] = result.Failure == FailureKind.PolicyViolation ? result.Message ?? Text.LoginFailed : Text.LoginFailed,
            };
            Input.Password = null;
            return Page();
        }

        await HttpContext.SignInAsync(
            CookieAuthenticationDefaults.AuthenticationScheme, AuthService.CreatePrincipal(result.Value!));

        // Only same-site return URLs are honoured (no open redirect).
        return Url.IsLocalUrl(ReturnUrl) ? LocalRedirect(ReturnUrl!) : Redirect("/Admin");
    }

    public class LoginInput
    {
        public string? Email { get; set; }
        public string? Password { get; set; }
    }
}
