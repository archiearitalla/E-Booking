using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using VeskaFlow.Web;
using VeskaFlow.Web.Data;
using VeskaFlow.Web.Services;

namespace VeskaFlow.Web.Tests;

/// <summary>The automatic promo email: who gets it, the daily limit, stopping by itself, and the unsubscribe link.</summary>
[Collection("http")]
public class PromoEmailTests
{
    private static readonly BookingActor Manager = BookingActor.Staff(Guid.NewGuid(), "Ana (manager)");

    private sealed class RecordingSender(bool accept = true) : IEmailSender
    {
        public bool IsConfigured => true;
        public bool Accept { get; set; } = accept;
        public List<(string To, string Subject, string Body)> Sent { get; } = [];
        public Task<bool> SendAsync(string to, string subject, string textBody, string? htmlBody = null, CancellationToken ct = default)
        {
            if (Accept) Sent.Add((to, subject, textBody));
            return Task.FromResult(Accept);
        }
    }

    private sealed record Ctx(TestDbFactory Db, TestClock Clock, PromoEmailService Emails, PromoBannerService Promo, RecordingSender Sender, Shop Shop);

    private static async Task<Ctx> SetupAsync(int freeShopMax = 30, bool withPromo = true)
    {
        var db = new TestDbFactory(await TestDb.ConnectionStringAsync());
        var clock = new TestClock(Seed.BaseNow);
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            { ["App:PublicBaseUrl"] = "https://flow.test", ["Email:FreeShopDailyMax"] = freeShopMax.ToString(), ["Email:DailyLimit"] = "1000" }).Build();
        var promo = new PromoBannerService(db, clock);
        var sender = new RecordingSender();
        var emails = new PromoEmailService(sender, new EmailQuota(db, clock, config), db, promo, clock, config, NullLogger<PromoEmailService>.Instance);
        var shop = await Seed.CreateShopAsync();
        if (withPromo) Assert.True((await promo.SaveAsync(shop.BusinessId, null, false, "Haircut week", "Bring a friend", null, "plain", true, null, 20)).IsSuccess);
        return new Ctx(db, clock, emails, promo, sender, shop);
    }

    private static async Task<Guid> AddCustomerAsync(Ctx c, string name, string? email, bool consent, string? phone = null)
    {
        await using var ctx = c.Db.CreateDbContext();
        var customer = new Customer { BusinessId = c.Shop.BusinessId, Name = name, Phone = phone ?? "0917" + Random.Shared.Next(1000000, 9999999), Email = email, MarketingOk = consent };
        ctx.Customers.Add(customer);
        await ctx.SaveChangesAsync();
        return customer.Id;
    }

    private static async Task<List<string>> AuditAsync(Ctx c)
    {
        await using var ctx = c.Db.CreateDbContext();
        return await ctx.AuditLogs.Where(a => a.BusinessId == c.Shop.BusinessId && a.Action.StartsWith("PROMO_EMAIL")).OrderBy(a => a.Id).Select(a => a.Action).ToListAsync();
    }

    [Fact]
    public async Task It_emails_only_customers_who_agreed_and_have_an_email_once_each_with_an_unsubscribe_link_and_then_stops_by_itself()
    {
        var c = await SetupAsync();
        var pia = await AddCustomerAsync(c, "Pia Cruz", "pia@example.com", true);
        await AddCustomerAsync(c, "No Consent", "no@example.com", false);
        await AddCustomerAsync(c, "No Email", null, true);
        await AddCustomerAsync(c, "Bad Email", "not an email", true);

        var started = await c.Emails.StartAsync(c.Shop.BusinessId, "Asia/Manila", Manager);
        Assert.True(started.IsSuccess, started.Message);
        Assert.Equal(1, started.Value);
        Assert.True((await c.Emails.StatusAsync(c.Shop.BusinessId)).Queued);

        Assert.Equal(1, await c.Emails.RunAsync());
        var mail = Assert.Single(c.Sender.Sent);
        Assert.Equal("pia@example.com", mail.To);
        Assert.Contains("Hi Pia!", mail.Body);
        Assert.Contains("Haircut week", mail.Body);
        Assert.Contains("20% off", mail.Body);
        Assert.Contains("https://flow.test/b/", mail.Body);
        await using (var ctx = c.Db.CreateDbContext())
        {
            var row = await ctx.Customers.SingleAsync(x => x.Id == pia);
            Assert.NotNull(row.PromoSentAtUtc);
            Assert.False(string.IsNullOrEmpty(row.MarketingToken));
            Assert.Contains($"https://flow.test/Unsubscribe/{row.MarketingToken}", mail.Body);        // the link in the email is this customer's
        }

        Assert.Equal(0, await c.Emails.RunAsync());                                                     // nobody is sent it twice, and it finishes
        Assert.Single(c.Sender.Sent);
        Assert.False((await c.Emails.StatusAsync(c.Shop.BusinessId)).Queued);
        Assert.Equal(new[] { "PROMO_EMAIL_STARTED", "PROMO_EMAIL_DONE" }, await AuditAsync(c));
    }

    [Fact]
    public async Task It_stays_inside_the_shops_daily_limit_and_goes_on_the_next_day()
    {
        var c = await SetupAsync(freeShopMax: 2);
        for (var i = 1; i <= 5; i++) await AddCustomerAsync(c, $"Customer {i}", $"c{i}@example.com", true);
        Assert.True((await c.Emails.StartAsync(c.Shop.BusinessId, "Asia/Manila", Manager)).IsSuccess);

        Assert.Equal(2, await c.Emails.RunAsync());
        Assert.Equal(0, await c.Emails.RunAsync());                                                     // the day's room is used up
        Assert.True((await c.Emails.StatusAsync(c.Shop.BusinessId)).Queued);
        c.Clock.Now = c.Clock.Now.AddDays(1);
        Assert.Equal(2, await c.Emails.RunAsync());
        c.Clock.Now = c.Clock.Now.AddDays(1);
        Assert.Equal(1, await c.Emails.RunAsync());
        Assert.Equal(5, c.Sender.Sent.Select(m => m.To).Distinct().Count());
        await c.Emails.RunAsync();
        Assert.False((await c.Emails.StatusAsync(c.Shop.BusinessId)).Queued);
    }

    [Fact]
    public async Task It_stops_when_the_promo_ends_or_the_shop_turns_customer_emails_off_and_skips_someone_who_just_stopped()
    {
        var c = await SetupAsync();
        var a = await AddCustomerAsync(c, "Ana", "a@example.com", true);
        await AddCustomerAsync(c, "Ben", "b@example.com", true);
        Assert.True((await c.Emails.StartAsync(c.Shop.BusinessId, "Asia/Manila", Manager)).IsSuccess);
        await using (var ctx = c.Db.CreateDbContext()) await ctx.Customers.Where(x => x.Id == a).ExecuteUpdateAsync(u => u.SetProperty(x => x.MarketingOk, false));   // Ana says stop
        Assert.Equal(1, await c.Emails.RunAsync());
        Assert.Equal("b@example.com", Assert.Single(c.Sender.Sent).To);

        var c2 = await SetupAsync();
        await AddCustomerAsync(c2, "Cara", "c@example.com", true);
        Assert.True((await c2.Emails.StartAsync(c2.Shop.BusinessId, "Asia/Manila", Manager)).IsSuccess);
        await c2.Promo.RemoveAsync(c2.Shop.BusinessId);                                                 // the promo is gone
        Assert.Equal(0, await c2.Emails.RunAsync());
        Assert.Empty(c2.Sender.Sent);
        Assert.False((await c2.Emails.StatusAsync(c2.Shop.BusinessId)).Queued);
        Assert.Contains("PROMO_EMAIL_ENDED", await AuditAsync(c2));

        var c3 = await SetupAsync();
        await AddCustomerAsync(c3, "Dan", "d@example.com", true);
        Assert.True((await c3.Emails.StartAsync(c3.Shop.BusinessId, "Asia/Manila", Manager)).IsSuccess);
        await using (var ctx = c3.Db.CreateDbContext()) await ctx.Businesses.Where(b => b.Id == c3.Shop.BusinessId).ExecuteUpdateAsync(u => u.SetProperty(b => b.CustomerEmails, false));
        Assert.Equal(0, await c3.Emails.RunAsync());
        Assert.Empty(c3.Sender.Sent);
    }

    [Fact]
    public async Task Starting_needs_a_promo_the_switch_and_someone_to_send_to_and_a_refused_email_is_tried_again()
    {
        var none = await SetupAsync(withPromo: false);
        await AddCustomerAsync(none, "Eve", "e@example.com", true);
        Assert.Equal(Text.PromoEmailNeedPromo, (await none.Emails.StartAsync(none.Shop.BusinessId, "Asia/Manila", Manager)).Message);

        var nobody = await SetupAsync();
        await AddCustomerAsync(nobody, "No Consent", "n@example.com", false);
        Assert.Equal(Text.PromoEmailNobody, (await nobody.Emails.StartAsync(nobody.Shop.BusinessId, "Asia/Manila", Manager)).Message);

        var c = await SetupAsync();
        var id = await AddCustomerAsync(c, "Fay", "f@example.com", true);
        await using (var ctx = c.Db.CreateDbContext()) await ctx.Businesses.Where(b => b.Id == c.Shop.BusinessId).ExecuteUpdateAsync(u => u.SetProperty(b => b.CustomerEmails, false));
        Assert.Equal(Text.PromoEmailSwitchedOff, (await c.Emails.StartAsync(c.Shop.BusinessId, "Asia/Manila", Manager)).Message);
        await using (var ctx = c.Db.CreateDbContext()) await ctx.Businesses.Where(b => b.Id == c.Shop.BusinessId).ExecuteUpdateAsync(u => u.SetProperty(b => b.CustomerEmails, true));

        c.Sender.Accept = false;                                                                        // the email service refuses
        Assert.True((await c.Emails.StartAsync(c.Shop.BusinessId, "Asia/Manila", Manager)).IsSuccess);
        Assert.Equal(0, await c.Emails.RunAsync());
        await using (var ctx = c.Db.CreateDbContext()) Assert.Null((await ctx.Customers.SingleAsync(x => x.Id == id)).PromoSentAtUtc);   // not marked sent
        Assert.True((await c.Emails.StatusAsync(c.Shop.BusinessId)).Queued);
        c.Sender.Accept = true;
        Assert.Equal(1, await c.Emails.RunAsync());

        await c.Emails.StopAsync(c.Shop.BusinessId, Manager);                                           // stop when nothing is queued does nothing
        Assert.DoesNotContain("PROMO_EMAIL_STOPPED", await AuditAsync(c));
    }

    [Fact]
    public async Task The_unsubscribe_link_stops_promo_messages_is_audited_and_is_safe_to_open_again_or_to_guess()
    {
        var c = await SetupAsync();
        var id = await AddCustomerAsync(c, "Gil Reyes", "g@example.com", true);
        Assert.True((await c.Emails.StartAsync(c.Shop.BusinessId, "Asia/Manila", Manager)).IsSuccess);
        await c.Emails.RunAsync();
        string token;
        await using (var ctx = c.Db.CreateDbContext()) token = (await ctx.Customers.SingleAsync(x => x.Id == id)).MarketingToken!;

        var found = await c.Emails.LookupAsync(token);
        Assert.Equal("Gil Reyes", found!.Value.Customer);
        Assert.True(await c.Emails.UnsubscribeAsync(token));
        Assert.True(await c.Emails.UnsubscribeAsync(token));                                            // the second time changes nothing
        Assert.False(await c.Emails.UnsubscribeAsync("short"));
        Assert.False(await c.Emails.UnsubscribeAsync(new string('x', 43)));
        Assert.Null(await c.Emails.LookupAsync(null));

        await using var check = c.Db.CreateDbContext();
        var row = await check.Customers.SingleAsync(x => x.Id == id);
        Assert.False(row.MarketingOk);
        Assert.Null(row.PromoSentAtUtc);
        Assert.Equal(1, await check.AuditLogs.CountAsync(a => a.BusinessId == c.Shop.BusinessId && a.Action == "PROMO_CONSENT_OFF" && a.ActorType == "CUSTOMER"));
    }

    [Fact]
    public async Task The_unsubscribe_page_and_the_send_page_card_work_over_HTTP()
    {
        var (factory, client) = await HttpApp.StartAsync();
        using var _f = factory;
        var db = new TestDbFactory(await TestDb.ConnectionStringAsync());
        var owner = HttpApp.NewClient(factory);
        await HttpApp.PostFormAsync(owner, "/Register", new()
        {
            ["Input.BusinessName"] = "Mail Shop", ["Input.OwnerName"] = "Owner", ["Input.Email"] = $"mail{Guid.NewGuid():N}@example.com",
            ["Input.Password"] = "correct-horse-battery", ["Input.Accept"] = "true", ["Input.Industry"] = "barbershop",
        });
        var token = Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32)).Replace('+', '-').Replace('/', '_').TrimEnd('=');
        Guid businessId;
        await using (var ctx = db.CreateDbContext())
        {
            businessId = await ctx.Businesses.Where(b => b.Name == "Mail Shop").OrderByDescending(b => b.CreatedAtUtc).Select(b => b.Id).FirstAsync();
            ctx.Customers.Add(new Customer { BusinessId = businessId, Name = "Hal Cruz", Phone = "09175550177", Email = "h@example.com", MarketingOk = true, MarketingToken = token });
            await ctx.SaveChangesAsync();
        }

        var page = WebUtility.HtmlDecode(await client.GetStringAsync($"/Unsubscribe/{token}"));
        Assert.Contains("Mail Shop", page);
        Assert.Contains(Text.UnsubscribeButton, page);
        Assert.DoesNotContain("Hal Cruz", page);                                                        // nothing about the person is shown
        Assert.Contains(Text.UnsubscribeUnknown, WebUtility.HtmlDecode(await client.GetStringAsync($"/Unsubscribe/{new string('z', 43)}")));

        var posted = await HttpApp.PostFormAsync(client, $"/Unsubscribe/{token}", new(), tokenPage: $"/Unsubscribe/{token}");
        Assert.Contains(Text.UnsubscribeDone, WebUtility.HtmlDecode(await posted.Content.ReadAsStringAsync()));
        await using (var check = db.CreateDbContext()) Assert.False((await check.Customers.SingleAsync(x => x.MarketingToken == token)).MarketingOk);

        await new PromoBannerService(db, TimeProvider.System).SaveAsync(businessId, null, false, "Haircut week", null, null, "plain", true, null, null);
        var card = WebUtility.HtmlDecode(await owner.GetStringAsync("/Admin/PromoSend"));
        Assert.Contains(Text.PromoEmailCardTitle, card);
        Assert.Contains(Text.PromoEmailNoService, card);                                                // no email service in the test app
    }
}
