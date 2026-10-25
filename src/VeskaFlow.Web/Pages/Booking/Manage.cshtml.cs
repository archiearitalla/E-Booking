using VeskaFlow.Web.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.RateLimiting;

namespace VeskaFlow.Web.Pages.Booking;

[EnableRateLimiting(RateLimitPolicies.Booking)]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public class ManageModel(BookingService booking) : PageModel
{
    public BookingDetails? Details { get; private set; }
    public bool JustBooked { get; private set; }
    public string? Message { get; private set; }

    public async Task<IActionResult> OnGetAsync(string token, int? confirmed, CancellationToken ct)
    {
        Details = await booking.GetByTokenAsync(token, ct);
        if (Details is null) return NotFound(); // same page as an unknown shop
        JustBooked = confirmed == 1;
        Message = TempData[MessageKey] as string;
        return Page();
    }

    public async Task<IActionResult> OnPostCancelAsync(string token, CancellationToken ct)
    {
        var result = await booking.CancelByTokenAsync(token, ct: ct);
        if (result.Failure == FailureKind.NotFound) return NotFound();

        // Post/redirect/get so a refresh never re-submits. A refusal (for example: online cancellation closes
        // N hours before the appointment) is carried to the next page view.
        if (!result.IsSuccess) TempData[MessageKey] = result.Message;
        return RedirectToPage(new { token });
    }

    private const string MessageKey = "ManageMessage";
}
