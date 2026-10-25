using VeskaFlow.Web.Services;
using Microsoft.AspNetCore.Mvc;

namespace VeskaFlow.Web.Pages.Admin;

public class SettingsModel(ShopSettingsService settings, IConfiguration config) : AdminPageModel
{
    [BindProperty] public SettingsForm Input { get; set; } = new();

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
