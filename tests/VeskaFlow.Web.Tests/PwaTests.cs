using System.Net;
using System.Text.Json;

namespace VeskaFlow.Web.Tests;

/// <summary>Installing the back office on a phone (manifest, icons, tiny service worker) and the day's KPI cards.</summary>
[Collection("http")]
public class PwaTests
{
    private static string Id(string prefix) => prefix + Guid.NewGuid().ToString("N")[..10];

    [Fact]
    public async Task The_app_can_be_installed_and_the_bookings_page_shows_the_days_numbers()
    {
        var (factory, client) = await HttpApp.StartAsync();
        using var _f = factory;
        var slug = Id("pw");
        await HttpApp.PostFormAsync(client, "/Register", new()
        {
            ["Input.Accept"] = "true", ["Input.BusinessName"] = "Pwa Shop", ["Input.Slug"] = slug, ["Input.OwnerName"] = "Owner",
            ["Input.Email"] = $"{Id("o")}@example.com", ["Input.Password"] = "correct-horse-1", ["Input.Industry"] = "clinic",
        });
        var anonymous = HttpApp.NewClient(factory);

        var manifest = await anonymous.GetAsync("/manifest.webmanifest");
        Assert.Equal(HttpStatusCode.OK, manifest.StatusCode);
        using var doc = JsonDocument.Parse(await manifest.Content.ReadAsStringAsync());
        Assert.Equal("standalone", doc.RootElement.GetProperty("display").GetString());
        Assert.Equal("/Admin", doc.RootElement.GetProperty("start_url").GetString());
        var icons = doc.RootElement.GetProperty("icons").EnumerateArray().ToList();
        Assert.Contains(icons, i => i.GetProperty("sizes").GetString() == "192x192");
        Assert.Contains(icons, i => i.GetProperty("purpose").GetString() == "maskable");
        foreach (var icon in icons)
        {
            var response = await anonymous.GetAsync(icon.GetProperty("src").GetString());
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal("image/png", response.Content.Headers.ContentType!.MediaType);
        }
        Assert.Equal(HttpStatusCode.OK, (await anonymous.GetAsync("/sw.js")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await anonymous.GetAsync("/icons/apple-touch-icon.png")).StatusCode);

        // Only the back office offers the install; the customers' booking page does not.
        var admin = await client.GetStringAsync("/Admin");
        Assert.Contains("rel=\"manifest\"", admin);
        Assert.Contains("class=\"kpis\"", admin);
        Assert.Contains(Text.KpiToDo, admin);
        Assert.DoesNotContain("rel=\"manifest\"", await anonymous.GetStringAsync($"/b/{slug}"));
        Assert.Contains(Text.InstallTitle, await client.GetStringAsync("/Admin/About"));
    }
}
