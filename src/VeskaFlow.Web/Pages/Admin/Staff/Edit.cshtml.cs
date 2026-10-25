using System.Globalization;
using VeskaFlow.Web.Services;
using Microsoft.AspNetCore.Mvc;

namespace VeskaFlow.Web.Pages.Admin.Staff;

public class EditModel(CatalogAdminService catalog) : AdminPageModel
{
    /// <summary>Monday first; the database uses 0 = Sunday.</summary>
    public static readonly short[] DayOrder = [1, 2, 3, 4, 5, 6, 0];

    public static string DayName(short day) => CultureInfo.InvariantCulture.DateTimeFormat.GetDayName((DayOfWeek)day);

    [BindProperty] public StaffForm Input { get; set; } = new();

    public bool IsNew { get; private set; }
    public bool HoursFormSubmitted { get; private set; }
    public Guid? StaffId { get; private set; }
    public string TimeZone { get; private set; } = "";
    public IReadOnlyList<ServiceRow> AllServices { get; private set; } = [];
    public Dictionary<short, DayFields> HourFields { get; private set; } = [];

    /// <summary>One day as the form shows it: opens, optional break (from/until), closes. A break is stored as two windows.</summary>
    public sealed record DayFields(string Open, string BreakFrom, string BreakTo, string Close);

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
        var breakError = false;

        static bool Time(string s, out TimeOnly t) =>
            TimeOnly.TryParseExact(s, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out t);

        foreach (var day in DayOrder)
        {
            var f = ReadDay(day);
            if (f.Open.Length == 0 && f.Close.Length == 0 && f.BreakFrom.Length == 0 && f.BreakTo.Length == 0) continue;

            if (!Time(f.Open, out var o) || !Time(f.Close, out var c)) { parseError = true; continue; }

            if (f.BreakFrom.Length == 0 && f.BreakTo.Length == 0)
            {
                windows.Add(new HourWindow(day, o, c));
                continue;
            }

            // A break needs both times and must sit inside the opening hours of that day.
            if (!Time(f.BreakFrom, out var bf) || !Time(f.BreakTo, out var bt)) { parseError = true; continue; }
            if (!(o < bf && bf < bt && bt < c)) { breakError = true; continue; }
            windows.Add(new HourWindow(day, o, bf));
            windows.Add(new HourWindow(day, bt, c));
        }

        ServiceResult<SaveOutcome> result;
        if (parseError)
            result = ServiceResult<SaveOutcome>.Invalid(new Dictionary<string, string> { ["hours"] = Text.HoursIncomplete });
        else if (breakError)
            result = ServiceResult<SaveOutcome>.Invalid(new Dictionary<string, string> { ["hours"] = Text.HoursBreakInvalid });
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
        static string T(TimeOnly t) => t.ToString("HH:mm", CultureInfo.InvariantCulture);
        HourFields = [];
        foreach (var day in DayOrder)
        {
            var windows = hours.Where(h => h.DayOfWeek == day).OrderBy(h => h.Open).ToList();
            HourFields[day] = windows.Count switch
            {
                0 => new DayFields("", "", "", ""),
                1 => new DayFields(T(windows[0].Open), "", "", T(windows[0].Close)),
                // Two windows with a gap between them are shown as one day with a break.
                _ when windows[0].Close < windows[1].Open =>
                    new DayFields(T(windows[0].Open), T(windows[0].Close), T(windows[1].Open), T(windows[^1].Close)),
                _ => new DayFields(T(windows[0].Open), "", "", T(windows[^1].Close)),
            };
        }
    }

    private DayFields ReadDay(short day) => new(
        Request.Form[$"open_{day}"].ToString().Trim(), Request.Form[$"breakfrom_{day}"].ToString().Trim(),
        Request.Form[$"breakto_{day}"].ToString().Trim(), Request.Form[$"close_{day}"].ToString().Trim());

    private Dictionary<short, DayFields> ReadHourFieldsFromForm() => DayOrder.ToDictionary(d => d, ReadDay);

    public class StaffForm
    {
        public string? Name { get; set; }
        public int SortOrder { get; set; }
        public bool IsActive { get; set; } = true;
        public List<Guid> ServiceIds { get; set; } = [];
    }
}
