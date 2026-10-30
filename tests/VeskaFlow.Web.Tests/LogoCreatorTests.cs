using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;
using VeskaFlow.Web;
using VeskaFlow.Web.Services;

namespace VeskaFlow.Web.Tests;

/// <summary>The Logo Creator on the Settings page: its closed lists, its default letters, readable colours, and the page that carries it.</summary>
[Collection("http")]
public class LogoCreatorTests
{
    [Theory]
    [InlineData("Kuya Jun Salon", "KJ")]
    [InlineData("Salon", "SA")]
    [InlineData("A&B Cuts", "AB")]
    [InlineData("  ", "VF")]
    [InlineData(null, "VF")]
    [InlineData("24 7 Gym", "27")]
    [InlineData("the-barber.shop", "TB")]
    [InlineData("Ñandú Café", "ÑC")]
    public void Default_letters_are_the_initials_of_the_shop_name(string? name, string expected) =>
        Assert.Equal(expected, LogoStyles.Initials(name));

    [Fact]
    public void Initials_are_never_longer_than_the_creator_allows()
    {
        Assert.InRange(LogoStyles.Initials(new string('x', 200)).Length, 1, LogoStyles.MaxText);
        Assert.InRange(LogoStyles.Initials("Verylongword Another").Length, 1, LogoStyles.MaxText);
    }

    [Fact]
    public void Every_colour_pair_is_readable_and_every_choice_has_a_name_and_a_unique_key()
    {
        foreach (var (key, label, background, foreground) in LogoStyles.Colors)
        {
            Assert.False(string.IsNullOrWhiteSpace(label), key);
            Assert.True(Contrast(background, foreground) >= 4.5, $"{key}: {Contrast(background, foreground):0.0}");              // letters stay readable on every background
        }
        Assert.Equal(LogoStyles.Colors.Count, LogoStyles.Colors.Select(c => c.Key).Distinct().Count());
        Assert.Equal(LogoStyles.Icons.Count, LogoStyles.Icons.Select(i => i.Key).Distinct().Count());
        Assert.All(LogoStyles.Icons, i => Assert.False(string.IsNullOrWhiteSpace(i.Label)));
        Assert.All(LogoStyles.Icons.Where(i => i.Key != "none"), i => Assert.StartsWith("<", i.Shapes));                          // only drawing shapes, nothing else
        Assert.All(LogoStyles.Icons, i => Assert.DoesNotContain("script", i.Shapes, StringComparison.OrdinalIgnoreCase));
        Assert.Equal(["circle", "rounded", "square"], LogoStyles.Shapes.Select(s => s.Key));
        Assert.Equal(["sans", "serif"], LogoStyles.Fonts.Select(f => f.Key));
        Assert.Contains(LogoStyles.Colors, c => c.Key == BrandColors.Default);                                                  // the shop's own colour can be the default
        Assert.All(BrandColors.All, c => Assert.Contains(LogoStyles.Colors, l => l.Key == c.Key));
    }

    [Fact]
    public async Task The_settings_page_carries_the_creator_with_the_shops_letters_and_colour_and_the_style_sheet_has_every_swatch()
    {
        var (factory, _) = await HttpApp.StartAsync();
        using var _f = factory;
        var owner = HttpApp.NewClient(factory);
        var slug = HttpApp.Unique("lc");
        Assert.Equal(HttpStatusCode.Redirect, (await HttpApp.PostFormAsync(owner, "/Register", new()
        {
            ["Input.Accept"] = "true", ["Input.BusinessName"] = "Kuya Jun Salon", ["Input.Slug"] = slug, ["Input.OwnerName"] = "Jun Reyes",
            ["Input.Email"] = $"{HttpApp.Unique("o")}@example.com", ["Input.Password"] = "correct-horse-1",
        })).StatusCode);

        var page = WebUtility.HtmlDecode(await owner.GetStringAsync("/Admin/Settings"));
        Assert.Contains("id=\"logo-creator\"", page);
        Assert.Contains("data-default-text=\"KJ\"", page);                                                                       // the shop's initials
        Assert.Contains("/js/logo-creator.js", page);
        Assert.Contains("id=\"logo-form\"", page);                                                                              // it sends through the ordinary logo upload
        Assert.Matches("<section id=\"logo-creator\"[^>]* hidden", page);                                                       // hidden until its script runs
        foreach (var (key, _, bg, fg) in LogoStyles.Colors) Assert.Contains($"name=\"lc-color\" value=\"{key}\" data-bg=\"{bg}\" data-fg=\"{fg}\"", page);
        foreach (var (key, _, _) in LogoStyles.Icons) Assert.Contains($"name=\"lc-icon\" value=\"{key}\"", page);
        foreach (var (key, _) in LogoStyles.Shapes) Assert.Contains($"name=\"lc-shape\" value=\"{key}\"", page);
        foreach (var (key, _) in LogoStyles.Fonts) Assert.Contains($"name=\"lc-font\" value=\"{key}\"", page);
        Assert.Matches("name=\"lc-color\" value=\"teal\"[^>]* checked", page);                                                  // a new shop's colour is teal

        var css = await owner.GetStringAsync("/css/site.css");
        foreach (var (key, _, bg, fg) in LogoStyles.Colors) Assert.Contains($".lc-c-{key} {{ --lc-bg: {bg}; --lc-fg: {fg}; }}", css);   // the swatch matches the colour the script draws

        var script = await owner.GetStringAsync("/js/logo-creator.js");
        Assert.DoesNotContain("innerHTML =", script);                                                                           // the script never writes markup from typed text
        Assert.Contains("image/png", script);
    }

    // ---------------------------------------------------------------- colours

    private static double Luminance(string hex)
    {
        double Channel(int start)
        {
            var c = int.Parse(hex.AsSpan(start, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture) / 255.0;
            return c <= 0.03928 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
        }
        return 0.2126 * Channel(1) + 0.7152 * Channel(3) + 0.0722 * Channel(5);
    }

    private static double Contrast(string a, string b)
    {
        double x = Luminance(a), y = Luminance(b);
        return (Math.Max(x, y) + 0.05) / (Math.Min(x, y) + 0.05);
    }
}
