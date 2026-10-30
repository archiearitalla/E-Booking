using System.Net;
using System.Net.Http.Headers;
using Microsoft.EntityFrameworkCore;
using VeskaFlow.Web;
using VeskaFlow.Web.Data;
using VeskaFlow.Web.Services;

namespace VeskaFlow.Web.Tests;

/// <summary>The owner's promo banner: validation, the last day in the shop's time, isolation between shops, and the web pages.</summary>
[Collection("http")]
public class PromoBannerTests
{
    private static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 1, 2, 3, 4];
    private static readonly byte[] Jpeg = [0xFF, 0xD8, 0xFF, 0xE0, 9, 9, 9, 9];

    private static async Task<(TestDbFactory Db, TestClock Clock, PromoBannerService Promo)> SetupAsync()
    {
        var db = new TestDbFactory(await TestDb.ConnectionStringAsync());
        var clock = new TestClock(Seed.BaseNow); // Monday 2030-03-04, 08:00 in Manila
        return (db, clock, new PromoBannerService(db, clock));
    }

    private static DateOnly Day(int d) => new(2030, 3, d);

    // ---------------------------------------------------------------- the service

    [Fact]
    public async Task Bad_input_is_refused_with_a_reason_for_each_field_and_nothing_is_saved()
    {
        var (_, _, promo) = await SetupAsync();
        var shop = await Seed.CreateShopAsync();

        var none = await promo.SaveAsync(shop.BusinessId, null, "20% off", true, null);
        Assert.Equal(Text.PromoMissingPicture, none.FieldErrors["picture"]);                      // a first banner needs a picture
        Assert.Equal(Text.PromoCaptionInvalid, (await promo.SaveAsync(shop.BusinessId, Png, "   ", true, null)).FieldErrors["caption"]);
        Assert.Contains("caption", (await promo.SaveAsync(shop.BusinessId, Png, new string('x', PromoBannerService.MaxCaption + 1), true, null)).FieldErrors.Keys);
        Assert.Equal(Text.PromoTooBig, (await promo.SaveAsync(shop.BusinessId, new byte[PromoBannerService.MaxBytes + 1], "x", true, null)).FieldErrors["picture"]);
        Assert.Equal(Text.PromoWrongType, (await promo.SaveAsync(shop.BusinessId, "<svg><script>alert(1)</script></svg>"u8.ToArray(), "x", true, null)).FieldErrors["picture"]);
        Assert.Equal(Text.PromoEndsPast, (await promo.SaveAsync(shop.BusinessId, Png, "x", true, Day(3))).FieldErrors["endsOn"]);   // yesterday
        Assert.Equal(Text.PromoEndsFar, (await promo.SaveAsync(shop.BusinessId, Png, "x", true, Day(4).AddDays(366))).FieldErrors["endsOn"]);
        Assert.True((await promo.SaveAsync(shop.BusinessId, Png, "x", true, Day(4).AddDays(365))).IsSuccess);                       // a year ahead is the limit
        await promo.RemoveAsync(shop.BusinessId);

        var several = await promo.SaveAsync(shop.BusinessId, null, "", true, Day(1));
        Assert.Equal(["caption", "endsOn", "picture"], several.FieldErrors.Keys.Order());       // every problem at once, not one at a time
        Assert.Equal(FailureKind.NotFound, (await promo.SaveAsync(Guid.NewGuid(), Png, "x", true, null)).Failure);
        Assert.Equal(PromoState.None, (await promo.GetForAdminAsync(shop.BusinessId))!.State);
    }

    [Fact]
    public async Task A_saved_banner_shows_keeps_its_picture_when_only_the_words_change_and_can_be_replaced()
    {
        var (_, _, promo) = await SetupAsync();
        var shop = await Seed.CreateShopAsync();
        var slug = await SlugAsync(shop);

        Assert.Null(await promo.GetForPageAsync(shop.BusinessId, "Asia/Manila"));
        Assert.True((await promo.SaveAsync(shop.BusinessId, Png, "  20% off all haircuts  ", true, Day(6))).IsSuccess);
        var view = await promo.GetForPageAsync(shop.BusinessId, "Asia/Manila");
        Assert.Equal("20% off all haircuts", view!.Caption);                                    // trimmed
        var image = await promo.GetImageBySlugAsync(slug.ToUpperInvariant());                  // the address is not case sensitive
        Assert.Equal("image/png", image!.ContentType);
        Assert.Equal(Png, image.Data);

        Assert.True((await promo.SaveAsync(shop.BusinessId, null, "30% off", true, null)).IsSuccess);   // no new picture: the old one stays
        Assert.Equal(Png, (await promo.GetImageBySlugAsync(slug))!.Data);
        Assert.Equal("30% off", (await promo.GetForPageAsync(shop.BusinessId, "Asia/Manila"))!.Caption);
        Assert.Null((await promo.GetForAdminAsync(shop.BusinessId))!.EndsOn);                   // the last day was cleared

        Assert.True((await promo.SaveAsync(shop.BusinessId, Jpeg, "30% off", true, null)).IsSuccess);   // a new picture replaces it
        Assert.Equal("image/jpeg", (await promo.GetImageBySlugAsync(slug))!.ContentType);

        await promo.RemoveAsync(shop.BusinessId);
        Assert.Null(await promo.GetImageBySlugAsync(slug));
        Assert.Null(await promo.GetForPageAsync(shop.BusinessId, "Asia/Manila"));
    }

    [Fact]
    public async Task The_last_day_ends_at_midnight_in_the_shops_time_and_a_paused_banner_is_hidden()
    {
        var (_, clock, promo) = await SetupAsync();
        var shop = await Seed.CreateShopAsync();
        var slug = await SlugAsync(shop);
        Assert.True((await promo.SaveAsync(shop.BusinessId, Png, "Promo", true, Day(5))).IsSuccess);   // last day: Tuesday March 5 in Manila

        clock.Now = new DateTimeOffset(2030, 3, 5, 15, 59, 0, TimeSpan.Zero);                    // 23:59 Tuesday in Manila
        Assert.NotNull(await promo.GetForPageAsync(shop.BusinessId, "Asia/Manila"));
        Assert.NotNull(await promo.GetImageBySlugAsync(slug));
        Assert.Equal(PromoState.Showing, (await promo.GetForAdminAsync(shop.BusinessId))!.State);

        clock.Now = new DateTimeOffset(2030, 3, 5, 16, 0, 0, TimeSpan.Zero);                     // 00:00 Wednesday in Manila
        Assert.Null(await promo.GetForPageAsync(shop.BusinessId, "Asia/Manila"));
        Assert.Null(await promo.GetImageBySlugAsync(slug));                                      // its address stops working too
        var ended = await promo.GetForAdminAsync(shop.BusinessId);
        Assert.Equal(PromoState.Ended, ended!.State);
        Assert.NotNull(await promo.GetOwnImageAsync(shop.BusinessId));                            // the owner still sees and can renew it

        Assert.True((await promo.SaveAsync(shop.BusinessId, null, "Promo", true, Day(9))).IsSuccess);   // a new last day brings it back
        Assert.NotNull(await promo.GetForPageAsync(shop.BusinessId, "Asia/Manila"));

        Assert.True((await promo.SaveAsync(shop.BusinessId, null, "Promo", false, Day(9))).IsSuccess);  // switched off
        Assert.Null(await promo.GetForPageAsync(shop.BusinessId, "Asia/Manila"));
        Assert.Null(await promo.GetImageBySlugAsync(slug));
        Assert.Equal(PromoState.Paused, (await promo.GetForAdminAsync(shop.BusinessId))!.State);
    }

    [Fact]
    public async Task One_shops_banner_is_never_shown_or_changed_through_another_shop()
    {
        var (_, _, promo) = await SetupAsync();
        var mine = await Seed.CreateShopAsync();
        var other = await Seed.CreateShopAsync();
        Assert.True((await promo.SaveAsync(mine.BusinessId, Png, "Mine", true, null)).IsSuccess);

        Assert.Null(await promo.GetForPageAsync(other.BusinessId, "Asia/Manila"));
        Assert.Null(await promo.GetOwnImageAsync(other.BusinessId));
        Assert.Equal(PromoState.None, (await promo.GetForAdminAsync(other.BusinessId))!.State);
        await promo.RemoveAsync(other.BusinessId);                                               // another shop's remove touches nothing here
        Assert.NotNull(await promo.GetOwnImageAsync(mine.BusinessId));
        Assert.True((await promo.SaveAsync(other.BusinessId, Jpeg, "Theirs", true, null)).IsSuccess);
        Assert.Equal("Mine", (await promo.GetForPageAsync(mine.BusinessId, "Asia/Manila"))!.Caption);
        Assert.Equal(Png, (await promo.GetOwnImageAsync(mine.BusinessId))!.Data);
    }

    private static async Task<string> SlugAsync(Shop shop)
    {
        await using var ctx = new TestDbFactory(shop.ConnectionString).CreateDbContext();
        return await ctx.Businesses.Where(b => b.Id == shop.BusinessId).Select(b => b.Slug).SingleAsync();
    }

    // ---------------------------------------------------------------- the web layer

    private static async Task<(HttpClient Client, string Slug, Guid BusinessId)> RegisterOwnerAsync(AppFactory factory)
    {
        var client = HttpApp.NewClient(factory);
        var slug = HttpApp.Unique("pr");
        var response = await HttpApp.PostFormAsync(client, "/Register", new()
        {
            ["Input.Accept"] = "true", ["Input.BusinessName"] = "Promo Shop", ["Input.Slug"] = slug, ["Input.OwnerName"] = "Ben Santos",
            ["Input.Email"] = $"{HttpApp.Unique("o")}@example.com", ["Input.Password"] = "correct-horse-1",
        });
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        await using var ctx = new TestDbFactory(await TestDb.ConnectionStringAsync()).CreateDbContext();
        return (client, slug, await ctx.Businesses.Where(b => b.Slug == slug).Select(b => b.Id).SingleAsync());
    }

    private static async Task<HttpResponseMessage> UploadAsync(HttpClient client, byte[]? picture, string caption, bool active = true, string? endsOn = null)
    {
        var page = await client.GetStringAsync("/Admin/Promo");
        using var form = new MultipartFormDataContent
        {
            { new StringContent(HttpApp.TokenIn(page, "/Admin/Promo")), "__RequestVerificationToken" },
            { new StringContent(caption), "Input.Caption" },
        };
        if (active) form.Add(new StringContent("true"), "Input.Active");
        if (endsOn is not null) form.Add(new StringContent(endsOn), "Input.EndsOn");
        if (picture is not null)
        {
            var file = new ByteArrayContent(picture);
            file.Headers.ContentType = new MediaTypeHeaderValue("image/png");
            form.Add(file, "picture", "promo.png");
        }
        return await client.PostAsync("/Admin/Promo", form);
    }

    [Fact]
    public async Task The_owner_uploads_a_banner_and_customers_see_it_until_it_is_paused_or_removed()
    {
        var (factory, _) = await HttpApp.StartAsync();
        using var _f = factory;
        var (owner, slug, _) = await RegisterOwnerAsync(factory);
        var visitor = HttpApp.NewClient(factory);

        Assert.Equal(HttpStatusCode.NotFound, (await visitor.GetAsync($"/banner/{slug}")).StatusCode);   // none yet
        Assert.DoesNotContain("promo-img", await visitor.GetStringAsync($"/b/{slug}"));
        Assert.Contains(Text.PromoStatusNone, await owner.GetStringAsync("/Admin/Promo"));

        // A first banner without a picture is refused and what was typed stays in the form.
        var refused = await UploadAsync(owner, null, "20% off all haircuts");
        Assert.Equal(HttpStatusCode.OK, refused.StatusCode);
        var refusedHtml = WebUtility.HtmlDecode(await refused.Content.ReadAsStringAsync());
        Assert.Contains(Text.PromoMissingPicture, refusedHtml);
        Assert.Contains("value=\"20% off all haircuts\"", refusedHtml);

        Assert.Equal(HttpStatusCode.Redirect, (await UploadAsync(owner, Png, "20% off all haircuts")).StatusCode);
        Assert.Contains(Text.PromoSaved, WebUtility.HtmlDecode(await owner.GetStringAsync("/Admin/Promo")));

        var page = await visitor.GetStringAsync($"/b/{slug}");
        Assert.Matches($"<img class=\"promo-img\" src=\"/banner/{slug}\\?v=\\d+\" alt=\"20% off all haircuts\"", page);
        Assert.True(page.IndexOf("promo-img", StringComparison.Ordinal) < page.IndexOf("id=\"book\"", StringComparison.Ordinal)); // above the booking form

        var image = await visitor.GetAsync($"/banner/{slug}");
        Assert.Equal(HttpStatusCode.OK, image.StatusCode);
        Assert.Equal("image/png", image.Content.Headers.ContentType!.MediaType);
        Assert.Equal("no-cache", image.Headers.CacheControl?.ToString());
        Assert.Equal(Png, await image.Content.ReadAsByteArrayAsync());
        var again = new HttpRequestMessage(HttpMethod.Get, $"/banner/{slug}");
        again.Headers.TryAddWithoutValidation("If-None-Match", image.Headers.ETag!.ToString());
        Assert.Equal(HttpStatusCode.NotModified, (await visitor.SendAsync(again)).StatusCode);

        // Paused: gone from the page and its address stops working, but the owner still sees the picture to switch it back on.
        Assert.Equal(HttpStatusCode.Redirect, (await UploadAsync(owner, null, "20% off all haircuts", active: false)).StatusCode);
        Assert.DoesNotContain("promo-img", await visitor.GetStringAsync($"/b/{slug}"));
        Assert.Equal(HttpStatusCode.NotFound, (await visitor.GetAsync($"/banner/{slug}")).StatusCode);
        var preview = await owner.GetAsync("/Admin/Promo?handler=Image");
        Assert.Equal(HttpStatusCode.OK, preview.StatusCode);
        Assert.Equal(Png, await preview.Content.ReadAsByteArrayAsync());
        Assert.Contains(Text.PromoStatusPaused, await owner.GetStringAsync("/Admin/Promo"));

        // Removed.
        Assert.Equal(HttpStatusCode.Redirect, (await UploadAsync(owner, null, "x", true)).StatusCode); // back on, then removed below
        var page2 = await owner.GetStringAsync("/Admin/Promo");
        var removed = await owner.PostAsync("/Admin/Promo?handler=Remove", new FormUrlEncodedContent(new Dictionary<string, string>
            { ["__RequestVerificationToken"] = HttpApp.TokenIn(page2, "/Admin/Promo") }));
        Assert.Equal(HttpStatusCode.Redirect, removed.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await visitor.GetAsync($"/banner/{slug}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await owner.GetAsync("/Admin/Promo?handler=Image")).StatusCode);
    }

    [Fact]
    public async Task The_promo_page_needs_a_sign_in_and_an_owners_picture_is_not_reachable_by_anyone_else()
    {
        var (factory, _) = await HttpApp.StartAsync();
        using var _f = factory;
        var (owner, slug, _) = await RegisterOwnerAsync(factory);
        var (neighbour, _, _) = await RegisterOwnerAsync(factory);
        var anonymous = HttpApp.NewClient(factory);

        Assert.Equal(HttpStatusCode.Redirect, (await anonymous.GetAsync("/Admin/Promo")).StatusCode);
        Assert.StartsWith("/Login", (await anonymous.GetAsync("/Admin/Promo")).Headers.Location!.PathAndQuery);
        Assert.Equal(HttpStatusCode.Redirect, (await UploadAsync(owner, Png, "Mine")).StatusCode);
        Assert.Equal(HttpStatusCode.Redirect, (await anonymous.GetAsync("/Admin/Promo?handler=Image")).StatusCode);   // sign-in first
        Assert.Equal(HttpStatusCode.NotFound, (await neighbour.GetAsync("/Admin/Promo?handler=Image")).StatusCode);   // their own shop has none

        // Only a real picture is accepted, whatever the browser says it is.
        var fake = await UploadAsync(neighbour, "<html><script>alert(1)</script></html>"u8.ToArray(), "Not a picture");
        Assert.Equal(HttpStatusCode.OK, fake.StatusCode);
        Assert.Contains(Text.PromoWrongType, WebUtility.HtmlDecode(await fake.Content.ReadAsStringAsync()));
    }

    [Fact]
    public async Task The_banner_is_hidden_when_the_shop_is_not_taking_bookings()
    {
        var (factory, _) = await HttpApp.StartAsync();
        using var _f = factory;
        var (owner, slug, businessId) = await RegisterOwnerAsync(factory);
        Assert.Equal(HttpStatusCode.Redirect, (await UploadAsync(owner, Png, "Promo")).StatusCode);
        var visitor = HttpApp.NewClient(factory);
        Assert.Contains("promo-img", await visitor.GetStringAsync($"/b/{slug}"));

        await using (var ctx = new TestDbFactory(await TestDb.ConnectionStringAsync()).CreateDbContext())
            await ctx.Businesses.Where(b => b.Id == businessId).ExecuteUpdateAsync(u => u.SetProperty(b => b.TrialEndsAtUtc, DateTime.UtcNow.AddDays(-1)));
        Assert.DoesNotContain("promo-img", await visitor.GetStringAsync($"/b/{slug}"));       // a closed shop shows no promo
    }
}
