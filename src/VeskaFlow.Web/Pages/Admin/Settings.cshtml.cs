using VeskaFlow.Web.Services;
using Microsoft.AspNetCore.Mvc;

namespace VeskaFlow.Web.Pages.Admin;

[AdminAccess(AdminLevel.Owner)]
[RequestSizeLimit(1_048_576)] // a logo is at most 256 KB; nothing bigger is read
public class SettingsModel(ShopSettingsService settings, CatalogAdminService catalog, IConfiguration config, LogoService logos, BillingInfo billing, PlatformImageService images, AiAccess ai) : AdminPageModel
{
    [BindProperty] public SettingsForm Input { get; set; } = new();

    /// <summary>The shop's opening hours as the form shows them.</summary>
    public DayFields ShopRange { get; private set; } = new("", "", "", "");
    public bool HoursFormSubmitted { get; private set; }

    /// <summary>Changes when the logo does; null when the shop has none.</summary>
    public long? LogoVersion { get; private set; }

    /// <summary>Until when the paid more-emails add-on is active (null: not bought), and the shop time zone to show it in.</summary>
    public DateTime? EmailAddonUntilUtc { get; private set; }
    /// <summary>The AI receptionist add-on: until when it is on (null: not bought) and this month's use.</summary>
    public DateTime? AiAddonUntilUtc { get; private set; }
    public AiUsageSummary AiUsage { get; private set; } = new(0, 0, 0, 0);
    public BillingInfo Billing => billing;
    public long? PayQrVersion { get; private set; }
    public string TimeZoneName { get; private set; } = "Asia/Manila";

    /// <summary>Loads what the page shows besides the settings form: the opening hours and the logo.</summary>
    private async Task LoadHoursAsync(CancellationToken ct)
    {
        ShopRange = HoursForm.FillRange(await catalog.GetShopHoursAsync(BusinessId, ct));
        LogoVersion = await logos.VersionAsync(BusinessId, ct);
        PayQrVersion = await images.VersionAsync(PlatformImageService.GcashQr, ct);
        AiAddonUntilUtc = await ai.AddonUntilAsync(BusinessId, ct);
        AiUsage = await ai.UsageAsync(BusinessId, ct);
    }

    /// <summary>Saves the chosen picture as the shop's logo (PNG, JPEG or WebP, up to 256 KB).</summary>
    public async Task<IActionResult> OnPostLogoAsync(IFormFile? logo, CancellationToken ct)
    {
        byte[]? bytes = null;
        if (logo is { Length: > 0 and <= LogoService.MaxBytes })
        {
            await using var stream = logo.OpenReadStream();
            using var buffer = new MemoryStream();
            await stream.CopyToAsync(buffer, ct);
            bytes = buffer.ToArray();
        }
        else if (logo is { Length: > LogoService.MaxBytes })
        {
            Failure = Text.LogoTooBig;
            return RedirectToPage();
        }

        var result = await logos.SaveAsync(BusinessId, bytes, ct);
        if (result.IsSuccess) Success = Text.LogoSaved;
        else Failure = result.FieldErrors.GetValueOrDefault("logo") ?? result.Message;
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostRemoveLogoAsync(CancellationToken ct)
    {
        await logos.RemoveAsync(BusinessId, ct);
        Success = Text.LogoRemoved;
        return RedirectToPage();
    }

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
            new SettingsInput(Input.Name, Input.TimeZone, Input.SlotMinutes, Input.LeadTimeMinutes, Input.HorizonDays, Input.CancelCutoffHours, Input.BrandColor, Input.FollowUpNoticeDays, Input.ReminderHours, Input.CustomerEmails, Input.AllowManualTotal), ct);

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
        EmailAddonUntilUtc = s.EmailAddonUntilUtc;
        TimeZoneName = s.TimeZone;
        Input = new SettingsForm
        {
            Name = s.Name, TimeZone = s.TimeZone, SlotMinutes = s.SlotMinutes,
            LeadTimeMinutes = s.LeadTimeMinutes, HorizonDays = s.HorizonDays, CancelCutoffHours = s.CancelCutoffHours,
            BrandColor = s.BrandColor, FollowUpNoticeDays = s.FollowUpNoticeDays, ReminderHours = s.ReminderHours, CustomerEmails = s.CustomerEmails, AllowManualTotal = s.AllowManualTotal,
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
        public int? FollowUpNoticeDays { get; set; } = 3;
        public int? ReminderHours { get; set; } = 24;
        public bool CustomerEmails { get; set; } = true;
        public bool AllowManualTotal { get; set; } = true;
    }
}
