namespace VeskaFlow.Core.Domain;

/// <summary>
/// One canonical form per phone number, so the same person is recognised however the number was typed
/// (BUSINESS_RULES 8.2: customers are matched by phone). Philippine mobile numbers are the common case:
/// 0917 123 4567, +63 917 123 4567, 63917 123 4567 and 917 123 4567 are all 09171234567.
/// Any other number keeps its digits (and a leading +) unchanged.
/// </summary>
public static class PhoneNumber
{
    public static string Normalize(string? raw)
    {
        var s = new string((raw ?? "").Where(c => char.IsDigit(c) || c == '+').ToArray());
        if (s.Length == 0) return "";

        var plus = s[0] == '+';
        var digits = new string(s.Where(char.IsDigit).ToArray());

        if (plus && digits.StartsWith("63", StringComparison.Ordinal) && digits.Length == 12) return "0" + digits[2..];
        if (!plus && digits.StartsWith("63", StringComparison.Ordinal) && digits.Length == 12) return "0" + digits[2..];
        if (!plus && digits.Length == 10 && digits[0] == '9') return "0" + digits;
        return plus ? "+" + digits : digits;
    }
}
