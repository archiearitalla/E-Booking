using VeskaFlow.Web.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace VeskaFlow.Web.Pages.Platform;

/// <summary>
/// The platform owner's list of shops: switch a shop on or off, confirm an owner's email by hand, or send a new confirmation
/// link. Only a signed-in user whose email is listed in <c>Platform:AdminEmails</c> sees it; everyone else gets "not found".
/// </summary>
public class IndexModel(AccountControlService accounts, IConfiguration config, ILogger<IndexModel> logger, EmailQuota quota) : PageModel
{
    /// <summary>The shop the platform owner is signed in to: it can be switched on or off but never deleted from here.</summary>
    public Guid? OwnBusinessId => User.BusinessId();

    public IReadOnlyList<ShopRow> Shops { get; private set; } = [];
    public bool EmailConfigured => accounts.EmailConfigured;

    /// <summary>Where the nightly backups are kept (the GitHub Actions page of the repository).</summary>
    public string BackupUrl => config["Platform:BackupUrl"] is { Length: > 0 } u ? u : "https://github.com/archiearitalla/E-Booking/actions/workflows/backup.yml";

    public int EmailsToday { get; private set; }
    public int EmailLimit => quota.DailyLimit;
    public DateTime? BackupCopySavedAtUtc { get; private set; }
    public int? BackupDaysSince { get; private set; }
    public bool BackupOverdue => BackupDaysSince is null || BackupDaysSince >= AccountControlService.BackupWarnDays;

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
        EmailsToday = await quota.TodayTotalAsync(ct);
        BackupCopySavedAtUtc = await accounts.GetBackupCopySavedAsync(ct);
        BackupDaysSince = AccountControlService.DaysSince(BackupCopySavedAtUtc, DateTime.UtcNow);
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

    /// <summary>The platform owner says they saved a copy of the backup on their own drive.</summary>
    public async Task<IActionResult> OnPostBackupSavedAsync(CancellationToken ct)
    {
        await accounts.MarkBackupCopySavedAsync(ct);
        Success = Text.PlatformBackupMarked;
        return RedirectToPage();
    }

    /// <summary>Adds days to a shop's paid more-emails add-on after the client paid for it.</summary>
    public async Task<IActionResult> OnPostEmailAddonAsync(Guid id, int days, CancellationToken ct)
    {
        var result = await accounts.ExtendEmailAddonAsync(id, days, ct);
        if (result.Failure == FailureKind.NotFound) return NotFound();
        if (result.IsSuccess) Success = Text.PlatformEmailAddonExtended(result.Value.ToString("MMM d, yyyy", System.Globalization.CultureInfo.InvariantCulture));
        else Failure = result.Message;
        return RedirectToPage();
    }

    /// <summary>Adds days to a shop's access end date (after the client paid, or as a favour).</summary>
    public async Task<IActionResult> OnPostExtendAsync(Guid id, int days, CancellationToken ct)
    {
        var result = await accounts.ExtendAccessAsync(id, days, ct);
        if (result.Failure == FailureKind.NotFound) return NotFound();
        if (result.IsSuccess) Success = Text.PlatformExtended(result.Value.ToString("MMM d, yyyy", System.Globalization.CultureInfo.InvariantCulture));
        else Failure = result.Message;
        return RedirectToPage();
    }

    /// <summary>Deletes a shop and all its data for good. Needs the booking address typed as confirmation.</summary>
    public async Task<IActionResult> OnPostDeleteAsync(Guid id, string? confirm, CancellationToken ct)
    {
        var result = await accounts.DeleteShopAsync(id, confirm, User.BusinessId(), ct);
        if (result.Failure == FailureKind.NotFound) return NotFound();
        if (result.IsSuccess)
        {
            logger.LogWarning("The platform owner deleted a shop and all of its data."); // no names or addresses in the log
            Success = Text.PlatformDeleted;
        }
        else Failure = result.FieldErrors.GetValueOrDefault("confirm") ?? result.Message;
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
