using System.Net;
using Microsoft.EntityFrameworkCore;
using VeskaFlow.Web;
using VeskaFlow.Web.Data;
using VeskaFlow.Web.Services;

namespace VeskaFlow.Web.Tests;

/// <summary>Sending a promo to customers (BUSINESS_RULES 6.y21): consent, who is listed, the sent marks, and the pages.</summary>
[Collection("http")]
public class PromoMessageTests
{
    private static readonly BookingActor Staff = BookingActor.Staff(Guid.NewGuid(), "Ana (manager)");

    private sealed record Ctx(TestDbFactory Db, TestClock Clock, PromoMessageService Messages, PromoBannerService Promo, BookingService Booking, Shop Shop);

    private static async Task<Ctx> SetupAsync()
    {
        var db = new TestDbFactory(await TestDb.ConnectionStringAsync());
        var clock = new TestClock(Seed.BaseNow);
        var promo = new PromoBannerService(db, clock);
        return new Ctx(db, clock, new PromoMessageService(db, promo, clock), promo, new BookingService(db, clock), await Seed.CreateShopAsync());
    }

    private static async Task<Guid> BookAsync(Ctx c, int hour, string name, string phone, bool consent, string? email = null)
    {
        var request = Seed.Request(c.Shop, Seed.Manila(5, hour), actor: Staff) with { CustomerName = name, Phone = phone, Email = email, MarketingOk = consent };
        var made = await c.Booking.CreateAsync(request);
        Assert.True(made.IsSuccess, made.Message);
        await using var ctx = c.Db.CreateDbContext();
        return await ctx.Bookings.Where(b => b.Id == made.Value!.Booking.Id).Select(b => b.CustomerId).SingleAsync();
    }

    private static Task<ServiceResult<bool>> StartPromo(Ctx c) => c.Promo.SaveAsync(c.Shop.BusinessId, null, false, "Haircut week", "Bring a friend", null, "plain", true, null, 20);

    [Fact]
    public async Task Consent_comes_only_from_a_tick_and_a_later_booking_without_it_never_takes_it_away()
    {
        var c = await SetupAsync();
        var none = await BookAsync(c, 9, "Pia", "09175550101", false);
        var yes = await BookAsync(c, 10, "Rey", "09175550102", true);
        await using (var ctx = c.Db.CreateDbContext())
        {
            Assert.False((await ctx.Customers.SingleAsync(x => x.Id == none)).MarketingOk);
            var rey = await ctx.Customers.SingleAsync(x => x.Id == yes);
            Assert.True(rey.MarketingOk);
            Assert.NotNull(rey.MarketingOkAtUtc);
        }

        Assert.Equal(yes, await BookAsync(c, 11, "Rey", "09175550102", false));                    // booked again, box empty
        Assert.Equal(none, await BookAsync(c, 12, "Pia", "09175550101", true));                    // booked again, box ticked: now a yes
        await using var check = c.Db.CreateDbContext();
        Assert.True((await check.Customers.SingleAsync(x => x.Id == yes)).MarketingOk);
        Assert.True((await check.Customers.SingleAsync(x => x.Id == none)).MarketingOk);
    }

    [Fact]
    public async Task The_list_has_only_customers_who_said_yes_and_can_be_reached_and_it_counts_and_marks_sent()
    {
        var c = await SetupAsync();
        await StartPromo(c);
        var pia = await BookAsync(c, 9, "Pia Cruz", "09175550101", true);
        await BookAsync(c, 10, "No Consent", "09175550102", false);
        await BookAsync(c, 11, "Walk In", "", true);                                                // a walk-in with no number and no email: cannot be reached
        await BookAsync(c, 12, "Walk Mail", "", true, "walk@example.com");                          // no number but an email: can

        var view = await c.Messages.GetViewAsync(c.Shop.BusinessId, "Asia/Manila", null);
        Assert.NotNull(view.Promo);
        Assert.Equal(new[] { "Pia Cruz", "Walk Mail" }, view.Recipients.Select(r => r.Name).OrderBy(n => n));
        Assert.Equal((2, 0), (view.Eligible, view.Sent));

        Assert.True((await c.Messages.MarkSentAsync(c.Shop.BusinessId, pia, true)).IsSuccess);
        var after = await c.Messages.GetViewAsync(c.Shop.BusinessId, "Asia/Manila", null);
        Assert.Equal((2, 1), (after.Eligible, after.Sent));
        Assert.Equal("Walk Mail", after.Recipients[0].Name);                                       // those not yet sent come first
        Assert.Single((await c.Messages.GetViewAsync(c.Shop.BusinessId, "Asia/Manila", "pia")).Recipients);
        Assert.Empty((await c.Messages.GetViewAsync(c.Shop.BusinessId, "Asia/Manila", "100%_")).Recipients);   // wildcards are text, not patterns

        Assert.Equal(1, await c.Messages.ResetSentAsync(c.Shop.BusinessId));
        Assert.Equal(0, (await c.Messages.GetViewAsync(c.Shop.BusinessId, "Asia/Manila", null)).Sent);

        await c.Promo.RemoveAsync(c.Shop.BusinessId);
        Assert.Null((await c.Messages.GetViewAsync(c.Shop.BusinessId, "Asia/Manila", null)).Promo);   // no promo showing
    }

