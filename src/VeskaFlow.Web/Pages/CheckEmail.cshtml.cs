using VeskaFlow.Web.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.RateLimiting;

namespace VeskaFlow.Web.Pages;

/// <summary>"Check your email": shown after sign-up, with a way to send the confirmation link again.</summary>
[EnableRateLimiting(RateLimitPolicies.Auth)]
public class CheckEmailModel(AccountControlService accounts, IConfiguration config) : PageModel
{
    /// <summary>The address the link was sent to (kept in TempData, never in the URL).</summary>
    public string? Email { get; private set; }

    public bool Resent { get; private set; }

    /// <summary>True when no email service is set up, so the shop owner must ask the administrator to confirm the account.</summary>
    public bool ManualOnly => !accounts.EmailConfigured;

    public IActionResult OnGet()
    {
        Email = TempData.Peek("ConfirmEmailAddress") as string;
        return Page();
    }

    /// <summary>
    /// Asked every few seconds by the page itself, so it can move on once the link is opened (in this tab or on another device).
    /// The address stays in TempData on the server: it is never put in a URL. Says only yes or no.
    /// </summary>
    public async Task<IActionResult> OnGetStatusAsync(CancellationToken ct)
    {
        var email = TempData.Peek("ConfirmEmailAddress") as string;
        TempData.Keep("ConfirmEmailAddress");
        return new JsonResult(new { confirmed = await accounts.IsEmailConfirmedAsync(email, ct) });
    }

    /// <summary>Sends the link again. The answer is the same whether or not the email belongs to an account.</summary>
    public async Task<IActionResult> OnPostResendAsync(string? email, CancellationToken ct)
    {
        Email = string.IsNullOrWhiteSpace(email) ? null : email.Trim();
        var issued = await accounts.IssueConfirmationAsync(Email, ct);
        if (issued is not null) await accounts.SendConfirmationAsync(issued, BaseUrl(), ct);
        TempData["ConfirmEmailAddress"] = Email;
        Resent = true;
        return Page();
    }

    private string BaseUrl() =>
        config["App:PublicBaseUrl"] is { Length: > 0 } configured ? configured.Trim().TrimEnd('/') : $"{Request.Scheme}://{Request.Host}";
}
