using VeskaFlow.Web.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace VeskaFlow.Web.Pages.Platform;

/// <summary>
/// The platform owner's list of shops: switch a shop on or off, confirm an owner's email by hand, or send a new confirmation
/// link. Only a signed-in user whose email is listed in <c>Platform:AdminEmails</c> sees it; everyone else gets "not found".
/// </summary>
public class IndexModel(AccountControlService accounts, IConfiguration config) : PageModel
{
    public IReadOnlyList<ShopRow> Shops { get; private set; } = [];
    public bool EmailConfigured => accounts.EmailConfigured;

    [TempData] public string? Success { get; set; }
    [TempData] public string? Failure { get; set; }

    /// <summary>A confirmation link to copy, shown once when it could not be emailed.</summary>
    [TempData] public string? LinkToCopy { get; set; }

    public override async Task OnPageHandlerExecutionAsync(PageHandlerExecutingContext context, PageHandlerExecutionDelegate next)
    {
        if (!await accounts.IsPlatformAdminAsync(User.UserId(), context.HttpContext.RequestAborted))
        {
            context.Result = NotFound(); // the page does not exist for anyone else
            return;
        }
        await next();
    }

    public async Task<IActionResult> OnGetAsync(CancellationToken ct)
    {
        Shops = await accounts.ListShopsAsync(ct);
        return Page();
    }

    public async Task<IActionResult> OnPostEnableAsync(Guid id, bool on, CancellationToken ct)
    {
        // Switching off your own shop would lock the platform owner out.
        if (!on && id == User.BusinessId())
        {
            Failure = Text.PlatformCannotDisableOwn;
            return RedirectToPage();
        }
        var result = await accounts.SetEnabledAsync(id, on, ct);
        if (!result.IsSuccess) return NotFound();
        Success = on ? Text.PlatformEnabled : Text.PlatformDisabled;
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostConfirmNowAsync(Guid id, CancellationToken ct)
    {
        var result = await accounts.ConfirmManuallyAsync(id, ct);
        if (result.IsSuccess) Success = Text.PlatformConfirmed;
        else Failure = Text.PlatformAlreadyConfirmed;
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostSendLinkAsync(Guid id, CancellationToken ct)
    {
        var issued = await accounts.IssueForShopAsync(id, ct);
        if (issued is null)
        {
            Failure = Text.PlatformAlreadyConfirmed;
            return RedirectToPage();
        }

        var baseUrl = config["App:PublicBaseUrl"] is { Length: > 0 } configured ? configured.Trim().TrimEnd('/') : $"{Request.Scheme}://{Request.Host}";
        if (await accounts.SendConfirmationAsync(issued, baseUrl, ct))
            Success = string.Format(Text.PlatformLinkEmailed, issued.Email);
        else
        {
            // No email service (or it refused): hand the link to the platform owner to send by other means.
            LinkToCopy = AccountControlService.ConfirmUrl(baseUrl, issued.Token);
            Failure = Text.PlatformLinkNotEmailed;
        }
        return RedirectToPage();
    }
}
