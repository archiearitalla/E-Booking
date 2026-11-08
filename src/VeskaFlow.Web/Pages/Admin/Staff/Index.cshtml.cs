using VeskaFlow.Web.Services;
using Microsoft.AspNetCore.Mvc;

namespace VeskaFlow.Web.Pages.Admin.Staff;

[AdminAccess(AdminLevel.Manager)]
public class IndexModel(CatalogAdminService catalog, ShopSettingsService settings, BillingInfo billing) : AdminPageModel
{
    public IReadOnlyList<StaffRow> Rows { get; private set; } = [];
    public string Plan { get; private set; } = "FREE";
    public bool InTrial { get; private set; }
    public int ExtraStaff { get; private set; }
    public DateTime? ExtraStaffUntilUtc { get; private set; }
    public string TimeZoneName { get; private set; } = "Asia/Manila";
    public string ExtraStaffPrice => billing.ExtraStaffPrice;
    public string ContactUrl => billing.ContactUrl;

    public async Task<IActionResult> OnGetAsync(CancellationToken ct)
    {
        Rows = await catalog.ListStaffAsync(BusinessId, ct);
        var current = await settings.GetSettingsAsync(BusinessId, ct);
        Plan = current?.Plan ?? "FREE";
        InTrial = current?.TrialEndsAtUtc is not null;
        ExtraStaff = current is null ? 0 : PlanLimits.EffectiveExtra(current.ExtraStaff, current.ExtraStaffUntilUtc, DateTime.UtcNow);
        ExtraStaffUntilUtc = current?.ExtraStaff > 0 && current.ExtraStaffUntilUtc is { } u ? DateTime.SpecifyKind(u, DateTimeKind.Utc) : null;
        TimeZoneName = current?.TimeZone ?? "Asia/Manila";
        return Page();
    }
}
