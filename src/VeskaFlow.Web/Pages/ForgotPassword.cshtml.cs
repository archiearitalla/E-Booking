using VeskaFlow.Web.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace VeskaFlow.Web.Pages;

/// <summary>
/// Asks for the account's email and sends a one-hour reset link. The answer is the same for every address, so the page
/// cannot be used to find out who has an account.
/// </summary>
[EnableRateLimiting(RateLimitPolicies.Auth)]
public class ForgotPasswordModel(AccountControlService accounts, IConfiguration config, ILogger<ForgotPasswordModel> logger) : FormPageModel
{
    [BindProperty] public string? Email { get; set; }
    public bool Sent { get; private set; }
    public bool EmailConfigured => accounts.EmailConfigured;

    public void OnGet() { }

    public async Task<IActionResult> OnPostAsync(CancellationToken ct)
    {
        if (!EmailConfigured)
        {
            Errors = new Dictionary<string, string> { ["form"] = Text.ResetNoEmail };
            return Page();
        }
        var issued = await accounts.IssuePasswordResetAsync(Email, ct);
        if (issued is not null)
        {
            var baseUrl = config["App:PublicBaseUrl"] is { Length: > 0 } configured ? configured.Trim().TrimEnd('/') : $"{Request.Scheme}://{Request.Host}";
            if (!await accounts.SendPasswordResetAsync(issued, baseUrl, ct))
                logger.LogWarning("A password reset email was not accepted by the email service."); // no address in the log
        }
        Sent = true; // the same answer whether or not the address has an account
        return Page();
    }
}
