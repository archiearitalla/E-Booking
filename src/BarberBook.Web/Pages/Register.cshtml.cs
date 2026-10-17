using BarberBook.Web.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace BarberBook.Web.Pages;

[EnableRateLimiting(RateLimitPolicies.Auth)]
public class RegisterModel(OnboardingService onboarding) : FormPageModel
{
    [BindProperty]
    public RegisterInput Input { get; set; } = new();

    public IActionResult OnGet() =>
        User.Identity?.IsAuthenticated == true ? Redirect("/Admin") : Page();

    public async Task<IActionResult> OnPostAsync(CancellationToken ct)
    {
        var result = await onboarding.RegisterAsync(
            new RegisterRequest(Input.BusinessName, Input.Slug, Input.OwnerName, Input.Email, Input.Password), ct);

        if (!result.IsSuccess)
        {
            Errors = result.FieldErrors;
            Input.Password = null; // never echo a password back
            return Page();
        }

        await HttpContext.SignInAsync(
            CookieAuthenticationDefaults.AuthenticationScheme, AuthService.CreatePrincipal(result.Value!));
        return Redirect("/Admin");
    }

    public class RegisterInput
    {
        public string? BusinessName { get; set; }
        public string? Slug { get; set; }
        public string? OwnerName { get; set; }
        public string? Email { get; set; }
        public string? Password { get; set; }
    }
}
