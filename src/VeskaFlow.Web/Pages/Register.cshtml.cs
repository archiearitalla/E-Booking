using VeskaFlow.Web.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace VeskaFlow.Web.Pages;

[EnableRateLimiting(RateLimitPolicies.Auth)]
public class RegisterModel(OnboardingService onboarding, IConfiguration config) : FormPageModel
{
    /// <summary>Trial length in days for new sign-ups. 0 turns trials off (for example on a dedicated deployment).</summary>
    private int TrialDays => config.GetValue("Trial:Days", 0);

    [BindProperty]
    public RegisterInput Input { get; set; } = new();

    public IActionResult OnGet() =>
        User.Identity?.IsAuthenticated == true ? Redirect("/Admin") : Page();

    public async Task<IActionResult> OnPostAsync(CancellationToken ct)
    {
        var result = await onboarding.RegisterAsync(
            new RegisterRequest(Input.BusinessName, Input.Slug, Input.OwnerName, Input.Email, Input.Password,
                Industry: Input.Industry, TrialDays: TrialDays), ct);

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
        public string? Industry { get; set; } = Industries.Default;
    }
}
