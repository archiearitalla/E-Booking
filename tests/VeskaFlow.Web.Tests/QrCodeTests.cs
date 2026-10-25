using System.Net;
using VeskaFlow.Web.Services;
using static VeskaFlow.Web.Tests.HttpApp;

namespace VeskaFlow.Web.Tests;

public class QrCodeServiceTests
{
    [Fact]
    public void The_booking_url_uses_the_configured_public_address_when_there_is_one_and_the_request_host_otherwise()
    {
        Assert.Equal("https://flow.veskacode.com/b/smile", QrCodes.BookingUrl("https://flow.veskacode.com/", "http", "localhost:5100", "smile"));
        Assert.Equal("https://flow.veskacode.com/b/smile", QrCodes.BookingUrl(" https://flow.veskacode.com ", "http", "localhost:5100", "smile"));
        Assert.Equal("http://localhost:5100/b/smile", QrCodes.BookingUrl(null, "http", "localhost:5100", "smile"));
        Assert.Equal("https://example.com/b/smile", QrCodes.BookingUrl("", "https", "example.com", "smile"));
    }

    [Fact]
    public void The_qr_code_is_a_scalable_svg_that_is_the_same_for_the_same_address_and_different_for_another()
    {
        var a = QrCodes.Svg("https://flow.veskacode.com/b/smile");
        var again = QrCodes.Svg("https://flow.veskacode.com/b/smile");
        var other = QrCodes.Svg("https://flow.veskacode.com/b/other-shop");

        Assert.StartsWith("<svg", a.TrimStart());
        Assert.Contains("viewBox", a);
        Assert.DoesNotContain("width=\"", a.Split('>')[0]); // scales with its container
        Assert.Equal(a, again);
        Assert.NotEqual(a, other);
        Assert.DoesNotContain("<script", a, StringComparison.OrdinalIgnoreCase);
    }
}

[Collection("http")]
public class QrCodeWebTests
{
    [Fact]
    public async Task The_owner_sees_and_downloads_a_qr_code_for_their_own_booking_page_and_strangers_cannot()
    {
        var (factory, _) = await StartAsync();
        using var _f = factory;
        var client = NewClient(factory);
        var slug = Unique("qr");
        var reg = await PostFormAsync(client, "/Register", new()
        {
            ["Input.BusinessName"] = "QR Clinic", ["Input.Slug"] = slug, ["Input.OwnerName"] = "Dr Reyes",
            ["Input.Email"] = $"{Unique("o")}@example.com", ["Input.Password"] = "correct-horse-1", ["Input.Industry"] = "clinic",
        });
        Assert.Equal(HttpStatusCode.Redirect, reg.StatusCode);

        var page = WebUtility.HtmlDecode(await client.GetStringAsync("/Admin/Settings"));
        Assert.Contains(Text.QrTitle, page);
        Assert.Contains($"/b/{slug}", page);
        Assert.Contains("<svg", page);

        var download = await client.GetAsync("/Admin/Settings?handler=Qr");
        Assert.Equal(HttpStatusCode.OK, download.StatusCode);
        Assert.Equal("image/svg+xml", download.Content.Headers.ContentType!.MediaType);
        Assert.Contains($"{slug}-booking-qr.svg", download.Content.Headers.ContentDisposition!.ToString());
        Assert.StartsWith("<svg", (await download.Content.ReadAsStringAsync()).TrimStart());

        var stranger = await NewClient(factory).GetAsync("/Admin/Settings?handler=Qr");
        Assert.Equal(HttpStatusCode.Redirect, stranger.StatusCode);
        Assert.Contains("/Login", stranger.Headers.Location!.OriginalString);
    }
}
