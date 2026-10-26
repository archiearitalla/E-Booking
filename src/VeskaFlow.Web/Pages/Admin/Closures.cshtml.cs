using System.Globalization;
using VeskaFlow.Web.Services;
using Microsoft.AspNetCore.Mvc;

namespace VeskaFlow.Web.Pages.Admin;

/// <summary>One-off closures (dates) and weekly closures (every Sunday and so on), together on one page.</summary>
[AdminAccess(AdminLevel.Manager)]
public class ClosuresModel(ShopSettingsService settings, CatalogAdminService catalog) : AdminPageModel
{
    public const string OnDates = "dates";
    public const string EveryWeek = "weekly";

    [BindProperty] public ClosureForm Input { get; set; } = new();

    public IReadOnlyList<ClosureRow> Rows { get; private set; } = [];
    public IReadOnlyList<WeeklyClosureRow> WeeklyRows { get; private set; } = [];
    public IReadOnlyList<StaffRow> Staff { get; private set; } = [];

    /// <summary>Monday first, because that is how a week reads for a shop owner.</summary>
    public static readonly (int Value, string Label)[] Weekdays =
        [(1, "Monday"), (2, "Tuesday"), (3, "Wednesday"), (4, "Thursday"), (5, "Friday"), (6, "Saturday"), (0, "Sunday")];

    public const string AllDays = "all";

    public static string DayName(short? day) => day is null ? "day" : Weekdays.First(w => w.Value == day).Label;

    /// <summary>"12:00 PM – end of day", "start of day – 10:00 AM" or "12:00 PM – 1:00 PM".</summary>
    public static string TimeText(TimeOnly? from, TimeOnly? to)
    {
        if (from is not { } f || to is not { } t) return "";
        var start = f == TimeOnly.MinValue ? Text.ClosureStartOfDay : f.ToString("h:mm tt", CultureInfo.InvariantCulture);
        var end = t >= ShopSettingsService.EndOfDay ? Text.ClosureEndOfDay : t.ToString("h:mm tt", CultureInfo.InvariantCulture);
        return $"{start} – {end}";
    }

    private static bool TryTime(string? text, out TimeOnly? value)
    {
        value = null;
        if (string.IsNullOrWhiteSpace(text)) return true;
        if (!TimeOnly.TryParseExact(text.Trim(), "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var t)) return false;
        value = t;
        return true;
    }

    public async Task<IActionResult> OnGetAsync(CancellationToken ct)
    {
        await LoadAsync(ct);
        return Page();
    }

    public async Task<IActionResult> OnPostAddAsync(CancellationToken ct)
    {
        // Times come from the AM/PM dropdowns; keep what was picked so the form is not emptied on an error.
        Input.StartTime = TimeFields.Read(Request.Form, "StartTime");
        Input.EndTime = TimeFields.Read(Request.Form, "EndTime");
        if (!TryTime(Input.StartTime, out var startTime) || !TryTime(Input.EndTime, out var endTime))
        {
            Errors = new Dictionary<string, string> { ["startTime"] = Text.ClosureTimeInvalid };
            await LoadAsync(ct);
            return Page();
        }

        if (Input.Repeat == EveryWeek)
        {
            int? weekday = null;
            if (Input.Weekday != AllDays)
            {
                if (!int.TryParse(Input.Weekday, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed))
                {
                    Errors = new Dictionary<string, string> { ["weekday"] = Text.ClosureWeekdayInvalid };
                    await LoadAsync(ct);
                    return Page();
                }
                weekday = parsed;
            }

            var weekly = await settings.AddWeeklyClosureAsync(BusinessId, new WeeklyClosureInput(Input.StaffId, weekday, Input.Reason, startTime, endTime), ct);
            if (weekly.Failure == FailureKind.NotFound) return NotFound();
            if (!weekly.IsSuccess)
            {
                Errors = weekly.FieldErrors;
                await LoadAsync(ct);
                return Page();
            }

            var day = DayName((short?)weekday);
            var count = weekly.Value!.AffectedBookings;
            Success = count > 0 ? string.Format(Text.ClosureWeeklySavedAffected, day, count) : string.Format(Text.ClosureWeeklySaved, day);
            return RedirectToPage();
        }

        if (!DateOnly.TryParseExact(Input.FromDate, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var from)
            || !DateOnly.TryParseExact(Input.ToDate, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var to))
        {
            Errors = new Dictionary<string, string> { ["toDate"] = Text.ClosureDatesRequired };
            await LoadAsync(ct);
            return Page();
        }

        // The staff id is checked against the signed-in business inside the service; a foreign id is "not found".
        var result = await settings.AddClosureAsync(BusinessId, new ClosureInput(Input.StaffId, from, to, Input.Reason, startTime, endTime), ct);
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

    public async Task<IActionResult> OnPostToggleWeeklyAsync(Guid id, bool on, CancellationToken ct)
    {
        var result = await settings.SetWeeklyClosureActiveAsync(BusinessId, id, on, ct);
        if (!result.IsSuccess) return NotFound();
        Success = on ? Text.ClosureWeeklyTurnedOn : Text.ClosureWeeklyTurnedOff;
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostDeleteWeeklyAsync(Guid id, CancellationToken ct)
    {
        var result = await settings.DeleteWeeklyClosureAsync(BusinessId, id, ct);
        if (!result.IsSuccess) return NotFound();
        Success = Text.ClosureRemoved;
        return RedirectToPage();
    }

    private async Task LoadAsync(CancellationToken ct)
    {
        Rows = await settings.ListClosuresAsync(BusinessId, ct);
        WeeklyRows = await settings.ListWeeklyClosuresAsync(BusinessId, ct);
        Staff = (await catalog.ListStaffAsync(BusinessId, ct)).Where(s => s.IsActive).ToList();
    }

    public class ClosureForm
    {
        public string? Repeat { get; set; } = OnDates;
        public Guid? StaffId { get; set; }
        public string? FromDate { get; set; }
        public string? ToDate { get; set; }
        public string? Weekday { get; set; }
        public string? StartTime { get; set; }
        public string? EndTime { get; set; }
        public string? Reason { get; set; }
    }
}
