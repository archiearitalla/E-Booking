using System.Text;

namespace VeskaFlow.Web.Services;

/// <summary>A vCard (.vcf) so staff can save a customer to the contacts of their phone with one tap.</summary>
public static class ContactFile
{
    public static string Vcf(string name, string phone, string? email, string shop)
    {
        static string Escape(string s) => s.Replace("\\", "\\\\").Replace(";", "\\;").Replace(",", "\\,").Replace("\r", "").Replace("\n", "\\n");

        var sb = new StringBuilder();
        sb.Append("BEGIN:VCARD\r\nVERSION:3.0\r\n");
        sb.Append($"FN:{Escape(name)}\r\n");
        sb.Append($"N:{Escape(name)};;;;\r\n");
        if (!DisplayFormat.IsPlaceholderPhone(phone)) sb.Append($"TEL;TYPE=CELL:{Escape(phone)}\r\n");
        if (!string.IsNullOrWhiteSpace(email)) sb.Append($"EMAIL;TYPE=INTERNET:{Escape(email)}\r\n");
        sb.Append($"NOTE:{Escape($"Customer of {shop}")}\r\n");
        sb.Append("END:VCARD\r\n");
        return sb.ToString();
    }

    /// <summary>A file name that is safe on every device: letters, digits and dashes only.</summary>
    public static string FileName(string name)
    {
        var safe = new string(name.Select(c => char.IsLetterOrDigit(c) ? c : '-').ToArray()).Trim('-');
        return (safe.Length == 0 ? "customer" : safe) + ".vcf";
    }
}
