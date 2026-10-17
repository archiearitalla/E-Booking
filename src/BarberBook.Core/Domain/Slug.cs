using System.Text;
using System.Text.RegularExpressions;

namespace BarberBook.Core.Domain;

public static partial class Slug
{
    [GeneratedRegex("^[a-z0-9]+(-[a-z0-9]+)*$")]
    private static partial Regex Valid();

    public static bool IsValid(string? slug) =>
        slug is { Length: >= 3 and <= 40 } && Valid().IsMatch(slug);

    /// <summary>"Maria's Salon & Barber!" -> "marias-salon-barber"</summary>
    public static string From(string name)
    {
        var sb = new StringBuilder();
        foreach (var ch in name.ToLowerInvariant().Replace("'", ""))
        {
            if (ch is >= 'a' and <= 'z' or >= '0' and <= '9') sb.Append(ch);
            else if (sb.Length > 0 && sb[^1] != '-') sb.Append('-');
        }
        var slug = sb.ToString().Trim('-');
        if (slug.Length > 40) slug = slug[..40].Trim('-');
        return slug;
    }
}
