using System.Net;
using System.Net.Http.Headers;
using Microsoft.EntityFrameworkCore;
using VeskaFlow.Web;
using VeskaFlow.Web.Data;
using VeskaFlow.Web.Services;

namespace VeskaFlow.Web.Tests;

/// <summary>The owner's Promo Creator: modes, headline, details and badge, the last day in the shop's time, isolation between shops, and the web pages.</summary>
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

    private static Task<ServiceResult<bool>> Save(
        PromoBannerService promo, Shop shop, byte[]? picture = null, bool removePicture = false, string? caption = "20% off all haircuts",
        string? details = null, string? badge = null, string? theme = null, bool active = true, DateOnly? endsOn = null) =>
        promo.SaveAsync(shop.BusinessId, picture, removePicture, caption, details, badge, theme, active, endsOn);

    private static async Task<string> SlugAsync(Shop shop)
    {
        await using var ctx = new TestDbFactory(shop.ConnectionString).CreateDbContext();
        return await ctx.Businesses.Where(b => b.Id == shop.BusinessId).Select(b => b.Slug).SingleAsync();
    }

    // ---------------------------------------------------------------- the service

    [Fact]
    public async Task Bad_input_is_refused_with_a_reason_for_each_field_and_nothing_is_saved()
    {
        var (_, _, promo) = await SetupAsync();
        var shop = await Seed.CreateShopAsync();

        Assert.Equal(Text.PromoCaptionInvalid, (await Save(promo, shop, caption: "   ")).FieldErrors["caption"]);
        Assert.Contains("caption", (await Save(promo, shop, caption: new string('x', PromoBannerService.MaxCaption + 1))).FieldErrors.Keys);
        Assert.Equal(Text.PromoDetailsInvalid, (await Save(promo, shop, details: new string('x', PromoBannerService.MaxDetails + 1))).FieldErrors["details"]);
        Assert.Equal(Text.PromoBadgeInvalid, (await Save(promo, shop, badge: new string('x', PromoBannerService.MaxBadge + 1))).FieldErrors["badge"]);
        Assert.Equal(Text.PromoThemeInvalid, (await Save(promo, shop, theme: "easter")).FieldErrors["theme"]);                  // only the listed modes
        Assert.Equal(Text.PromoTooBig, (await Save(promo, shop, new byte[PromoBannerService.MaxBytes + 1])).FieldErrors["picture"]);
        Assert.Equal(Text.PromoWrongType, (await Save(promo, shop, "<svg><script>alert(1)</script></svg>"u8.ToArray())).FieldErrors["picture"]);
        Assert.Equal(Text.PromoEndsPast, (await Save(promo, shop, endsOn: Day(3))).FieldErrors["endsOn"]);                       // yesterday
        Assert.Equal(Text.PromoEndsFar, (await Save(promo, shop, endsOn: Day(4).AddDays(366))).FieldErrors["endsOn"]);
        Assert.Equal(PromoState.None, (await promo.GetForAdminAsync(shop.BusinessId))!.State);                                   // none of that was kept

        var several = await Save(promo, shop, caption: "", badge: new string('x', 17), theme: "nope", endsOn: Day(1));
        Assert.Equal(["badge", "caption", "endsOn", "theme"], several.FieldErrors.Keys.Order());                                // every problem at once
        Assert.Equal(FailureKind.NotFound, (await promo.SaveAsync(Guid.NewGuid(), Png, false, "x", null, null, null, true, null)).Failure);

        Assert.True((await Save(promo, shop, endsOn: Day(4).AddDays(365))).IsSuccess);                                          // a year ahead is the limit
        Assert.True((await Save(promo, shop, details: new string('x', PromoBannerService.MaxDetails), badge: new string('x', PromoBannerService.MaxBadge))).IsSuccess);
    }

    [Fact]
    public async Task A_promo_can_be_words_only_with_a_badge_and_details_and_a_picture_is_optional_and_can_be_added_kept_or_removed()
    {
        var (_, _, promo) = await SetupAsync();
        var shop = await Seed.CreateShopAsync();
        var slug = await SlugAsync(shop);

        Assert.Null(await promo.GetForPageAsync(shop.BusinessId, "Asia/Manila"));
        Assert.True((await Save(promo, shop, caption: "  20% off all haircuts  ", details: "  Book any weekday.  ", badge: " 20% OFF ")).IsSuccess);
        var wordsOnly = (await promo.GetForPageAsync(shop.BusinessId, "Asia/Manila"))!;
        Assert.Equal(("20% off all haircuts", "Book any weekday.", "20% OFF"), (wordsOnly.Headline, wordsOnly.Details, wordsOnly.Badge));   // trimmed
        Assert.False(wordsOnly.HasPicture);                                                                                     // no picture at all
        Assert.Equal(PromoThemes.Default, wordsOnly.Theme);
        Assert.Null(await promo.GetImageBySlugAsync(slug));
        Assert.Null(await promo.GetOwnImageAsync(shop.BusinessId));
        var admin = (await promo.GetForAdminAsync(shop.BusinessId))!;
        Assert.True(admin.Exists);
        Assert.False(admin.HasPicture);

        Assert.True((await Save(promo, shop, Png, theme: "halloween", endsOn: Day(6))).IsSuccess);                              // add a picture
        var image = await promo.GetImageBySlugAsync(slug.ToUpperInvariant());                                                  // the address is not case sensitive
        Assert.Equal("image/png", image!.ContentType);
        Assert.Equal(Png, image.Data);
        var withPicture = (await promo.GetForPageAsync(shop.BusinessId, "Asia/Manila"))!;
        Assert.Equal(("halloween", Day(6)), (withPicture.Theme, withPicture.EndsOn!.Value));
        Assert.Null(withPicture.Details);                                                                                       // empty fields are cleared, not kept
        Assert.Null(withPicture.Badge);

        Assert.True((await Save(promo, shop, caption: "30% off", theme: "christmas")).IsSuccess);                                // no new picture: the old one stays
        Assert.Equal(Png, (await promo.GetImageBySlugAsync(slug))!.Data);
        var changed = (await promo.GetForPageAsync(shop.BusinessId, "Asia/Manila"))!;
        Assert.Equal(("30% off", "christmas"), (changed.Headline, changed.Theme));
        Assert.Null(changed.EndsOn);                                                                                           // the last day was cleared

        Assert.True((await Save(promo, shop, Jpeg, caption: "30% off")).IsSuccess);                                             // a new picture replaces it
        Assert.Equal("image/jpeg", (await promo.GetImageBySlugAsync(slug))!.ContentType);

        Assert.True((await Save(promo, shop, removePicture: true, caption: "30% off")).IsSuccess);                              // drop the picture, keep the words
        Assert.Null(await promo.GetImageBySlugAsync(slug));
        Assert.Equal("30% off", (await promo.GetForPageAsync(shop.BusinessId, "Asia/Manila"))!.Headline);
        Assert.False((await promo.GetForAdminAsync(shop.BusinessId))!.HasPicture);

        await promo.RemoveAsync(shop.BusinessId);
        Assert.Null(await promo.GetForPageAsync(shop.BusinessId, "Asia/Manila"));
        Assert.False((await promo.GetForAdminAsync(shop.BusinessId))!.Exists);
    }

    [Fact]
    public async Task Every_mode_can_be_saved_and_the_database_refuses_anything_else()
    {
        var (db, _, promo) = await SetupAsync();
        var shop = await Seed.CreateShopAsync();

        Assert.Equal(["plain", "halloween", "christmas", "new_year", "valentines", "summer", "back_to_school", "payday", "rainy"], PromoThemes.All);
        foreach (var key in PromoThemes.All)
        {
            Assert.True((await Save(promo, shop, theme: key)).IsSuccess, key);
            Assert.Equal(key, (await promo.GetForPageAsync(shop.BusinessId, "Asia/Manila"))!.Theme);
            Assert.False(string.IsNullOrWhiteSpace(PromoThemes.Label(key)));
            Assert.False(string.IsNullOrWhiteSpace(PromoThemes.Kicker(key)));
        }
        Assert.Equal("plain", PromoThemes.Normalize("easter"));
        Assert.Equal("plain", PromoThemes.Normalize(null));

        // The database checks too: a wrong mode, a type without a picture, and words that are too long or empty.
        await using var ctx = db.CreateDbContext();
        var id = shop.BusinessId;
        var theme = await Assert.ThrowsAnyAsync<Exception>(() => ctx.Database.ExecuteSqlInterpolatedAsync($"UPDATE business_banner SET theme = 'easter' WHERE business_id = {id}"));
        Assert.Contains("business_banner_theme", theme.ToString());
        await Save(promo, shop, Png);
        var pair = await Assert.ThrowsAnyAsync<Exception>(() => ctx.Database.ExecuteSqlInterpolatedAsync($"UPDATE business_banner SET content_type = NULL WHERE business_id = {id}"));
        Assert.Contains("business_banner_picture_pair", pair.ToString());
        var badge = await Assert.ThrowsAnyAsync<Exception>(() => ctx.Database.ExecuteSqlInterpolatedAsync($"UPDATE business_banner SET badge = {new string('x', 17)} WHERE business_id = {id}"));
        Assert.Contains("business_banner_badge", badge.ToString());
        var empty = await Assert.ThrowsAnyAsync<Exception>(() => ctx.Database.ExecuteSqlInterpolatedAsync($"UPDATE business_banner SET details = '' WHERE business_id = {id}"));
        Assert.Contains("business_banner_details", empty.ToString());
    }

    [Fact]
    public async Task The_last_day_ends_at_midnight_in_the_shops_time_and_a_paused_banner_is_hidden()
    {
        var (_, clock, promo) = await SetupAsync();
        var shop = await Seed.CreateShopAsync();
        var slug = await SlugAsync(shop);
        Assert.True((await Save(promo, shop, Png, caption: "Promo", endsOn: Day(5))).IsSuccess);                                // last day: Tuesday March 5 in Manila

        clock.Now = new DateTimeOffset(2030, 3, 5, 15, 59, 0, TimeSpan.Zero);                                                 // 23:59 Tuesday in Manila
        Assert.NotNull(await promo.GetForPageAsync(shop.BusinessId, "Asia/Manila"));
        Assert.NotNull(await promo.GetImageBySlugAsync(slug));
        Assert.Equal(PromoState.Showing, (await promo.GetForAdminAsync(shop.BusinessId))!.State);

        clock.Now = new DateTimeOffset(2030, 3, 5, 16, 0, 0, TimeSpan.Zero);                                                  // 00:00 Wednesday in Manila
        Assert.Null(await promo.GetForPageAsync(shop.BusinessId, "Asia/Manila"));
        Assert.Null(await promo.GetImageBySlugAsync(slug));                                                                   // its address stops working too
        Assert.Equal(PromoState.Ended, (await promo.GetForAdminAsync(shop.BusinessId))!.State);
        Assert.NotNull(await promo.GetOwnImageAsync(shop.BusinessId));                                                         // the owner still sees and can renew it

        Assert.True((await Save(promo, shop, caption: "Promo", endsOn: Day(9))).IsSuccess);                                    // a new last day brings it back
        Assert.NotNull(await promo.GetForPageAsync(shop.BusinessId, "Asia/Manila"));

        Assert.True((await Save(promo, shop, caption: "Promo", active: false, endsOn: Day(9))).IsSuccess);                      // switched off
        Assert.Null(await promo.GetForPageAsync(shop.BusinessId, "Asia/Manila"));
        Assert.Null(await promo.GetImageBySlugAsync(slug));
        Assert.Equal(PromoState.Paused, (await promo.GetForAdminAsync(shop.BusinessId))!.State);
    }

    [Fact]
    public async Task One_shops_promo_is_never_shown_or_changed_through_another_shop()
    {
        var (_, _, promo) = await SetupAsync();
        var mine = await Seed.CreateShopAsync();
        var other = await Seed.CreateShopAsync();
        Assert.True((await Save(promo, mine, Png, caption: "Mine", badge: "MINE", theme: "valentines")).IsSuccess);

        Assert.Null(await promo.GetForPageAsync(other.BusinessId, "Asia/Manila"));
        Assert.Null(await promo.GetOwnImageAsync(other.BusinessId));
        Assert.Equal(PromoState.None, (await promo.GetForAdminAsync(other.BusinessId))!.State);
        await promo.RemoveAsync(other.BusinessId);                                                                             // another shop's remove touches nothing here
        Assert.NotNull(await promo.GetOwnImageAsync(mine.BusinessId));
        Assert.True((await Save(promo, other, Jpeg, caption: "Theirs", badge: "THEIRS", theme: "summer")).IsSuccess);
        var kept = (await promo.GetForPageAsync(mine.BusinessId, "Asia/Manila"))!;
        Assert.Equal(("Mine", "MINE", "valentines"), (kept.Headline, kept.Badge, kept.Theme));
        Assert.Equal(Png, (await promo.GetOwnImageAsync(mine.BusinessId))!.Data);
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

    private static async Task<HttpResponseMessage> PostPromoAsync(
        HttpClient client, byte[]? picture, string caption, string theme = "plain", bool active = true, string? endsOn = null, bool removePicture = false,
        string? details = null, string? badge = null)
    {
        var page = await client.GetStringAsync("/Admin/Promo");
        using var form = new MultipartFormDataContent
        {
            { new StringContent(HttpApp.TokenIn(page, "/Admin/Promo")), "__RequestVerificationToken" },
            { new StringContent(caption), "Input.Caption" },
            { new StringContent(theme), "Input.Theme" },
        };
        if (details is not null) form.Add(new StringContent(details), "Input.Details");
        if (badge is not null) form.Add(new StringContent(badge), "Input.Badge");
        if (active) form.Add(new StringContent("true"), "Input.Active");
        if (removePicture) form.Add(new StringContent("true"), "Input.RemovePicture");
        if (endsOn is not null) form.Add(new StringContent(endsOn), "Input.EndsOn");
        if (picture is not null)
        {
            var file = new ByteArrayContent(picture);
            file.Headers.ContentType = new MediaTypeHeaderValue("image/png");
            form.Add(file, "picture", "promo.png");
        }
        return await client.PostAsync("/Admin/Promo", form);
    }

    /// <summary>The promo part of the booking page: from its card to the booking form.</summary>
    private static string PromoPart(string page)
    {
        var from = page.IndexOf("class=\"promo ", StringComparison.Ordinal);
        return from < 0 ? "" : page[from..page.IndexOf("id=\"book\"", StringComparison.Ordinal)];
    }

    [Fact]
    public async Task The_owner_makes_a_themed_promo_with_or_without_a_picture_and_customers_see_it_until_it_is_paused_or_removed()
    {
        var (factory, _) = await HttpApp.StartAsync();
        using var _f = factory;
        var (owner, slug, _) = await RegisterOwnerAsync(factory);
        var visitor = HttpApp.NewClient(factory);

        Assert.Equal(HttpStatusCode.NotFound, (await visitor.GetAsync($"/banner/{slug}")).StatusCode);                         // none yet
        Assert.DoesNotContain("class=\"promo ", await visitor.GetStringAsync($"/b/{slug}"));
        var form = await owner.GetStringAsync("/Admin/Promo");
        Assert.Contains(Text.PromoStatusNone, form);
        Assert.Contains("<h1>Promo Creator</h1>", form);
        foreach (var key in PromoThemes.All) Assert.Contains($"name=\"Input.Theme\" value=\"{key}\"", form);                    // every mode can be picked
        Assert.Contains("id=\"promo-preview\"", form);                                                                         // the live preview and its script
        Assert.Contains("/js/promo-creator.js", form);
        foreach (var key in PromoThemes.All) Assert.Contains($"data-promo-icon=\"{key}\"", form);                              // the decoration of each mode for the preview

        // Words only: no picture is needed, the mode gives it its look.
        Assert.Equal(HttpStatusCode.Redirect, (await PostPromoAsync(owner, null, "20% off all haircuts", "halloween", details: "Book any weekday.", badge: "20% OFF")).StatusCode);
        Assert.Contains(Text.PromoSaved, WebUtility.HtmlDecode(await owner.GetStringAsync("/Admin/Promo")));
        var page = WebUtility.HtmlDecode(await visitor.GetStringAsync($"/b/{slug}"));
        var promoPart = PromoPart(page);
        Assert.Contains("class=\"promo promo-t-halloween\"", promoPart);
        Assert.Contains("Halloween special", promoPart);
        Assert.Contains("<h2 class=\"promo-title\">20% off all haircuts</h2>", promoPart);
        Assert.Contains(">20% OFF</p>", promoPart);
        Assert.Contains("Book any weekday.", promoPart);
        Assert.DoesNotContain("promo-img", promoPart);                                                                         // no picture
        Assert.DoesNotMatch(@"\shidden[\s>=]", promoPart);                                                                            // nothing is left hidden for customers
        Assert.Equal(HttpStatusCode.NotFound, (await visitor.GetAsync($"/banner/{slug}")).StatusCode);

        // With a picture and a last day, in another mode, without badge or details.
        var lastDay = DateOnly.FromDateTime(DateTime.UtcNow.AddHours(8)).AddDays(10);
        Assert.Equal(HttpStatusCode.Redirect, (await PostPromoAsync(owner, Png, "Merry Christmas! 20% off", "christmas", endsOn: lastDay.ToString("yyyy-MM-dd"))).StatusCode);
        promoPart = PromoPart(WebUtility.HtmlDecode(await visitor.GetStringAsync($"/b/{slug}")));
        Assert.Contains("class=\"promo promo-t-christmas\"", promoPart);
        Assert.Contains("Christmas promo", promoPart);
        Assert.DoesNotContain("promo-badge", promoPart);                                                                       // an empty badge is not shown at all
        Assert.DoesNotContain("promo-details", promoPart);
        Assert.Matches($"<img class=\"promo-img\" src=\"/banner/{slug}\\?v=\\d+\" alt=\"\"", promoPart);
        Assert.Contains(Text.PromoUntilChip(lastDay.ToString("MMMM d", System.Globalization.CultureInfo.InvariantCulture)), promoPart);
        Assert.Contains("class=\"promo-deco\"", promoPart);                                                                    // the decoration of the mode
        Assert.DoesNotMatch(@"\shidden[\s>=]", promoPart);

        var image = await visitor.GetAsync($"/banner/{slug}");
        Assert.Equal(HttpStatusCode.OK, image.StatusCode);
        Assert.Equal("image/png", image.Content.Headers.ContentType!.MediaType);
        Assert.Equal("no-cache", image.Headers.CacheControl?.ToString());
        Assert.Equal(Png, await image.Content.ReadAsByteArrayAsync());
        var again = new HttpRequestMessage(HttpMethod.Get, $"/banner/{slug}");
        again.Headers.TryAddWithoutValidation("If-None-Match", image.Headers.ETag!.ToString());
        Assert.Equal(HttpStatusCode.NotModified, (await visitor.SendAsync(again)).StatusCode);

        // Drop the picture, keep the words and the mode.
        Assert.Equal(HttpStatusCode.Redirect, (await PostPromoAsync(owner, null, "Merry Christmas! 20% off", "christmas", removePicture: true)).StatusCode);
        promoPart = PromoPart(WebUtility.HtmlDecode(await visitor.GetStringAsync($"/b/{slug}")));
        Assert.Contains("Merry Christmas! 20% off", promoPart);
        Assert.DoesNotContain("promo-img", promoPart);
        Assert.Equal(HttpStatusCode.NotFound, (await visitor.GetAsync($"/banner/{slug}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await owner.GetAsync("/Admin/Promo?handler=Image")).StatusCode);

        // Paused: gone from the page, and the owner sees why and still previews the picture.
        Assert.Equal(HttpStatusCode.Redirect, (await PostPromoAsync(owner, Png, "Merry Christmas! 20% off", "christmas", active: false)).StatusCode);
        Assert.DoesNotContain("class=\"promo ", await visitor.GetStringAsync($"/b/{slug}"));
        Assert.Equal(HttpStatusCode.NotFound, (await visitor.GetAsync($"/banner/{slug}")).StatusCode);
        var preview = await owner.GetAsync("/Admin/Promo?handler=Image");
        Assert.Equal(HttpStatusCode.OK, preview.StatusCode);
        Assert.Equal(Png, await preview.Content.ReadAsByteArrayAsync());
        Assert.Contains(Text.PromoStatusPaused, await owner.GetStringAsync("/Admin/Promo"));

        // Removed.
        var page2 = await owner.GetStringAsync("/Admin/Promo");
        var removed = await owner.PostAsync("/Admin/Promo?handler=Remove", new FormUrlEncodedContent(new Dictionary<string, string>
            { ["__RequestVerificationToken"] = HttpApp.TokenIn(page2, "/Admin/Promo") }));
        Assert.Equal(HttpStatusCode.Redirect, removed.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await visitor.GetAsync($"/banner/{slug}")).StatusCode);
        Assert.DoesNotContain("class=\"promo ", await visitor.GetStringAsync($"/b/{slug}"));
    }

    [Fact]
    public async Task Every_mode_has_its_own_look_and_a_made_up_mode_or_too_long_words_are_refused_and_not_saved()
    {
        var (factory, _) = await HttpApp.StartAsync();
        using var _f = factory;
        var (owner, slug, _) = await RegisterOwnerAsync(factory);
        var visitor = HttpApp.NewClient(factory);

        var kickers = new HashSet<string>();
        foreach (var key in PromoThemes.All)
        {
            Assert.Equal(HttpStatusCode.Redirect, (await PostPromoAsync(owner, null, $"Promo {key}", key)).StatusCode);
            var page = WebUtility.HtmlDecode(await visitor.GetStringAsync($"/b/{slug}"));
            Assert.Contains($"class=\"promo promo-t-{key}\"", page);
            Assert.Contains($"<p class=\"promo-kicker\">{PromoThemes.Kicker(key)}</p>", page);
            kickers.Add(PromoThemes.Kicker(key));
        }
        Assert.Equal(PromoThemes.All.Count, kickers.Count);                                                                     // each mode says its own thing

        // Every mode has its colours in the style sheet, so none falls back to a bare card.
        var css = await visitor.GetStringAsync("/css/site.css");
        foreach (var key in PromoThemes.All.Where(k => k != "plain")) Assert.Contains($".promo-t-{key} {{", css);

        var tampered = await PostPromoAsync(owner, null, "Promo", "<script>alert(1)</script>");
        Assert.Equal(HttpStatusCode.OK, tampered.StatusCode);
        Assert.Contains(Text.PromoThemeInvalid, WebUtility.HtmlDecode(await tampered.Content.ReadAsStringAsync()));
        var tooLong = await PostPromoAsync(owner, null, "Promo", "summer", badge: new string('x', 17));
        Assert.Equal(HttpStatusCode.OK, tooLong.StatusCode);
        Assert.Contains(Text.PromoBadgeInvalid, WebUtility.HtmlDecode(await tooLong.Content.ReadAsStringAsync()));
        Assert.Contains("promo-t-rainy", await visitor.GetStringAsync($"/b/{slug}"));                                          // what was saved before is unchanged

        // Words are shown as text, never as markup.
        Assert.Equal(HttpStatusCode.Redirect, (await PostPromoAsync(owner, null, "<b>Big</b> sale", "plain", badge: "<i>x</i>", details: "<script>alert(1)</script>")).StatusCode);
        var safe = await visitor.GetStringAsync($"/b/{slug}");
        Assert.DoesNotContain("<script>alert(1)</script>", safe);
        Assert.DoesNotContain("<b>Big</b>", safe);
        Assert.Contains("&lt;b&gt;Big&lt;/b&gt; sale", safe);
    }

    [Fact]
    public async Task The_promo_page_needs_a_sign_in_and_an_owners_picture_is_not_reachable_by_anyone_else()
    {
        var (factory, _) = await HttpApp.StartAsync();
        using var _f = factory;
        var (owner, _, _) = await RegisterOwnerAsync(factory);
        var (neighbour, _, _) = await RegisterOwnerAsync(factory);
        var anonymous = HttpApp.NewClient(factory);

        Assert.Equal(HttpStatusCode.Redirect, (await anonymous.GetAsync("/Admin/Promo")).StatusCode);
        Assert.StartsWith("/Login", (await anonymous.GetAsync("/Admin/Promo")).Headers.Location!.PathAndQuery);
        Assert.Equal(HttpStatusCode.Redirect, (await PostPromoAsync(owner, Png, "Mine")).StatusCode);
        Assert.Equal(HttpStatusCode.Redirect, (await anonymous.GetAsync("/Admin/Promo?handler=Image")).StatusCode);              // sign-in first
        Assert.Equal(HttpStatusCode.NotFound, (await neighbour.GetAsync("/Admin/Promo?handler=Image")).StatusCode);              // their own shop has none

        // Only a real picture is accepted, whatever the browser says it is.
        var fake = await PostPromoAsync(neighbour, "<html><script>alert(1)</script></html>"u8.ToArray(), "Not a picture");
        Assert.Equal(HttpStatusCode.OK, fake.StatusCode);
        Assert.Contains(Text.PromoWrongType, WebUtility.HtmlDecode(await fake.Content.ReadAsStringAsync()));
    }

    [Fact]
    public async Task The_promo_is_hidden_when_the_shop_is_not_taking_bookings()
    {
        var (factory, _) = await HttpApp.StartAsync();
        using var _f = factory;
        var (owner, slug, businessId) = await RegisterOwnerAsync(factory);
        Assert.Equal(HttpStatusCode.Redirect, (await PostPromoAsync(owner, null, "Promo", "summer")).StatusCode);
        var visitor = HttpApp.NewClient(factory);
        Assert.Contains("promo-t-summer", await visitor.GetStringAsync($"/b/{slug}"));

        await using (var ctx = new TestDbFactory(await TestDb.ConnectionStringAsync()).CreateDbContext())
            await ctx.Businesses.Where(b => b.Id == businessId).ExecuteUpdateAsync(u => u.SetProperty(b => b.TrialEndsAtUtc, DateTime.UtcNow.AddDays(-1)));
        Assert.DoesNotContain("promo-t-summer", await visitor.GetStringAsync($"/b/{slug}"));                                    // a closed shop shows no promo
    }
}
