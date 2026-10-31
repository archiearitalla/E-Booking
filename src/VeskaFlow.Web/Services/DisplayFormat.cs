using System.Globalization;
using VeskaFlow.Core.Scheduling;

namespace VeskaFlow.Web.Services;

/// <summary>Presentation helpers only: how an instant, a price or a duration is shown. No rules live here.</summary>
public static class DisplayFormat
{
    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

    public static string Time(DateTime utc, string timeZone) =>
        LocalTime.ToLocal(utc, timeZone).ToString("h:mm tt", Invariant);

    public static string Date(DateTime utc, string timeZone) =>
        LocalTime.ToLocal(utc, timeZone).ToString("dddd, MMMM d, yyyy", Invariant);

    public static string Price(decimal price) => "₱" + price.ToString("N2", Invariant);

    public static string Duration(int minutes) =>
        minutes % 60 == 0 ? $"{minutes / 60} h" : minutes > 60 ? $"{minutes / 60} h {minutes % 60} min" : $"{minutes} min";

    /// <summary>An instant as it travels in a query string, always UTC with a trailing Z.</summary>
    public static string StartParam(DateTime utc) =>
        LocalTime.AsUtc(utc).ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", Invariant);

    public static bool TryParseStart(string? value, out DateTime utc)
    {
        utc = default;
        if (string.IsNullOrWhiteSpace(value) || !value.EndsWith('Z')) return false;
        return DateTime.TryParse(value, Invariant, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out utc);
    }

    /// <summary>A walk-in recorded without a phone number is stored with a generated placeholder; it is never shown or dialled.</summary>
    public const string WalkInPhonePrefix = "walkin-";

    /// <summary>
    /// The number as digits with the country code and no plus sign, as WhatsApp and Viber want it (09171234567 becomes 639171234567).
    /// Null for a walk-in placeholder or a number that does not look like a mobile number.
    /// </summary>
    public static string? InternationalDigits(string? phone)
    {
        if (string.IsNullOrWhiteSpace(phone) || IsPlaceholderPhone(phone)) return null;
        var digits = new string(phone.Where(char.IsAsciiDigit).ToArray());
        if (phone.TrimStart().StartsWith('+')) return digits.Length is >= 8 and <= 15 ? digits : null;
        if (digits.Length == 11 && digits[0] == '0') return "63" + digits[1..];          // Philippine mobile number written 09...
        if (digits.Length == 12 && digits.StartsWith("63", StringComparison.Ordinal)) return digits;
        return null;
    }

    public static bool IsPlaceholderPhone(string? phone) => phone is not null && phone.StartsWith(WalkInPhonePrefix, StringComparison.Ordinal);

    public static string DateParam(DateOnly date) => date.ToString("yyyy-MM-dd", Invariant);
}
