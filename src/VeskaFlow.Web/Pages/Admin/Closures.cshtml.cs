using System.Globalization;
using VeskaFlow.Web.Services;
using Microsoft.AspNetCore.Mvc;

namespace VeskaFlow.Web.Pages.Admin;

public class ClosuresModel(ShopSettingsService settings, CatalogAdminService catalog) : AdminPageModel
{
    [BindProperty] public ClosureForm Input { get; set; } = new();

    public IReadOnlyList<ClosureRow> Rows { get; private set; } = [];
    public IReadOnlyList<StaffRow> Staff { get; private set; } = [];

    public async Task<IActionResult> OnGetAsync(CancellationToken ct)
    {
        await LoadAsync(ct);
        return Page();
    }

    public async Task<IActionResult> OnPostAddAsync(CancellationToken ct)
    {
        if (!DateOnly.TryParseExact(Input.FromDate, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var from)
            || !DateOnly.TryParseExact(Input.ToDate, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var to))
        {
            Errors = new Dictionary<string, string> { ["toDate"] = Text.ClosureDatesRequired };
            await LoadAsync(ct);
            return Page();
        }

        // The staff id is checked against the signed-in business inside the service; a foreign id is "not found".
        var result = await settings.AddClosureAsync(BusinessId, new ClosureInput(Input.StaffId, from, to, Input.Reason), ct);
        if (result.Failure == FailureKind.NotFound) return NotFound();
        if (!result.IsSuccess)
        {
            Errors = result.FieldErrors;
            await LoadAsync(ct);
            return Page();
        }

        // Closing days never cancels bookings. Say how many are affected so the owner handles each one deliberately.
        var affected = result.Value!.AffectedBookings;
        Success = affected > 0 ? string.Format(Text.ClosureSavedAffected, affected) : Text.ClosureSaved;
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostDeleteAsync(Guid id, CancellationToken ct)
    {
        var result = await settings.DeleteClosureAsync(BusinessId, id, ct);
        if (!result.IsSuccess) return NotFound();
        Success = Text.ClosureRemoved;
        return RedirectToPage();
    }

    private async Task LoadAsync(CancellationToken ct)
    {
        Rows = await settings.ListClosuresAsync(BusinessId, ct);
        Staff = (await catalog.ListStaffAsync(BusinessId, ct)).Where(s => s.IsActive).ToList();
    }

    public class ClosureForm
    {
        public Guid? StaffId { get; set; }
        public string? FromDate { get; set; }
        public string? ToDate { get; set; }
        public string? Reason { get; set; }
    }
}
