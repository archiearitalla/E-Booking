using System.Runtime.InteropServices;
using VeskaFlow.Web.Services;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace VeskaFlow.Web.Pages.Admin;

public class AboutModel(IWebHostEnvironment environment, ShopSettingsService settings, BillingInfo billing, PlatformImageService images) : AdminPageModel
{
    public string Runtime { get; } = RuntimeInformation.FrameworkDescription;
    public string EnvironmentName { get; } = environment.EnvironmentName;

    public BillingInfo Billing => billing;
    public DateTime? AccessEndsUtc { get; private set; }
    public long? PayQrVersion { get; private set; }
    public string TimeZone { get; private set; } = "Asia/Manila";

    public async Task OnGetAsync(CancellationToken ct)
    {
        AccessEndsUtc = await settings.TrialEndsAtAsync(BusinessId, ct);
        PayQrVersion = await images.VersionAsync(PlatformImageService.GcashQr, ct);
        TimeZone = (await settings.GetSettingsAsync(BusinessId, ct))?.TimeZone ?? TimeZone;
    }
}
