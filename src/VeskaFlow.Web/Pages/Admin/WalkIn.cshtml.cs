using System.Globalization;
using VeskaFlow.Core.Scheduling;
using VeskaFlow.Web.Services;
using Microsoft.AspNetCore.Mvc;

namespace VeskaFlow.Web.Pages.Admin;

/// <summary>
/// Staff record a customer who is at the counter now (BUSINESS_RULES 8.2). The booking skips the lead time but still
/// respects working hours, closures and overlaps, and the database constraint stays the final guard.
/// </summary>
public class WalkInModel(BookingService booking, CatalogAdminService catalog, ShopSettingsService settings, TimeProvider time) : AdminPageModel
{
    [BindProperty(SupportsGet = true)] public Guid? ServiceId { get; set; }
    [BindProperty(SupportsGet = true)] public Guid? StaffId { get; set; }

    /// <summary>What the customer needs when "Other" is chosen (the service is not in the list).</summary>
    [BindProperty(SupportsGet = true)] public string? ServiceNote { get; set; }
    [BindProperty] public WalkInForm Input { get; set; } = new();

    public IReadOnlyList<ServiceRow> Services { get; private set; } = [];
    public IReadOnlyList<StaffRow> Staff { get; private set; } = [];
    public IReadOnlyList<DateTime> Times { get; private set; } = [];
    public string TimeZone { get; private set; } = "Asia/Manila";
    public bool Searched { get; private set; }

    /// <summary>True when the chosen service is the "Other" one.</summary>
    public bool SelectedIsOther => Services.Any(s => s.Id == ServiceId && s.IsOther);

    public async Task<IActionResult> OnGetAsync(CancellationToken ct)
    {
        await LoadAsync(ct);
        if (ScopeStaffId is { } only) StaffId = only; // a Staff login tied to one person books for that person only
        ServiceNote = ServiceNote?.Trim();
        if (SelectedIsOther && string.IsNullOrEmpty(ServiceNote))
        {
            Errors = new Dictionary<string, string> { ["serviceNote"] = Text.OtherServiceRequired }; // say what "Other" is first
            return Page();
        }
        if (ServiceId is not null) await LoadTimesAsync(ct);
        if (Searched && Input.IdempotencyKey is null) Input = new WalkInForm { Name = Text.WalkInNameDefault, IdempotencyKey = Guid.NewGuid().ToString("N") };
        return Page();
    }

    public async Task<IActionResult> OnPostAsync(CancellationToken ct)
    {
        await LoadAsync(ct);
        if (ScopeStaffId is { } fixedStaff) StaffId = fixedStaff;
        if (ServiceId is null || !Services.Any(s => s.Id == ServiceId && s.IsActive)) return NotFound();
        if (!DateTime.TryParse(Input.Start, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var start))
        {
            await LoadTimesAsync(ct);
            Errors = new Dictionary<string, string> { ["start"] = Text.WalkInNoTimes };
            return Page();
        }

        // The business comes from the signed-in user; the server re-derives availability for the chosen time.
        var result = await booking.CreateAsync(new CreateBookingRequest(
            BusinessId, ServiceId.Value, StaffId, start, Input.Name ?? "", Input.Phone ?? "", null,
            Input.IdempotencyKey ?? "", StaffActor, ServiceNote?.Trim()), ct);

        if (result.IsSuccess)
        {
            var b = result.Value!.Booking;
            Success = string.Format(Text.WalkInBooked, DisplayFormat.Time(b.StartsAtUtc, TimeZone), b.StaffName);
            return RedirectToPage("/Admin/Index");
        }
        if (result.Failure == FailureKind.SlotTaken)
        {
            Failure = Text.WalkInTaken;
            return RedirectToPage(new { serviceId = ServiceId, staffId = StaffId, serviceNote = ServiceNote });
        }
        if (result.Failure == FailureKind.Validation) Errors = result.FieldErrors;
        else if (result.Failure == FailureKind.NotFound) return NotFound();
        else Failure = result.Message;
        await LoadTimesAsync(ct);
        return Page();
    }

    private async Task LoadAsync(CancellationToken ct)
    {
        Services = (await catalog.ListServicesAsync(BusinessId, ct)).Where(s => s.IsActive).ToList();
        Staff = (await catalog.ListStaffAsync(BusinessId, ct)).Where(s => s.IsActive).ToList();
        TimeZone = (await settings.GetSettingsAsync(BusinessId, ct))?.TimeZone ?? "Asia/Manila";
    }

    private async Task LoadTimesAsync(CancellationToken ct)
    {
        if (ServiceId is null || !Services.Any(s => s.Id == ServiceId)) return;
        Searched = true;
        var today = LocalTime.LocalDateOf(time.GetUtcNow().UtcDateTime, TimeZone);
        var result = await booking.GetFreeStartsAsync(BusinessId, ServiceId.Value, StaffId, today, walkIn: true, ct);
        if (result.IsSuccess) Times = result.Value!;
    }

    public class WalkInForm
    {
        public string? Name { get; set; }
        public string? Phone { get; set; }
        public string? Start { get; set; }
        public string? IdempotencyKey { get; set; }
    }
}
