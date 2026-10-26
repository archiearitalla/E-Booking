using System.Globalization;
using VeskaFlow.Core.Scheduling;
using VeskaFlow.Web.Services;
using Microsoft.AspNetCore.Mvc;

namespace VeskaFlow.Web.Pages.Admin;

/// <summary>Staff move a confirmed booking to another free time with the same staff member. The server re-checks the time.</summary>
public class RescheduleModel(BookingService bookings, ShopSettingsService settings, TimeProvider time) : AdminPageModel
{
    public BookingSummary? Booking { get; private set; }
    public string TimeZone { get; private set; } = "Asia/Manila";
    public IReadOnlyList<DateTime> Times { get; private set; } = [];
    public bool Searched { get; private set; }
    public string Today { get; private set; } = "";

    [BindProperty(SupportsGet = true)] public string? Date { get; set; }

    public async Task<IActionResult> OnGetAsync(Guid id, CancellationToken ct)
    {
        if (!await LoadAsync(id, ct)) return NotFound();
        if (DateOnly.TryParseExact(Date, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
        {
            Searched = true;
            var result = await bookings.GetRescheduleStartsAsync(BusinessId, id, date, ct);
            if (result.IsSuccess) Times = result.Value!;
        }
        return Page();
    }

    public async Task<IActionResult> OnPostAsync(Guid id, string? start, CancellationToken ct)
    {
        if (!DisplayFormat.TryParseStart(start, out var utc)) return RedirectToPage(new { id });
        var result = await bookings.RescheduleAsync(BusinessId, id, utc, StaffActor, ct);
        if (result.Failure == FailureKind.NotFound) return NotFound();
        if (!result.IsSuccess)
        {
            Failure = result.Message;
            return RedirectToPage(new { id });
        }
        Success = Text.RescheduleDone;
        return Redirect("/Admin");
    }

    private async Task<bool> LoadAsync(Guid id, CancellationToken ct)
    {
        Booking = await bookings.LoadSummaryAsync(BusinessId, id, ct);
        if (Booking is null) return false;
        TimeZone = (await settings.GetSettingsAsync(BusinessId, ct))?.TimeZone ?? "Asia/Manila";
        Today = LocalTime.LocalDateOf(time.GetUtcNow().UtcDateTime, TimeZone).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        return true;
    }
}
