using System.Net;

namespace VeskaFlow.Web.Tests;

/// <summary>The bright / dark switch: a button on every page, a small script that remembers the choice, and the bright palette in the style sheet.</summary>
[Collection("http")]
public class ThemeSwitchTests
{
    [Fact]
    public async Task Every_page_has_the_switch_and_loads_the_theme_script_before_the_page_is_drawn()
    {
        var (factory, client) = await HttpApp.StartAsync();
        using var _f = factory;

        foreach (var path in new[] { "/", "/Login", "/Privacy" })
        {
            var html = WebUtility.HtmlDecode(await client.GetStringAsync(path));
            Assert.Contains("data-theme-toggle", html);
            Assert.Contains("aria-label=\"" + Text.ThemeToLight + "\"", html);
            Assert.Contains("data-to-dark=\"" + Text.ThemeToDark + "\"", html);
            var script = html.IndexOf("/js/theme.js?v=", StringComparison.Ordinal);
            Assert.True(script >= 0 && script < html.IndexOf("<body", StringComparison.Ordinal), $"theme.js must be in the head on {path}");
            Assert.DoesNotContain("<script>", html);                                                                  // no inline script (the policy forbids it)
        }
    }

    [Fact]
    public async Task The_script_remembers_the_choice_and_the_style_sheet_has_the_bright_palette_and_the_shop_accents()
    {
        var (factory, client) = await HttpApp.StartAsync();
        using var _f = factory;

        var js = await client.GetStringAsync("/js/theme.js");
        Assert.Contains("localStorage", js);
        Assert.Contains("data-mode", js);
        Assert.Contains("catch", js);                                                                                  // blocked storage must not break the page

        var css = await client.GetStringAsync("/css/site.css");
        Assert.Contains(":root[data-mode=\"light\"]", css);
        Assert.Contains("color-scheme: light", css);
        foreach (var accent in new[] { "blue", "orange", "rose", "violet", "slate" })
            Assert.Contains($"html[data-mode=\"light\"] body[data-theme=\"{accent}\"]", css);                        // every shop color has a bright version
        Assert.Contains("html[data-mode=\"light\"] button.danger { color: #ffffff; }", css);                          // red buttons stay readable
    }
}
