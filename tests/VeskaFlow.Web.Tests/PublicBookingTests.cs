using System.Net;
using System.Text.RegularExpressions;
using System.Web;
using VeskaFlow.Web.Data;
using VeskaFlow.Web.Services;
using Microsoft.EntityFrameworkCore;
using static VeskaFlow.Web.Tests.HttpApp;

namespace VeskaFlow.Web.Tests;

/// <summary>The public booking flow over HTTP: real app, real PostgreSQL, real clock.</summary>
[Collection("http")]
public class PublicBookingTests
{
    private sealed record ShopInfo(string Slug, Guid BusinessId, Guid ServiceId, Guid StaffId, TestDbFactory Db);

    private static async Task<ShopInfo> NewShopAsync(int cancelCutoffHours = 2)
    {
        var db = new TestDbFactory(await TestDb.ConnectionStringAsync());
        var slug = Unique("pub");
        var owner = (await new OnboardingService(db).RegisterAsync(
            new RegisterRequest("Ben's Barbershop", slug, "Ben Santos", $"{Unique("o")}@example.com", "correct-horse-1"))).Value!;
        await using var ctx = db.CreateDbContext();
        await ctx.Businesses.Where(b => b.Id == owner.BusinessId)
            .ExecuteUpdateAsync(s => s.SetProperty(b => b.CancelCutoffHours, cancelCutoffHours));
        var serviceId = await ctx.Services.Where(s => s.BusinessId == owner.BusinessId && !s.IsOther).Select(s => s.Id).SingleAsync();
        var staffId = await ctx.StaffMembers.Where(s => s.BusinessId == owner.BusinessId).Select(s => s.Id).SingleAsync();
        return new ShopInfo(slug, owner.BusinessId, serviceId, staffId, db);
    }

    private static async Task<int> CountBookingsAsync(ShopInfo shop, string? status = null)
    {
        await using var ctx = shop.Db.CreateDbContext();
        var query = ctx.Bookings.Where(b => b.BusinessId == shop.BusinessId);
        if (status is not null)
        {
            var parsed = VeskaFlow.Core.Domain.BookingStatusExtensions.FromDb(status);
            query = query.Where(b => b.Status == parsed);
        }
        return await query.CountAsync();
    }

