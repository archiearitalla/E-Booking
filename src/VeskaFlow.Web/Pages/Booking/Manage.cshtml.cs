using VeskaFlow.Web.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.RateLimiting;

namespace VeskaFlow.Web.Pages.Booking;

[EnableRateLimiting(RateLimitPolicies.Booking)]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public class ManageModel(BookingService booking, IConfiguration config) : PageModel
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

    /// <summary>The address of this page: the private link the customer keeps to see or cancel the booking.</summary>
    public string ManageUrl(string token)
    {
        var configured = config["App:PublicBaseUrl"];
        var baseUrl = string.IsNullOrWhiteSpace(configured) ? $"{Request.Scheme}://{Request.Host}" : configured.Trim().TrimEnd('/');
        return $"{baseUrl}/booking/{token}";
    }

    /// <summary>Downloads the booking as a calendar file (.ics) to add to the phone's calendar.</summary>
    public async Task<IActionResult> OnGetCalendarAsync(string token, CancellationToken ct)
    {
        var details = await booking.GetByTokenAsync(token, ct);
        if (details is null) return NotFound();
        var b = details.Booking;
        if (b.Status == VeskaFlow.Core.Domain.BookingStatus.Cancelled) return NotFound();

        var ics = CalendarFile.Ics(b.Id, details.BusinessName, b.ServiceName, b.StaffName, b.StartsAtUtc, b.EndsAtUtc,
            ManageUrl(token), DateTime.UtcNow);
        return File(System.Text.Encoding.UTF8.GetBytes(ics), "text/calendar; charset=utf-8", "booking.ics");
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