    [Fact]
    public async Task Recording_a_yes_or_a_stop_is_audited_stop_clears_the_mark_and_another_shop_is_not_found()
    {
        var c = await SetupAsync();
        var id = await BookAsync(c, 9, "Pia Cruz", "09175550101", false);
        var other = await Seed.CreateShopAsync();

        Assert.Equal(FailureKind.NotFound, (await c.Messages.SetConsentAsync(other.BusinessId, id, true, Staff)).Failure);
        Assert.Equal(FailureKind.NotFound, (await c.Messages.MarkSentAsync(other.BusinessId, id, true)).Failure);
        Assert.Equal(FailureKind.NotFound, (await c.Messages.MarkSentAsync(c.Shop.BusinessId, id, true)).Failure);   // not a yes yet: cannot be marked

        Assert.True((await c.Messages.SetConsentAsync(c.Shop.BusinessId, id, true, Staff)).IsSuccess);
        Assert.True((await c.Messages.MarkSentAsync(c.Shop.BusinessId, id, true)).IsSuccess);
        Assert.True((await c.Messages.SetConsentAsync(c.Shop.BusinessId, id, false, Staff)).IsSuccess);
        Assert.False((await c.Messages.GetConsentAsync(c.Shop.BusinessId, id))!.Value.Allowed);
        await using var ctx = c.Db.CreateDbContext();
        Assert.Null((await ctx.Customers.SingleAsync(x => x.Id == id)).PromoSentAtUtc);
        var actions = await ctx.AuditLogs.Where(a => a.BusinessId == c.Shop.BusinessId && a.Action.StartsWith("PROMO_CONSENT")).OrderBy(a => a.Id).Select(a => a.Action).ToListAsync();
        Assert.Equal(new[] { "PROMO_CONSENT_ON", "PROMO_CONSENT_OFF" }, actions);
    }

