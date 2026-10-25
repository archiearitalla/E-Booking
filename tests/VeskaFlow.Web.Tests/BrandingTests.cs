using System.Net;
using VeskaFlow.Web;
using static VeskaFlow.Web.Tests.HttpApp;

namespace VeskaFlow.Web.Tests;

/// <summary>The product is called VeskaFlow; the old name must not show up on any page a visitor can open.</summary>
[Collection("http")]
public class BrandingTests
{
    [Fact]
    public async Task No_public_page_shows_the_old_product_name_and_the_footer_uses_the_new_one()
    {
        var (factory, client) = await StartAsync();
        using var _f = factory;

        Assert.Equal("VeskaFlow", AppInfo.Name);
        foreach (var path in new[] { "/", "/Login", "/Register", "/Status/404", "/no-such-page" })
        {
            var html = WebUtility.HtmlDecode(await (await client.GetAsync(path)).Content.ReadAsStringAsync()); // 404 pages too
            Assert.DoesNotContain("barberbook", html, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("VeskaFlow", html);
        }
    }
}
