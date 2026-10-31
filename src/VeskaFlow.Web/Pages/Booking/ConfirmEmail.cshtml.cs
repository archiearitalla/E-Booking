using VeskaFlow.Web.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.RateLimiting;

namespace VeskaFlow.Web.Pages.Booking;

/// <summary>
/// The page behind the link emailed to a customer of a shop that requires email confirmation. Opening it only shows the booking; the
/// button confirms (so a mail program that opens links to check them cannot confirm by itself). The secret in the link authorizes
/// exactly one booking, and an unknown or malformed one is "not found".
/// </summary>
[EnableRateLimiting(RateLimitPolicies.Booking)]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public class ConfirmEmailModel(BookingService booking) : PageModel
{
    public BookingDetails? Details { get; private set; }
    public string? Message { get; private set; }

    public async Task<IActionResult> OnGetAsync(string token, CancellationToken ct)
    {
        Details = await booking.GetByEmailTokenAsync(token, ct);
        if (Details is null) return NotFound();
        // Already confirmed (or finished): nothing to press, go to the booking page.
        if (Details.Booking.Status != VeskaFlow.Core.Domain.BookingStatus.Held)
            return RedirectToPage("Manage", new { token = Details.Booking.ManageToken });
        return Page();
    }

    public async Task<IActionResult> OnPostAsync(string token, CancellationToken ct)
    {
        var result = await booking.ConfirmByEmailAsync(token, ct);
        if (result.Failure == FailureKind.NotFound) return NotFound();
        if (!result.IsSuccess)
        {
            Details = await booking.GetByEmailTokenAsync(token, ct);
            if (Details is null) return NotFound();
            Message = result.Message;
            return Page();
        }
        TempData[ManageModel.NoticeKey] = Text.ConfirmedByEmail;
        return RedirectToPage("Manage", new { token = result.Value!.ManageToken });
    }
}
