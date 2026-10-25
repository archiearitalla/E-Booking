using VeskaFlow.Web.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace VeskaFlow.Web.Pages;

[EnableRateLimiting(RateLimitPolicies.Auth)]
public class RegisterModel(OnboardingService onboarding, AccountControlService accounts, IConfiguration config) : FormPageModel
{
    /// <summary>Trial length in days for new sign-ups. 0 turns trials off (for example on a dedicated deployment).</summary>
    private int TrialDays => config.GetValue("Trial:Days", 0);

    /// <summary>New owners must open the link in an email before they can sign in. On by default.</summary>
    private bool RequireEmailConfirmation => config.GetValue("Signup:RequireEmailConfirmation", true);

    /// <summary>When on, a new shop stays switched off until the platform owner enables it.</summary>
    private bool RequireApproval => config.GetValue("Signup:RequireApproval", false);

    [BindProperty]
    public RegisterInput Input { get; set; } = new();

    public IActionResult OnGet() =>
        User.Identity?.IsAuthenticated == true ? Redirect("/Admin") : Page();

    public async Task<IActionResult> OnPostAsync(CancellationToken ct)
    {
        var result = await onboarding.RegisterAsync(
            new RegisterRequest(Input.BusinessName, Input.Slug, Input.OwnerName, Input.Email, Input.Password,
                Industry: Input.Industry, TrialDays: TrialDays,
                RequireEmailConfirmation: RequireEmailConfirmation, RequireApproval: RequireApproval), ct);

        if (!result.IsSuccess)
        {
            Errors = result.FieldErrors;
            Input.Password = null; // never echo a password back
            return Page();
        }

        // The owner must prove the email address first: send the link and stop here (no sign-in yet).
        if (result.Value!.ConfirmationToken is { } token)
        {
            var baseUrl = config["App:PublicBaseUrl"] is { Length: > 0 } configured ? configured.Trim().TrimEnd('/') : $"{Request.Scheme}://{Request.Host}";
            await accounts.SendConfirmationAsync(new IssuedConfirmation(token, result.Value.Email, result.Value.DisplayName), baseUrl, ct);
            TempData["ConfirmEmailAddress"] = result.Value.Email;
            return Redirect("/CheckEmail");
        }

        // Waiting for the platform owner to switch the shop on: nothing to sign in to yet.
        if (!result.Value.Enabled) return Redirect("/Login?pending=true");

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