    /// <summary>Walks forward from today (Manila) until the time list shows a slot; returns the link behind it, as a browser would follow it.</summary>
    private static async Task<(string DetailsUrl, DateTime StartUtc)> FindSlotAsync(HttpClient client, ShopInfo shop, int skip = 0)
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow.AddHours(8));
        for (var d = 0; d < 12; d++)
        {
            var day = today.AddDays(d).ToString("yyyy-MM-dd");
            var html = await client.GetStringAsync($"/b/{shop.Slug}?serviceId={shop.ServiceId}&date={day}");
            var links = Regex.Matches(html, "href=\"([^\"]*/confirm\\?[^\"]*)\"").Select(m => WebUtility.HtmlDecode(m.Groups[1].Value)).ToList();
            if (links.Count > skip)
            {
                var url = links[skip];
                var start = DateTime.Parse(HttpUtility.ParseQueryString(new Uri("http://x" + url).Query)["start"]!,
                    null, System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal);
                return (url, start);
            }
        }
        throw new InvalidOperationException("No free slot found in the next 12 days.");
    }

    /// <summary>The hidden inputs of a page, i.e. what a browser would send back besides the visible fields.</summary>
    private static Dictionary<string, string> HiddenFields(string html)
    {
        var fields = new Dictionary<string, string>();
        foreach (Match tag in Regex.Matches(html, "<input[^>]*>"))
        {
            var t = tag.Value;
            if (!Regex.IsMatch(t, "type=\"hidden\"")) continue;
            var name = Regex.Match(t, "name=\"([^\"]*)\"").Groups[1].Value;
            fields[name] = WebUtility.HtmlDecode(Regex.Match(t, "value=\"([^\"]*)\"").Groups[1].Value);
        }
        return fields;
    }

    private static Dictionary<string, string> Customer(Dictionary<string, string> hidden, string name = "Juan Dela Cruz", string phone = "0917 123 4567")
    {
        var f = new Dictionary<string, string>(hidden) { ["Input.Name"] = name, ["Input.Phone"] = phone, ["Input.Email"] = "" };
        return f;
    }

    private static string PathOf(HttpResponseMessage response)
    {
        var l = response.Headers.Location!;
        return l.IsAbsoluteUri ? l.PathAndQuery : l.OriginalString;
    }

    [Fact]
    public async Task A_customer_can_book_from_the_time_list_and_sees_their_confirmation()
    {
        var (factory, client) = await StartAsync();
        using var _ = factory;
        var shop = await NewShopAsync();

        var (url, start) = await FindSlotAsync(client, shop);
        var form = await client.GetStringAsync(url);
        Assert.Contains(Text.BookConfirm, form);
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(form, "We keep your name")); // one privacy note, not two
        Assert.Contains("href=\"/Privacy\"", form);
        Assert.Contains($"data-confirm=\"{Text.ConfirmBooking}\"", WebUtility.HtmlDecode(form)); // asks before booking
        Assert.Equal("ok", await client.GetStringAsync("/ping")); // the keep-awake address answers without the database
        Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(new HttpRequestMessage(HttpMethod.Head, "/ping"))).StatusCode); // uptime monitors ask with HEAD
        Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(new HttpRequestMessage(HttpMethod.Head, "/healthz"))).StatusCode);

        var response = await client.PostAsync(url, new FormUrlEncodedContent(Customer(HiddenFields(form))));

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var location = PathOf(response);
        Assert.StartsWith("/booking/", location);
        Assert.EndsWith("confirmed=1", location);

        var manage = WebUtility.HtmlDecode(await client.GetStringAsync(location));
        Assert.Contains("Ben's Barbershop", manage);
        Assert.Contains("Haircut", manage);
        Assert.Contains("Juan Dela Cruz", manage);
        Assert.Contains(Text.ManageConfirmed, manage);
        Assert.Contains("Confirmed", manage);
        Assert.Equal(1, await CountBookingsAsync(shop));

        // The customer can keep the private link and add the appointment to the phone calendar.
        Assert.Contains(Text.ManageSaveTitle, manage);
        Assert.Contains("id=\"manage-link\"", manage);
        var token = location["/booking/".Length..location.IndexOf('?')];
        var calendar = await client.GetAsync($"/booking/{token}?handler=Calendar");
        Assert.Equal(HttpStatusCode.OK, calendar.StatusCode);
        Assert.Equal("text/calendar", calendar.Content.Headers.ContentType!.MediaType);
        var ics = await calendar.Content.ReadAsStringAsync();
        Assert.Contains("BEGIN:VEVENT", ics);
        Assert.Contains($"DTSTART:{start:yyyyMMdd'T'HHmmss'Z'}", ics);
        Assert.Contains("SUMMARY:Haircut at Ben's Barbershop", ics);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/booking/no-such-token-0123456789?handler=Calendar")).StatusCode);

        await using var ctx = shop.Db.CreateDbContext();
        var row = await ctx.Bookings.SingleAsync(b => b.BusinessId == shop.BusinessId);
        Assert.Equal(start, DateTime.SpecifyKind(row.StartsAtUtc, DateTimeKind.Utc));
        Assert.Equal("09171234567", (await ctx.Customers.SingleAsync(c => c.BusinessId == shop.BusinessId)).Phone);
    }

    [Fact]
    public async Task The_booking_page_uses_the_shops_color_theme_and_other_shops_keep_the_default()
    {
        var (factory, client) = await StartAsync();
        using var _ = factory;
        var colored = await NewShopAsync();
        var plain = await NewShopAsync();
        await using (var ctx = colored.Db.CreateDbContext())
            await ctx.Businesses.Where(b => b.Id == colored.BusinessId)
                .ExecuteUpdateAsync(s => s.SetProperty(b => b.BrandColor, "rose"));

        Assert.Contains("data-theme=\"rose\"", await client.GetStringAsync($"/b/{colored.Slug}"));
        Assert.Contains("data-theme=\"teal\"", await client.GetStringAsync($"/b/{plain.Slug}"));
    }

    [Fact]
    public async Task The_public_page_never_shows_customer_names_or_numbers()
    {
        var (factory, client) = await StartAsync();
        using var _ = factory;
        var shop = await NewShopAsync();
        var (url, _) = await FindSlotAsync(client, shop);
        var form = await client.GetStringAsync(url);
        await client.PostAsync(url, new FormUrlEncodedContent(Customer(HiddenFields(form), "Zacharias Quimby", "09175550123")));

        var page = await client.GetStringAsync($"/b/{shop.Slug}?serviceId={shop.ServiceId}&date={DateOnly.FromDateTime(DateTime.UtcNow.AddHours(8)):yyyy-MM-dd}");

        Assert.DoesNotContain("Zacharias", page);
        Assert.DoesNotContain("09175550123", page);
    }

    [Fact]
    public async Task Double_clicking_confirm_creates_one_booking_and_both_clicks_land_on_the_same_page()
    {
        var (factory, client) = await StartAsync();
        using var _ = factory;
        var shop = await NewShopAsync();
        var (url, _) = await FindSlotAsync(client, shop);
        var form = await client.GetStringAsync(url);
        var fields = Customer(HiddenFields(form));

        var responses = await Task.WhenAll(Enumerable.Range(0, 2).Select(_ =>
            Task.Run(() => client.PostAsync(url, new FormUrlEncodedContent(new Dictionary<string, string>(fields))))));

        Assert.All(responses, r => Assert.Equal(HttpStatusCode.Redirect, r.StatusCode));
        Assert.Equal(PathOf(responses[0]), PathOf(responses[1]));
        Assert.Equal(1, await CountBookingsAsync(shop));
    }

    [Fact]
    public async Task Losing_the_race_returns_to_the_time_list_with_a_just_taken_message()
    {
        var (factory, client) = await StartAsync();
        using var _ = factory;
        var shop = await NewShopAsync();
        var (url, start) = await FindSlotAsync(client, shop);
        var form = await client.GetStringAsync(url); // this customer is now looking at the form

        // Someone else books the same time first.
        var other = new BookingService(shop.Db, TimeProvider.System);
        var taken = await other.CreateAsync(new CreateBookingRequest(shop.BusinessId, shop.ServiceId, null, start,
            "Maria", "09170000001", null, Unique("k"), BookingActor.Customer("Maria")));
        Assert.True(taken.IsSuccess);

        var response = await client.PostAsync(url, new FormUrlEncodedContent(Customer(HiddenFields(form))));

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var back = PathOf(response);
        Assert.StartsWith($"/b/{shop.Slug}", back);
        var list = WebUtility.HtmlDecode(await client.GetStringAsync(back));
        Assert.Contains(Text.BookTaken, list);
        Assert.Contains("class=\"slot\"", list); // back at the time list, which still offers other times (or none that day)
        Assert.Equal(1, await CountBookingsAsync(shop)); // only Maria's booking exists
    }

    [Fact]
    public async Task A_stale_confirm_link_for_a_taken_time_goes_back_to_the_time_list()
    {
        var (factory, client) = await StartAsync();
        using var _ = factory;
        var shop = await NewShopAsync();
        var (url, start) = await FindSlotAsync(client, shop);
        await new BookingService(shop.Db, TimeProvider.System).CreateAsync(new CreateBookingRequest(
            shop.BusinessId, shop.ServiceId, null, start, "Maria", "09170000002", null, Unique("k"), BookingActor.Customer("Maria")));

        var response = await client.GetAsync(url);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.StartsWith($"/b/{shop.Slug}", PathOf(response));
    }

    [Fact]
    public async Task Unknown_slug_unknown_token_and_unknown_url_return_the_identical_not_found_page()
    {
        var (factory, client) = await StartAsync();
        using var _ = factory;

        var slug = await client.GetAsync("/b/no-such-shop");
        var token = await client.GetAsync("/booking/no-such-token-0123456789");
        var url = await client.GetAsync("/definitely/not/here");

        Assert.Equal(HttpStatusCode.NotFound, slug.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, token.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, url.StatusCode);
        var a = await slug.Content.ReadAsStringAsync();
        Assert.Equal(a, await token.Content.ReadAsStringAsync());
        Assert.Equal(a, await url.Content.ReadAsStringAsync());
        Assert.Contains(Text.NotFoundTitle, a);
    }

    [Fact]
    public async Task A_service_id_from_another_shop_is_not_found()
    {
        var (factory, client) = await StartAsync();
        using var _ = factory;
        var shopA = await NewShopAsync();
        var shopB = await NewShopAsync();
        var (urlA, _) = await FindSlotAsync(client, shopA);

        var forged = urlA.Replace(shopA.ServiceId.ToString(), shopB.ServiceId.ToString(), StringComparison.OrdinalIgnoreCase);

        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync(forged)).StatusCode);
    }

    [Fact]
    public async Task Invalid_details_show_field_errors_and_create_nothing()
    {
        var (factory, client) = await StartAsync();
        using var _ = factory;
        var shop = await NewShopAsync();
        var (url, _) = await FindSlotAsync(client, shop);
        var form = await client.GetStringAsync(url);

        var response = await client.PostAsync(url, new FormUrlEncodedContent(Customer(HiddenFields(form), name: "Juan", phone: "abc")));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains(Text.PhoneInvalid, WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync()));
        Assert.Equal(0, await CountBookingsAsync(shop));
    }

    [Fact]
    public async Task The_customer_can_cancel_from_the_manage_page_before_the_cutoff()
    {
        var (factory, client) = await StartAsync();
        using var _ = factory;
        var shop = await NewShopAsync(cancelCutoffHours: 0); // cancel allowed until the start itself
        var (url, _) = await FindSlotAsync(client, shop);
        var created = await client.PostAsync(url, new FormUrlEncodedContent(Customer(HiddenFields(await client.GetStringAsync(url)))));
        var manage = PathOf(created);

        var page = await client.GetStringAsync(manage);
        Assert.Contains(Text.ManageCancelButton, page);
        var cancelAction = WebUtility.HtmlDecode(Regex.Match(page, "<form[^>]*action=\"([^\"]*handler=Cancel[^\"]*)\"").Groups[1].Value);
        var cancel = await client.PostAsync(cancelAction, new FormUrlEncodedContent(HiddenFields(page)));

        Assert.Equal(HttpStatusCode.Redirect, cancel.StatusCode);
        var after = await client.GetStringAsync(new Uri(manage, UriKind.Relative).OriginalString.Split('?')[0]);
        Assert.Contains(Text.ManageCancelled, after);
        Assert.DoesNotContain(Text.ManageCancelButton, after);
        Assert.Equal(1, await CountBookingsAsync(shop, "CANCELLED"));
    }

    [Fact]
    public async Task Cancelling_after_the_cutoff_is_refused_with_the_shops_cutoff_and_the_booking_stays()
    {
        var (factory, client) = await StartAsync();
        using var _ = factory;
        var shop = await NewShopAsync(cancelCutoffHours: 5000); // cutoff beyond the whole horizon: always too late
        var (url, _) = await FindSlotAsync(client, shop);
        var created = await client.PostAsync(url, new FormUrlEncodedContent(Customer(HiddenFields(await client.GetStringAsync(url)))));
        var manage = PathOf(created).Split('?')[0];

        var page = await client.GetStringAsync(manage);
        Assert.DoesNotContain(Text.ManageCancelButton, page);
        Assert.Contains("5000 hour(s)", WebUtility.HtmlDecode(page));

        // A hand-made POST gets the same refusal and changes nothing.
        var forged = await client.PostAsync($"{manage}?handler=Cancel", new FormUrlEncodedContent(
            new Dictionary<string, string> { ["__RequestVerificationToken"] = TokenIn(await client.GetStringAsync("/Login"), "/Login") }));
        Assert.Equal(HttpStatusCode.Redirect, forged.StatusCode);
        Assert.Contains("5000 hour(s)", WebUtility.HtmlDecode(await client.GetStringAsync(manage)));
        Assert.Equal(1, await CountBookingsAsync(shop, "CONFIRMED"));
    }

    [Fact]
    public async Task Booking_posts_are_rate_limited_per_client_but_browsing_is_not()
    {
        var (factory, client) = await HttpApp.StartAsync(bookingPerMinute: 2);
        using var _ = factory;
        var shop = await NewShopAsync();
        var (url, _) = await FindSlotAsync(client, shop);
        var form = await client.GetStringAsync(url);

        var statuses = new List<HttpStatusCode>();
        for (var i = 0; i < 3; i++)
            statuses.Add((await client.PostAsync(url, new FormUrlEncodedContent(Customer(HiddenFields(form))))).StatusCode);

        Assert.Equal(new[] { HttpStatusCode.Redirect, HttpStatusCode.Redirect, HttpStatusCode.TooManyRequests }, statuses);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"/b/{shop.Slug}")).StatusCode);
    }

    [Fact]
    public async Task The_manage_page_is_not_cached_and_not_indexed()
    {
        var (factory, client) = await StartAsync();
        using var _ = factory;
        var shop = await NewShopAsync();
        var (url, _) = await FindSlotAsync(client, shop);
        var created = await client.PostAsync(url, new FormUrlEncodedContent(Customer(HiddenFields(await client.GetStringAsync(url)))));

        var page = await client.GetAsync(PathOf(created));

        Assert.Contains("no-store", string.Join(",", page.Headers.CacheControl?.ToString() ?? ""));
        Assert.Contains("noindex", await page.Content.ReadAsStringAsync());
    }
}
