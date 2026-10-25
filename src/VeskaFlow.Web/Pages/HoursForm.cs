using System.Globalization;
using VeskaFlow.Web.Services;

namespace VeskaFlow.Web.Pages;

/// <summary>One day as the hours form shows it: opens, optional break (from/until), closes. A break is stored as two windows.</summary>
public sealed record DayFields(string Open, string BreakFrom, string BreakTo, string Close);

/// <summary>Reads and fills the weekly hours form shared by the Staff page and the Settings page.</summary>
public static class HoursForm
{
    /// <summary>Monday first; the database uses 0 = Sunday.</summary>
    public static readonly short[] DayOrder = [1, 2, 3, 4, 5, 6, 0];

    public static string DayName(short day) => CultureInfo.InvariantCulture.DateTimeFormat.GetDayName((DayOfWeek)day);

    public sealed record ReadResult(List<HourWindow> Windows, bool Incomplete, bool BadBreak);

    private static bool Time(string s, out TimeOnly t) =>
        TimeOnly.TryParseExact(s, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out t);

    public static DayFields ReadDay(IFormCollection form, short day) => new(
        TimeFields.Read(form, $"open_{day}"), TimeFields.Read(form, $"breakfrom_{day}"),
        TimeFields.Read(form, $"breakto_{day}"), TimeFields.Read(form, $"close_{day}"));

    public static Dictionary<short, DayFields> ReadFields(IFormCollection form) => DayOrder.ToDictionary(d => d, d => ReadDay(form, d));

    /// <summary>The field-name suffix of the single range form used by Settings (one set of hours for every day).</summary>
    public const string AllDays = "all";

    public static DayFields ReadRange(IFormCollection form) => new(
        TimeFields.Read(form, "open_" + AllDays), TimeFields.Read(form, "breakfrom_" + AllDays),
        TimeFields.Read(form, "breakto_" + AllDays), TimeFields.Read(form, "close_" + AllDays));

    /// <summary>One range (opens, closes, optional break) applied to all seven days. Days off are set as closures.</summary>
    public static ReadResult ReadSingleRange(IFormCollection form)
    {
        var f = ReadRange(form);
        if (f.Open.Length == 0 && f.Close.Length == 0)
            return new ReadResult([], Incomplete: true, BadBreak: false); // the shop needs opening hours
        return Build(f, DayOrder);
    }

    public static ReadResult Read(IFormCollection form)
    {
        var windows = new List<HourWindow>();
        var incomplete = false;
        var badBreak = false;

        foreach (var day in DayOrder)
        {
            var f = ReadDay(form, day);
            if (f.Open.Length == 0 && f.Close.Length == 0 && f.BreakFrom.Length == 0 && f.BreakTo.Length == 0) continue;
            var one = Build(f, [day]);
            windows.AddRange(one.Windows);
            incomplete |= one.Incomplete;
            badBreak |= one.BadBreak;
        }
        return new ReadResult(windows, incomplete, badBreak);
    }

    private static ReadResult Build(DayFields f, IEnumerable<short> days)
    {
        var windows = new List<HourWindow>();
        if (!Time(f.Open, out var o) || !Time(f.Close, out var c)) return new ReadResult(windows, true, false);

        if (f.BreakFrom.Length == 0 && f.BreakTo.Length == 0)
        {
            windows.AddRange(days.Select(d => new HourWindow(d, o, c)));
            return new ReadResult(windows, false, false);
        }

        // A break needs both times and must sit inside the opening hours of that day.
        if (!Time(f.BreakFrom, out var bf) || !Time(f.BreakTo, out var bt)) return new ReadResult(windows, true, false);
        if (!(o < bf && bf < bt && bt < c)) return new ReadResult(windows, false, true);
        foreach (var d in days)
        {
            windows.Add(new HourWindow(d, o, bf));
            windows.Add(new HourWindow(d, bt, c));
        }
        return new ReadResult(windows, false, false);
    }

    /// <summary>The range to show for the shop: the first day (Monday first) that has hours, else empty fields.</summary>
    public static DayFields FillRange(IReadOnlyList<HourWindow> hours)
    {
        var all = Fill(hours);
        return DayOrder.Select(d => all[d]).FirstOrDefault(f => f.Open.Length > 0) ?? new DayFields("", "", "", "");
    }

    public static Dictionary<short, DayFields> Fill(IReadOnlyList<HourWindow> hours)
    {
        static string T(TimeOnly t) => t.ToString("HH:mm", CultureInfo.InvariantCulture);
        var result = new Dictionary<short, DayFields>();
        foreach (var day in DayOrder)
        {
            var windows = hours.Where(h => h.DayOfWeek == day).OrderBy(h => h.Open).ToList();
            result[day] = windows.Count switch
            {
                0 => new DayFields("", "", "", ""),
                1 => new DayFields(T(windows[0].Open), "", "", T(windows[0].Close)),
                // Two windows with a gap between them are shown as one day with a break.
                _ when windows[0].Close < windows[1].Open =>
                    new DayFields(T(windows[0].Open), T(windows[0].Close), T(windows[1].Open), T(windows[^1].Close)),
                _ => new DayFields(T(windows[0].Open), "", "", T(windows[^1].Close)),
            };
        }
        return result;
    }
}
