using System.Globalization;

namespace VeskaFlow.Web.Pages;

/// <summary>A time field shown as hour, minute and AM/PM dropdowns. <c>Value</c> is "HH:mm" (24-hour) or empty.</summary>
public sealed record TimePickerModel(string Name, string? Value)
{
    public static readonly string[] Minutes = Enumerable.Range(0, 12).Select(i => (i * 5).ToString("00", CultureInfo.InvariantCulture)).ToArray();

    /// <summary>The 12-hour parts of <c>Value</c>, or nulls when it is empty or not a time.</summary>
    public (int Hour, int Minute, string Suffix)? Parts()
    {
        if (!TimeOnly.TryParseExact(Value ?? "", "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var t)) return null;
        var h = t.Hour % 12;
        return (h == 0 ? 12 : h, t.Minute, t.Hour < 12 ? "AM" : "PM");
    }
}

public static class TimeFields
{
    /// <summary>Marks a time that was filled in wrongly; it never parses as a time, so the page reports it as incomplete.</summary>
    public const string Invalid = "invalid";

    /// <summary>
    /// Reads a time posted by the picker as "HH:mm". Empty when the hour is left blank. A plain "name" value in
    /// "HH:mm" form is accepted too.
    /// </summary>
    public static string Read(IFormCollection form, string name)
    {
        var plain = form[name].ToString().Trim();
        if (plain.Length > 0) return plain;

        var hour = form[name + "_h"].ToString().Trim();
        if (hour.Length == 0) return "";
        var minute = form[name + "_m"].ToString().Trim();
        var suffix = form[name + "_ap"].ToString().Trim().ToUpperInvariant();
        if (!int.TryParse(hour, NumberStyles.None, CultureInfo.InvariantCulture, out var h) || h is < 1 or > 12) return Invalid;
        if (!int.TryParse(minute.Length == 0 ? "0" : minute, NumberStyles.None, CultureInfo.InvariantCulture, out var m) || m is < 0 or > 59) return Invalid;
        if (suffix is not ("AM" or "PM")) return Invalid;
        var h24 = h % 12 + (suffix == "PM" ? 12 : 0);
        return $"{h24:00}:{m:00}";
    }
}
