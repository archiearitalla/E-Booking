using VeskaFlow.Core.Domain;
using VeskaFlow.Web.Services;
using Microsoft.AspNetCore.Mvc;

namespace VeskaFlow.Web.Pages.Admin;

/// <summary>Day view of bookings with complete, no-show and cancel. All rules live in BookingService.</summary>
public class IndexModel(AdminDayService days, BookingService bookings, ShopSettingsService settings) : AdminPageModel
{
    /// <summary>Downloads the customer as a contact card (.vcf) to save to the phone's contacts.</summary>
    public async Task<IActionResult> OnGetContactAsync(Guid id, CancellationToken ct)
    {
        var contact = await days.GetContactAsync(BusinessId, id, ct);
        var shop = await settings.GetSettingsAsync(BusinessId, ct);
        if (contact is null || shop is null) return NotFound(); // unknown id and another business's id look the same
        return File(System.Text.Encoding.UTF8.GetBytes(ContactFile.Vcf(contact.Name, contact.Phone, contact.Email, shop.Name)),
            "text/vcard; charset=utf-8", ContactFile.FileName(contact.Name));
    }
    public const string Confirmed = "confirmed";
    public const string Cancelled = "cancelled";
    public const string Done = "done";

    [BindProperty(SupportsGet = true)] public DateOnly? Date { get; set; }

    /// <summary>Which bookings to list: confirmed (the default), cancelled, or done (completed and no-show).</summary>
    [BindProperty(SupportsGet = true)] public string? Status { get; set; }

    public DayView? View { get; private set; }

    public string Filter => Status is Cancelled or Done ? Status : Confirmed;

    public IReadOnlyList<DayBooking> Shown => View!.Bookings.Where(b => Matches(Filter, b.Status)).ToList();

    public int CountFor(string filter) => View!.Bookings.Count(b => Matches(filter, b.Status));

    /// <summary>Confirmed means still on the schedule (held, pending or confirmed); done means completed or no-show.</summary>
    public static bool Matches(string filter, BookingStatus status) => filter switch
    {
        Cancelled => status == BookingStatus.Cancelled,
        Done => status is BookingStatus.Completed or BookingStatus.NoShow,
        _ => status.OccupiesSlot(),
    };

    public async Task<IActionResult> OnGetAsync(CancellationToken ct)
    {
        View = await days.GetDayAsync(BusinessId, Date, ct);
        return View is null ? NotFound() : Page();
    }

    public async Task<IActionResult> OnPostTransitionAsync(
        Guid id, string? action, uint? version, string? reason, DateOnly? date, string? status, CancellationToken ct)
    {
        BookingStatus? target = action switch
        {
            "complete" => BookingStatus.Completed,
            "noshow" => BookingStatus.NoShow,
            "cancel" => BookingStatus.Cancelled,
            _ => null,
        };
        if (target is null) return BadRequest();

        // Stay on the same day and the same filter after the change.
        var back = new { date = date?.ToString("yyyy-MM-dd"), status = status is Cancelled or Done ? status : null };

        var trimmed = string.IsNullOrWhiteSpace(reason) ? null : reason.Trim();
        if (trimmed is { Length: > 200 })
        {
            Failure = Text.ReasonTooLong;
            return RedirectToPage(back);
        }

        // The booking must belong to the signed-in business; another tenant's id is simply "not found".
        var result = await bookings.TransitionAsync(BusinessId, id, target.Value, StaffActor, trimmed, version, ct);
        if (result.Failure == FailureKind.NotFound) return NotFound();

        if (result.IsSuccess) Success = Text.BookingUpdated;
        else Failure = result.FieldErrors.GetValueOrDefault("reason") ?? result.Message;

        // Right after "Mark completed", open the page to message the customer, save the contact and set a follow-up.
        if (result.IsSuccess && target == BookingStatus.Completed) return RedirectToPage("/Admin/AfterVisit", new { id });
        return RedirectToPage(back);
    }
}
