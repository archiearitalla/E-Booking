using System.Net;
using VeskaFlow.Web.Services;

namespace VeskaFlow.Web.Tests;

/// <summary>The shop logo: only real pictures, size limit, public serving with a validator, removal, isolation.</summary>
[Collection("http")]
public class LogoTests
{
    private static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 0];

    [Fact]
    public void The_type_comes_from_the_file_itself()
    {
        Assert.Equal("image/png", LogoService.Sniff(Png));
        Assert.Equal("image/jpeg", LogoService.Sniff([0xFF, 0xD8, 0xFF, 0xE0]));
        Assert.Equal("image/webp", LogoService.Sniff("RIFF\0\0\0\0WEBPVP8 "u8.ToArray()));
        Assert.Null(LogoService.Sniff("<svg xmlns='http://www.w3.org/2000/svg'><script>alert(1)</script></svg>"u8.ToArray()));
        Assert.Null(LogoService.Sniff("<html></html>"u8.ToArray()));
    }

    [Fact]
    public async Task A_logo_is_saved_served_replaced_and_removed_for_its_own_shop_only()
    {
        var db = new TestDbFactory(await TestDb.ConnectionStringAsync());
        var onboarding = new OnboardingService(db, TimeProvider.System);
        var slug = "lg" + Guid.NewGuid().ToString("N")[..10];
        var shop = (await onboarding.RegisterAsync(new RegisterRequest("Logo Shop", slug, "Owner", $"o{Guid.NewGuid():N}@example.com", "correct-horse-1"))).Value!;
        var other = (await onboarding.RegisterAsync(new RegisterRequest("Other", "lo" + Guid.NewGuid().ToString("N")[..10], "Owner", $"o{Guid.NewGuid():N}@example.com", "correct-horse-1"))).Value!;
        var logos = new LogoService(db, TimeProvider.System);

        Assert.Null(await logos.VersionAsync(shop.BusinessId));
        Assert.Contains("logo", (await logos.SaveAsync(shop.BusinessId, null)).FieldErrors.Keys);
        Assert.Contains("logo", (await logos.SaveAsync(shop.BusinessId, "not a picture"u8.ToArray())).FieldErrors.Keys);
        Assert.Contains("logo", (await logos.SaveAsync(shop.BusinessId, Png.Concat(new byte[LogoService.MaxBytes]).ToArray())).FieldErrors.Keys);

        Assert.True((await logos.SaveAsync(shop.BusinessId, Png)).IsSuccess);
        var served = await logos.GetBySlugAsync(slug.ToUpperInvariant());
        Assert.Equal("image/png", served!.ContentType);
        Assert.Equal(Png, served.Data);
        Assert.Null(await logos.VersionAsync(other.BusinessId));

        var jpeg = new byte[] { 0xFF, 0xD8, 0xFF, 0xE0, 1, 2 };
        Assert.True((await logos.SaveAsync(shop.BusinessId, jpeg)).IsSuccess); // replaces
        Assert.Equal("image/jpeg", (await logos.GetBySlugAsync(slug))!.ContentType);

        await logos.RemoveAsync(other.BusinessId); // another shop's remove does nothing here
        Assert.NotNull(await logos.GetBySlugAsync(slug));
        await logos.RemoveAsync(shop.BusinessId);
        Assert.Null(await logos.GetBySlugAsync(slug));
    }

    [Fact]
    public async Task The_logo_address_is_public_cached_by_validator_and_shown_on_the_booking_page()
    {
        var (factory, _) = await HttpApp.StartAsync();
        using var _f = factory;
        var db = new TestDbFactory(await TestDb.ConnectionStringAsync());
        var slug = "lh" + Guid.NewGuid().ToString("N")[..10];
        var shop = (await new OnboardingService(db, TimeProvider.System).RegisterAsync(
            new RegisterRequest("Logo Page", slug, "Owner", $"o{Guid.NewGuid():N}@example.com", "correct-horse-1"))).Value!;
        var anonymous = HttpApp.NewClient(factory);

        Assert.Equal(HttpStatusCode.NotFound, (await anonymous.GetAsync($"/logo/{slug}")).StatusCode);
        Assert.DoesNotContain("class=\"shop-logo\"", await anonymous.GetStringAsync($"/b/{slug}"));

        await new LogoService(db, TimeProvider.System).SaveAsync(shop.BusinessId, Png);
        var response = await anonymous.GetAsync($"/logo/{slug}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("image/png", response.Content.Headers.ContentType!.MediaType);
        var etag = response.Headers.ETag!.ToString();

        var again = new HttpRequestMessage(HttpMethod.Get, $"/logo/{slug}");
        again.Headers.TryAddWithoutValidation("If-None-Match", etag);
        Assert.Equal(HttpStatusCode.NotModified, (await anonymous.SendAsync(again)).StatusCode);
        Assert.Contains("class=\"shop-logo\"", await anonymous.GetStringAsync($"/b/{slug}"));
    }
}
