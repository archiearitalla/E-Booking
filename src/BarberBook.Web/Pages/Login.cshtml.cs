using BarberBook.Web.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace BarberBook.Web.Pages;

[EnableRateLimiting(RateLimitPolicies.Auth)]
public class LoginModel(AuthService auth) : FormPageModel
{
    [BindProperty]
    public LoginInput Input { get; set; } = new();

    [BindProperty(SupportsGet = true)]
    public string? ReturnUrl { get; set; }

    public IActionResult OnGet() =>
        User.Identity?.IsAuthenticated == true ? Redirect("/Admin") : Page();

    public async Task<IActionResult> OnPostAsync(CancellationToken ct)
    {
        var result = await auth.SignInAsync(Input.Email, Input.Password, ct);
        if (!result.IsSuccess)
        {
            // One message for an unknown email and a wrong password.
            Errors = new Dictionary<string, string> { ["form"] = Text.LoginFailed };
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