    [Theory]
    [InlineData("09175550101", "639175550101")]
    [InlineData("0917 555 0101", "639175550101")]
    [InlineData("+639175550101", "639175550101")]
    [InlineData("639175550101", "639175550101")]
    [InlineData("+14155550123", "14155550123")]
    [InlineData("12345", null)]
    [InlineData("walkin-abc", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void A_number_becomes_the_digits_that_WhatsApp_and_Viber_expect(string? phone, string? expected) =>
        Assert.Equal(expected, DisplayFormat.InternationalDigits(phone));

    [Fact]
    public void The_message_has_the_name_the_promo_the_last_day_the_link_and_a_way_to_stop()
    {
        var text = Text.PromoMessage("Pia", "Ben's", "Haircut week", "Bring a friend", 20, new DateOnly(2030, 3, 31), "https://x.test/b/bens");
        Assert.Contains("Hi Pia!", text);
        Assert.Contains("Ben's has a promo: Haircut week.", text);
        Assert.Contains("Bring a friend.", text);
        Assert.Contains("20% off.", text);
        Assert.Contains("Until Mar 31.", text);
        Assert.Contains("https://x.test/b/bens", text);
        Assert.Contains("Reply STOP", text);
        Assert.DoesNotContain("off.", Text.PromoMessage("Pia", "Ben's", "Hello", null, null, null, "https://x.test"));
    }

    [Fact]
    public async Task The_send_page_lists_the_customers_who_agreed_and_the_history_page_records_a_yes_or_a_stop()
    {
        var (factory, _) = await HttpApp.StartAsync();
        using var _f = factory;
        var owner = HttpApp.NewClient(factory);
        await HttpApp.PostFormAsync(owner, "/Register", new()
        {
            ["Input.BusinessName"] = "Promo Shop", ["Input.OwnerName"] = "Owner", ["Input.Email"] = $"promo{Guid.NewGuid():N}@example.com",
            ["Input.Password"] = "correct-horse-battery", ["Input.Accept"] = "true", ["Input.Industry"] = "barbershop",
        });
        var db = new TestDbFactory(await TestDb.ConnectionStringAsync());
        Guid businessId, yesId, noId;
        await using (var ctx = db.CreateDbContext())
        {
            businessId = await ctx.Businesses.Where(b => b.Name == "Promo Shop").OrderByDescending(b => b.CreatedAtUtc).Select(b => b.Id).FirstAsync();
            var yes = new Customer { BusinessId = businessId, Name = "Pia Agreed", Phone = "09175550101", MarketingOk = true, MarketingOkAtUtc = DateTime.UtcNow };
            var no = new Customer { BusinessId = businessId, Name = "Nina Declined", Phone = "09175550102" };
            ctx.Customers.AddRange(yes, no);
            await ctx.SaveChangesAsync();
            yesId = yes.Id; noId = no.Id;
        }

        var none = WebUtility.HtmlDecode(await owner.GetStringAsync("/Admin/PromoSend"));
        Assert.Contains(Text.PromoSendNoPromo, none);                                               // no promo yet
        Assert.DoesNotContain("Pia Agreed", none);

        await new PromoBannerService(db, TimeProvider.System).SaveAsync(businessId, null, false, "Haircut week", "Bring a friend", null, "plain", true, null, 20);
        var page = WebUtility.HtmlDecode(await owner.GetStringAsync("/Admin/PromoSend"));
        Assert.Contains("Pia Agreed", page);
        Assert.DoesNotContain("Nina Declined", page);                                               // never listed without a yes
        Assert.Contains("Hi Pia! Promo Shop has a promo: Haircut week.", page);
        Assert.Contains("Reply STOP", page);
        Assert.Contains("sms:09175550101", page);
        Assert.Contains("https://wa.me/639175550101?text=Hi%20Pia", page);                         // WhatsApp: this customer's chat, message typed in
        Assert.Contains("viber://forward?text=Hi%20Pia", page);                                     // Viber and Telegram: share screen, message typed in
        Assert.Contains("https://t.me/share/url?url=%20&text=Hi%20Pia", page);
        Assert.Contains("data-share=", page);                                                       // the phone's own share list (Messenger and others)

        var marked = await HttpApp.PostFormAsync(owner, "/Admin/PromoSend?handler=Mark", new() { ["id"] = yesId.ToString(), ["sent"] = "true" }, tokenPage: "/Admin/PromoSend");
        Assert.Equal(HttpStatusCode.Redirect, marked.StatusCode);
        Assert.Contains(Text.PromoSendDone, WebUtility.HtmlDecode(await owner.GetStringAsync("/Admin/PromoSend")));
        var forged = await HttpApp.PostFormAsync(owner, "/Admin/PromoSend?handler=Mark", new() { ["id"] = noId.ToString(), ["sent"] = "true" }, tokenPage: "/Admin/PromoSend");
        Assert.Equal(HttpStatusCode.NotFound, forged.StatusCode);                                   // a customer who did not agree cannot be marked

        var history = WebUtility.HtmlDecode(await owner.GetStringAsync($"/Admin/History/{noId}"));
        Assert.Contains(Text.PromoConsentNotAllowed, history);
        var yesPost = await HttpApp.PostFormAsync(owner, $"/Admin/History/{noId}?handler=PromoConsent", new() { ["allow"] = "true" }, tokenPage: $"/Admin/History/{noId}");
        Assert.Equal(HttpStatusCode.Redirect, yesPost.StatusCode);
        Assert.Contains("Nina Declined", WebUtility.HtmlDecode(await owner.GetStringAsync("/Admin/PromoSend")));
        await HttpApp.PostFormAsync(owner, $"/Admin/History/{noId}?handler=PromoConsent", new() { ["allow"] = "false" }, tokenPage: $"/Admin/History/{noId}");
        Assert.DoesNotContain("Nina Declined", WebUtility.HtmlDecode(await owner.GetStringAsync("/Admin/PromoSend")));

        Assert.Contains("/Admin/PromoSend", WebUtility.HtmlDecode(await owner.GetStringAsync("/Admin/Promo")));
    }
}
