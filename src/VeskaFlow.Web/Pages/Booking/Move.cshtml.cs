using System.Globalization;
using VeskaFlow.Core.Scheduling;
using VeskaFlow.Web.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.RateLimiting;

namespace VeskaFlow.Web.Pages.Booking;

/// <summary>The customer moves their own booking to another free time. The private link authorizes exactly one booking.</summary>
[EnableRateLimiting(RateLimitPolicies.Booking)]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public class MoveModel(BookingService booking, TimeProvider time) : PageModel
{
    public BookingDetails? Details { get; private set; }
    public IReadOnlyList<DateTime> Times { get; private set; } = [];
    public bool Searched { get; private set; }
    public string Today { get; private set; } = "";
    public string? Message { get; private set; }

    [BindProperty(SupportsGet = true)] public string? Date { get; set; }

    public async Task<IActionResult> OnGetAsync(string token, CancellationToken ct)
    {
        if (!await LoadAsync(token, ct)) return NotFound();
        if (!Details!.CanMove) return RedirectToPage("Manage", new { token }); // the manage page explains why

        if (DateOnly.TryParseExact(Date, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
        {
            Searched = true;
            var result = await booking.GetRescheduleStartsByTokenAsync(token, date, ct);
            if (result.Failure == FailureKind.NotFound) return NotFound();
            if (result.IsSuccess) Times = result.Value!;
        }
        Message = TempData[MessageKey] as string;
        return Page();
    }

    public async Task<IActionResult> OnPostAsync(string token, string? start, CancellationToken ct)
    {
        if (!DisplayFormat.TryParseStart(start, out var utc)) return RedirectToPage(new { token });
        var result = await booking.RescheduleByTokenAsync(token, utc, ct);
        if (result.Failure == FailureKind.NotFound) return NotFound();
        if (!result.IsSuccess)
        {
            // A refusal (cutoff passed, slot just taken) is carried to the next page view.
            TempData[MessageKey] = result.Message;
            return RedirectToPage(new { token });
        }
        TempData[ManageModel.NoticeKey] = Text.MoveDone;
        return RedirectToPage("Manage", new { token });
    }

    private async Task<bool> LoadAsync(string token, CancellationToken ct)
    {
        Details = await booking.GetByTokenAsync(token, ct);
        if (Details is null) return false;
        Today = LocalTime.LocalDateOf(time.GetUtcNow().UtcDateTime, Details.TimeZone).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        return true;
    }

    private const string MessageKey = "MoveMessage";
}
