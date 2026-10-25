using System.Globalization;
using VeskaFlow.Web.Services;
using Microsoft.AspNetCore.Mvc;

namespace VeskaFlow.Web.Pages.Admin.Staff;

public class EditModel(CatalogAdminService catalog) : AdminPageModel
{
    public const int WindowsPerDay = 2; // two windows per day allow a split shift (lunch break)

    /// <summary>Monday first; the database uses 0 = Sunday.</summary>
    public static readonly short[] DayOrder = [1, 2, 3, 4, 5, 6, 0];

    public static string DayName(short day) => CultureInfo.InvariantCulture.DateTimeFormat.GetDayName((DayOfWeek)day);

    [BindProperty] public StaffForm Input { get; set; } = new();

    public bool IsNew { get; private set; }
    public bool HoursFormSubmitted { get; private set; }
    public Guid? StaffId { get; private set; }
    public string TimeZone { get; private set; } = "";
    public IReadOnlyList<ServiceRow> AllServices { get; private set; } = [];
    public Dictionary<(short Day, int Index), (string Open, string Close)> HourFields { get; private set; } = [];

    public async Task<IActionResult> OnGetAsync(Guid? id, CancellationToken ct)
    {
        IsNew = id is null;
        StaffId = id;
        if (id is null)
        {
            AllServices = await catalog.ListServicesAsync(BusinessId, ct);
            Input.ServiceIds = AllServices.Where(s => s.IsActive).Select(s => s.Id).ToList(); // new staff: offer every active service
            FillHours([]);
            return Page();
        }

        var view = await catalog.GetStaffAsync(BusinessId, id.Value, ct);
        if (view is null) return NotFound(); // unknown id and another business's id look the same
        ApplyView(view);
        return Page();
    }

    public async Task<IActionResult> OnPostSaveAsync(Guid? id, CancellationToken ct)
    {
        IsNew = id is null;
        StaffId = id;
        var result = await catalog.SaveStaffAsync(BusinessId,
            new StaffInput(id, Input.Name, Input.IsActive, Input.SortOrder, Input.ServiceIds), ct);

        if (result.Failure == FailureKind.NotFound) return NotFound();
        if (!result.IsSuccess)
        {
            Errors = result.FieldErrors;
            await ReloadAsync(id, ct);
            return Page();
        }

        var outcome = result.Value!;
        Success = outcome.AffectedBookings > 0
            ? string.Format(Text.StaffSavedAffected, outcome.AffectedBookings)
            : Text.StaffSaved;
        return RedirectToPage(new { id = outcome.Id });
    }

    public async Task<IActionResult> OnPostHoursAsync(Guid id, CancellationToken ct)
    {
        StaffId = id;
        HoursFormSubmitted = true;
        var windows = new List<HourWindow>();
        var parseError = false;

        foreach (var day in DayOrder)
        for (var n = 0; n < WindowsPerDay; n++)
        {
            var open = Request.Form[$"open_{day}_{n}"].ToString().Trim();
            var close = Request.Form[$"close_{day}_{n}"].ToString().Trim();
            if (open.Length == 0 && close.Length == 0) continue;

            if (TimeOnly.TryParseExact(open, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var o)
                && TimeOnly.TryParseExact(close, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var c))
                windows.Add(new HourWindow(day, o, c));
            else
                parseError = true; // one of the two times is blank or malformed
        }

        ServiceResult<SaveOutcome> result;
        if (parseError)
            result = ServiceResult<SaveOutcome>.Invalid(new Dictionary<string, string> { ["hours"] = Text.HoursIncomplete });
        else
            result = await catalog.SaveHoursAsync(BusinessId, id, windows, ct);

        if (result.Failure == FailureKind.NotFound) return NotFound();
        if (!result.IsSuccess)
        {
            Errors = result.FieldErrors;
            await ReloadAsync(id, ct, keepHoursFromForm: true);
            return Page();
        }

        var affected = result.Value!.AffectedBookings;
        Success = affected > 0 ? string.Format(Text.HoursSavedAffected, affected) : Text.HoursSaved;
        return Redirect($"/Admin/Staff/Edit/{id}#hours");
    }

    private async Task ReloadAsync(Guid? id, CancellationToken ct, bool keepHoursFromForm = false)
    {
        if (id is null)
        {
            AllServices = await catalog.ListServicesAsync(BusinessId, ct);
            FillHours([]);
            return;
        }
        var view = await catalog.GetStaffAsync(BusinessId, id.Value, ct);
        if (view is null) return;
        var submitted = Input;
        ApplyView(view);
        if (!keepHoursFromForm) Input = submitted; // keep what the person typed in the details form
        else HourFields = ReadHourFieldsFromForm();
    }

    private void ApplyView(StaffEditView view)
    {
        StaffId = view.Id;
        TimeZone = view.TimeZone;
        AllServices = view.AllServices;
        Input = new StaffForm { Name = view.Name, IsActive = view.IsActive, SortOrder = view.SortOrder, ServiceIds = view.ServiceIds.ToList() };
        FillHours(view.Hours);
    }

    private void FillHours(IReadOnlyList<HourWindow> hours)
    {
        HourFields = [];
        foreach (var day in DayOrder)
        {
            var windows = hours.Where(h => h.DayOfWeek == day).OrderBy(h => h.Open).ToList();
            for (var n = 0; n < WindowsPerDay; n++)
                HourFields[(day, n)] = n < windows.Count
                    ? (windows[n].Open.ToString("HH:mm", CultureInfo.InvariantCulture), windows[n].Close.ToString("HH:mm", CultureInfo.InvariantCulture))
                    : ("", "");
        }
    }

    private Dictionary<(short Day, int Index), (string Open, string Close)> ReadHourFieldsFromForm()
    {
        var fields = new Dictionary<(short, int), (string, string)>();
        foreach (var day in DayOrder)
            for (var n = 0; n < WindowsPerDay; n++)
                fields[(day, n)] = (Request.Form[$"open_{day}_{n}"].ToString(), Request.Form[$"close_{day}_{n}"].ToString());
        return fields;
    }

    public class StaffForm
    {
        public string? Name { get; set; }
        public int SortOrder { get; set; }
        public bool IsActive { get; set; } = true;
        public List<Guid> ServiceIds { get; set; } = [];
    }
}
