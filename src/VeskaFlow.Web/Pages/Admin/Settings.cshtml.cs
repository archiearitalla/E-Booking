using VeskaFlow.Web.Services;
using Microsoft.AspNetCore.Mvc;

namespace VeskaFlow.Web.Pages.Admin;

public class SettingsModel(ShopSettingsService settings, CatalogAdminService catalog, IConfiguration config) : AdminPageModel
{
    [BindProperty] public SettingsForm Input { get; set; } = new();

    /// <summary>The shop's opening hours as the form shows them.</summary>
    public DayFields ShopRange { get; private set; } = new("", "", "", "");
    public bool HoursFormSubmitted { get; private set; }

    private async Task LoadHoursAsync(CancellationToken ct) =>
        ShopRange = HoursForm.FillRange(await catalog.GetShopHoursAsync(BusinessId, ct));

    /// <summary>Saves the opening hours and copies them to every staff member.</summary>
    public async Task<IActionResult> OnPostHoursAsync(CancellationToken ct)
    {
        HoursFormSubmitted = true;
        var read = HoursForm.ReadSingleRange(Request.Form);

        ServiceResult<SaveOutcome> result;
        if (read.Incomplete)
            result = ServiceResult<SaveOutcome>.Invalid(new Dictionary<string, string> { ["hours"] = Text.HoursIncomplete });
        else if (read.BadBreak)
            result = ServiceResult<SaveOutcome>.Invalid(new Dictionary<string, string> { ["hours"] = Text.HoursBreakInvalid });
        else
            result = await catalog.SaveShopHoursAsync(BusinessId, read.Windows, ct);

        if (result.Failure == FailureKind.NotFound) return NotFound();
        if (!result.IsSuccess)
        {
            Errors = result.FieldErrors;
            var current = await settings.GetSettingsAsync(BusinessId, ct);
            if (current is null) return NotFound();
            Apply(current);
            BuildQr();
            ShopRange = HoursForm.ReadRange(Request.Form); // keep what was picked
            return Page();
        }

        var affected = result.Value!.AffectedBookings;
        Success = affected > 0 ? string.Format(Text.ShopHoursSavedAffected, affected) : Text.ShopHoursSaved;
        return Redirect("/Admin/Settings#opening-hours");
    }

    public string Slug { get; private set; } = "";
    public string Plan { get; private set; } = "";

    /// <summary>The shop's booking page address and its QR code (an SVG that scales to any size).</summary>
    public string BookingUrl { get; private set; } = "";
    public string QrSvg { get; private set; } = "";

    public async Task<IActionResult> OnGetAsync(CancellationToken ct)
    {
        var current = await settings.GetSettingsAsync(BusinessId, ct);
        if (current is null) return NotFound();
        Apply(current);
        BuildQr();
        await LoadHoursAsync(ct);
        return Page();
    }

    /// <summary>Downloads the QR code as an SVG file for printing (signage, flyers, a table card).</summary>
    public async Task<IActionResult> OnGetQrAsync(CancellationToken ct)
    {
        var current = await settings.GetSettingsAsync(BusinessId, ct);
        if (current is null) return NotFound();
        Apply(current);
        BuildQr();
        return File(System.Text.Encoding.UTF8.GetBytes(QrSvg), "image/svg+xml", $"{Slug}-booking-qr.svg");
    }

    private void BuildQr()
    {
        BookingUrl = QrCodes.BookingUrl(config["App:PublicBaseUrl"], Request.Scheme, Request.Host.Value, Slug);
        QrSvg = QrCodes.Svg(BookingUrl);
    }

    public async Task<IActionResult> OnPostAsync(CancellationToken ct)
    {
        var result = await settings.SaveSettingsAsync(BusinessId,
            new SettingsInput(Input.Name, Input.TimeZone, Input.SlotMinutes, Input.LeadTimeMinutes, Input.HorizonDays, Input.CancelCutoffHours, Input.BrandColor), ct);

        if (result.Failure == FailureKind.NotFound) return NotFound();
        if (!result.IsSuccess)
        {
            Errors = result.FieldErrors;
            var current = await settings.GetSettingsAsync(BusinessId, ct);
            Slug = current?.Slug ?? "";
            Plan = current?.Plan ?? "";
            BuildQr();
            await LoadHoursAsync(ct);
            return Page(); // keep what was typed
        }

        Success = result.Value ? Text.SettingsSavedZone : Text.SettingsSaved;
        return RedirectToPage();
    }

    private void Apply(ShopSettings s)
    {
        Slug = s.Slug;
        Plan = s.Plan;
        Input = new SettingsForm
        {
            Name = s.Name, TimeZone = s.TimeZone, SlotMinutes = s.SlotMinutes,
            LeadTimeMinutes = s.LeadTimeMinutes, HorizonDays = s.HorizonDays, CancelCutoffHours = s.CancelCutoffHours,
            BrandColor = s.BrandColor,
        };
    }

    public class SettingsForm
    {
        public string? Name { get; set; }
        public string? TimeZone { get; set; }
        public int SlotMinutes { get; set; } = 30;
        public int LeadTimeMinutes { get; set; } = 60;
        public int HorizonDays { get; set; } = 30;
        public int CancelCutoffHours { get; set; } = 2;
        public string? BrandColor { get; set; } = BrandColors.Default;
    }
}
