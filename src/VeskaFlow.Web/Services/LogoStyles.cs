namespace VeskaFlow.Web.Services;

/// <summary>
/// What the Logo Creator on the Settings page offers (BUSINESS_RULES 6.y8). A closed set of colours, shapes, icons and letter styles
/// that the page hands to its script; the picture itself is drawn in the browser and saved through the ordinary logo upload, so it is
/// checked like any other logo (PNG, up to 256 KB, recognised by its first bytes). Nothing here is typed by a person.
/// </summary>
public static class LogoStyles
{
    /// <summary>Background and letter colours. The first six share their names with the shop colours.</summary>
    public static readonly IReadOnlyList<(string Key, string Label, string Background, string Foreground)> Colors =
    [
        ("teal", Text.ColorTeal, "#0f766e", "#ffffff"), ("blue", Text.ColorBlue, "#1d4ed8", "#ffffff"), ("orange", Text.ColorOrange, "#c2410c", "#ffffff"),
        ("rose", Text.ColorRose, "#be123c", "#ffffff"), ("violet", Text.ColorViolet, "#6d28d9", "#ffffff"), ("slate", Text.ColorSlate, "#334155", "#ffffff"),
        ("green", Text.LogoColorGreen, "#166534", "#ffffff"), ("black", Text.LogoColorBlack, "#111827", "#f5c451"), ("gold", Text.LogoColorGold, "#f5c451", "#111827"),
    ];

    public static readonly IReadOnlyList<(string Key, string Label)> Shapes =
        [("circle", Text.LogoShapeCircle), ("rounded", Text.LogoShapeRounded), ("square", Text.LogoShapeSquare)];

    public static readonly IReadOnlyList<(string Key, string Label)> Fonts = [("sans", Text.LogoFontSans), ("serif", Text.LogoFontSerif)];

    /// <summary>Icons as the shapes inside a 24 x 24 outline drawing (the same style as the rest of the pages). An empty one means no icon.</summary>
    public static readonly IReadOnlyList<(string Key, string Label, string Shapes)> Icons =
    [
        ("none", Text.LogoIconNone, ""),
        ("scissors", Text.LogoIconScissors, "<circle cx=\"6\" cy=\"6\" r=\"3\"/><circle cx=\"6\" cy=\"18\" r=\"3\"/><line x1=\"20\" y1=\"4\" x2=\"8.12\" y2=\"15.88\"/><line x1=\"14.47\" y1=\"14.48\" x2=\"20\" y2=\"20\"/><line x1=\"8.12\" y1=\"8.12\" x2=\"12\" y2=\"12\"/>"),
        ("star", Text.LogoIconStar, "<polygon points=\"12 2 15.09 8.26 22 9.27 17 14.14 18.18 21.02 12 17.77 5.82 21.02 7 14.14 2 9.27 8.91 8.26 12 2\"/>"),
        ("heart", Text.LogoIconHeart, "<path d=\"M20.84 4.61a5.5 5.5 0 0 0-7.78 0L12 5.67l-1.06-1.06a5.5 5.5 0 0 0-7.78 7.78l1.06 1.06L12 21.23l7.78-7.78 1.06-1.06a5.5 5.5 0 0 0 0-7.78z\"/>"),
        ("sun", Text.LogoIconSun, "<circle cx=\"12\" cy=\"12\" r=\"5\"/><line x1=\"12\" y1=\"1\" x2=\"12\" y2=\"3\"/><line x1=\"12\" y1=\"21\" x2=\"12\" y2=\"23\"/><line x1=\"4.22\" y1=\"4.22\" x2=\"5.64\" y2=\"5.64\"/><line x1=\"18.36\" y1=\"18.36\" x2=\"19.78\" y2=\"19.78\"/><line x1=\"1\" y1=\"12\" x2=\"3\" y2=\"12\"/><line x1=\"21\" y1=\"12\" x2=\"23\" y2=\"12\"/><line x1=\"4.22\" y1=\"19.78\" x2=\"5.64\" y2=\"18.36\"/><line x1=\"18.36\" y1=\"5.64\" x2=\"19.78\" y2=\"4.22\"/>"),
        ("drop", Text.LogoIconDrop, "<path d=\"M12 2.69l5.66 5.66a8 8 0 1 1-11.31 0z\"/>"),
        ("spark", Text.LogoIconSpark, "<polygon points=\"13 2 3 14 12 14 11 22 21 10 12 10 13 2\"/>"),
        ("smile", Text.LogoIconSmile, "<circle cx=\"12\" cy=\"12\" r=\"10\"/><path d=\"M8 14s1.5 2 4 2 4-2 4-2\"/><line x1=\"9\" y1=\"9\" x2=\"9.01\" y2=\"9\"/><line x1=\"15\" y1=\"9\" x2=\"15.01\" y2=\"9\"/>"),
        ("award", Text.LogoIconAward, "<circle cx=\"12\" cy=\"8\" r=\"7\"/><polyline points=\"8.21 13.89 7 23 12 20 17 23 15.79 13.88\"/>"),
    ];

    public const int MaxText = 12;

    /// <summary>The first letters of the first two words of a shop's name, in capitals ("Kuya Jun Salon" gives "KJ"). Never longer than <see cref="MaxText"/>.</summary>
    public static string Initials(string? shopName)
    {
        var words = (shopName ?? "").Split(new[] { ' ', '-', '_', '.', ',', '&' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(w => new string(w.Where(char.IsLetterOrDigit).ToArray())).Where(w => w.Length > 0).ToList();
        if (words.Count == 0) return "VF";
        var letters = words.Count == 1 ? words[0][..Math.Min(2, words[0].Length)] : string.Concat(words.Take(2).Select(w => w[0]));
        return letters.ToUpperInvariant();
    }
}
