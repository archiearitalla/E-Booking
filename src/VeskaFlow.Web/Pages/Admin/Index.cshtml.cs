using VeskaFlow.Core.Domain;
using VeskaFlow.Web.Services;
using Microsoft.AspNetCore.Mvc;

namespace VeskaFlow.Web.Pages.Admin;

/// <summary>Day view of bookings with complete, no-show and cancel. All rules live in BookingService.</summary>
public class IndexModel(AdminDayService days, BookingService bookings) : AdminPageModel
{
    [BindProperty(SupportsGet = true)] public DateOnly? Date { get; set; }

    public DayView? View { get; private set; }

    public async Task<IActionResult> OnGetAsync(CancellationToken ct)
    {
        View = await days.GetDayAsync(BusinessId, Date, ct);
        return View is null ? NotFound() : Page();
    }

    public async Task<IActionResult> OnPostTransitionAsync(
        Guid id, string? action, uint? version, string? reason, DateOnly? date, CancellationToken ct)
    {
        BookingStatus? target = action switch
        {
            "complete" => BookingStatus.Completed,
            "noshow" => BookingStatus.NoShow,
            "cancel" => BookingStatus.Cancelled,
            _ => null,
        };
        if (target is null) return BadRequest();

        var trimmed = string.IsNullOrWhiteSpace(reason) ? null : reason.Trim();
        if (trimmed is { Length: > 200 })
        {
            Failure = Text.ReasonTooLong;
            return RedirectToPage(new { date = date?.ToString("yyyy-MM-dd") });
        }

        // The booking must belong to the signed-in business; another tenant's id is simply "not found".
        var result = await bookings.TransitionAsync(BusinessId, id, target.Value, StaffActor, trimmed, version, ct);
        if (result.Failure == FailureKind.NotFound) return NotFound();

        if (result.IsSuccess) Success = Text.BookingUpdated;
        else Failure = result.Message;
        return RedirectToPage(new { date = date?.ToString("yyyy-MM-dd") });
    }
}
