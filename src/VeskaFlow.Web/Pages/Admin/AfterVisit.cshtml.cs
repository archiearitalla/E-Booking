using VeskaFlow.Core.Domain;
using VeskaFlow.Web.Services;
using Microsoft.AspNetCore.Mvc;

namespace VeskaFlow.Web.Pages.Admin;

/// <summary>
/// Opens right after a booking is marked completed: send the customer a message on any app, save the contact and set a follow-up.
/// The system sends nothing; staff send it from their own phone.
/// </summary>
public class AfterVisitModel(
    AdminDayService days, FollowUpService followUps, VisitRecordService records, CatalogAdminService catalog,
    ShopSettingsService settings, IConfiguration config, BookingExtraService extras) : AdminPageModel
{
    public DayBooking? Booking { get; private set; }
    public IReadOnlyList<ServiceRow> Services { get; private set; } = [];
    public string ShopName { get; private set; } = "";
    public string BookingUrl { get; private set; } = "";
    public string TimeZone { get; private set; } = "";

    /// <summary>The shop's setting: may staff type the payment total? When false the page shows it but cannot change it.</summary>
    public bool AllowManualTotal { get; private set; } = true;
    public string DayLink { get; private set; } = "/Admin";

    public string ThankYou
    {
        get
        {
            var name = Booking!.CustomerName.Trim();
            var first = name.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? name;
            return Text.ThankYouMessage(first, ShopName, Booking.ServiceName, BookingUrl, Booking.FollowUpOn, Booking.FollowUpService);
        }
    }

    public async Task<IActionResult> OnGetAsync(Guid id, CancellationToken ct)
    {
        var loaded = await LoadAsync(id, ct);
        if (loaded is not null) return loaded;
        // Only a completed visit has an "after the visit"; anything else goes back to the day list.
        return Booking!.Status == BookingStatus.Completed ? Page() : Redirect("/Admin");
    }

    /// <summary>Adds a service that was done on top of the booked one. "Other" takes a description and a price typed by staff.</summary>
    public async Task<IActionResult> OnPostAddServiceAsync(Guid id, Guid serviceId, string? note, string? price, CancellationToken ct)
    {
        decimal? typed = null;
        var text = (price ?? "").Replace(",", "").Replace("\u20B1", "").Trim();
        if (text.Length > 0)
        {
            if (!decimal.TryParse(text, System.Globalization.NumberStyles.AllowDecimalPoint, System.Globalization.CultureInfo.InvariantCulture, out var parsed))
            {
                Failure = Text.ExtraPriceInvalid;
                return RedirectToPage(new { id });
            }
            typed = parsed;
        }

        var result = await extras.AddAsync(BusinessId, id, serviceId, note, typed, StaffActor, ct, ScopeStaffId);
        if (result.Failure == FailureKind.NotFound) return NotFound();
        if (result.IsSuccess) Success = Text.ExtraAdded + (result.Value!.PaymentReset ? Text.ExtraPaymentReset : "");
        else Failure = result.FieldErrors.Values.FirstOrDefault() ?? result.Message;
        return RedirectToPage(new { id });
    }

    public async Task<IActionResult> OnPostRemoveServiceAsync(Guid id, Guid extraId, CancellationToken ct)
    {
        var result = await extras.RemoveAsync(BusinessId, id, extraId, StaffActor, ct, ScopeStaffId);
        if (result.Failure == FailureKind.NotFound) return NotFound();
        if (result.IsSuccess) Success = Text.ExtraRemoved + (result.Value!.PaymentReset ? Text.ExtraPaymentReset : "");
        else Failure = result.Message;
        return RedirectToPage(new { id });
    }

    /// <summary>Saves the payment total of this visit. Blank clears it (the quoted price counts again).</summary>
    public async Task<IActionResult> OnPostPaymentAsync(Guid id, string? paidAmount, CancellationToken ct)
    {
        decimal? amount = null;
        var typed = (paidAmount ?? "").Replace(",", "").Replace("\u20B1", "").Trim();
        if (typed.Length > 0)
        {
            if (!decimal.TryParse(typed, System.Globalization.NumberStyles.AllowDecimalPoint, System.Globalization.CultureInfo.InvariantCulture, out var parsed))
            {
                Failure = Text.AfterVisitPaymentInvalid;
                return RedirectToPage(new { id });
            }
            amount = parsed;
        }

        var result = await records.SetPaidAmountAsync(BusinessId, id, amount, ct, ScopeStaffId, StaffActor);
        if (result.Failure == FailureKind.NotFound) return NotFound();
        if (result.IsSuccess) Success = Text.AfterVisitPaymentSaved;
        else Failure = result.FieldErrors.Values.FirstOrDefault() ?? result.Message;
        return RedirectToPage(new { id });
    }

    /// <summary>Saves the optional medical record of this visit.</summary>
    public async Task<IActionResult> OnPostNotesAsync(Guid id, string? visitNotes, CancellationToken ct)
    {
        var result = await records.SetNotesAsync(BusinessId, id, visitNotes, ct, ScopeStaffId);
        if (result.Failure == FailureKind.NotFound) return NotFound();
        if (result.IsSuccess) Success = Text.VisitNotesSaved;
        else Failure = result.FieldErrors.Values.FirstOrDefault() ?? result.Message;
        return RedirectToPage(new { id });
    }

    public async Task<IActionResult> OnPostFollowUpAsync(
        Guid id, string? followUpOn, string? followUpNote, Guid? followUpServiceId, string? clear, CancellationToken ct)
    {
        DateOnly? when = null;
        if (clear is null)
        {
            if (!DateOnly.TryParseExact(followUpOn, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.None, out var parsed))
            {
                Failure = Text.FollowUpDateInvalid;
                return RedirectToPage(new { id });
            }
            when = parsed;
        }

        var result = await followUps.SetAsync(BusinessId, id, when, followUpNote, followUpServiceId, ct, ScopeStaffId);
        if (result.Failure == FailureKind.NotFound) return NotFound();
        if (result.IsSuccess) Success = when is null ? Text.FollowUpCleared : Text.FollowUpSaved;
        else Failure = result.FieldErrors.Values.FirstOrDefault() ?? result.Message;
        return RedirectToPage(new { id });
    }

    private async Task<IActionResult?> LoadAsync(Guid id, CancellationToken ct)
    {
        Booking = await days.GetBookingAsync(BusinessId, id, ct, ScopeStaffId);
        var shop = await settings.GetSettingsAsync(BusinessId, ct);
        if (Booking is null || shop is null) return NotFound(); // unknown id and another business's id look the same

        Services = (await catalog.ListServicesAsync(BusinessId, ct)).Where(s => s.IsActive).ToList();
        ShopName = shop.Name;
        TimeZone = shop.TimeZone;
        AllowManualTotal = shop.AllowManualTotal;
        BookingUrl = QrCodes.BookingUrl(config["App:PublicBaseUrl"], Request.Scheme, Request.Host.Value, shop.Slug);
        var date = VeskaFlow.Core.Scheduling.LocalTime.LocalDateOf(Booking.StartsAtUtc, shop.TimeZone);
        DayLink = $"/Admin?date={DisplayFormat.DateParam(date)}&status=done";
        return null;
    }
}
