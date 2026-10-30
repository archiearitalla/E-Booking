using System.Net;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using VeskaFlow.Core.Domain;
using VeskaFlow.Web.Services;

namespace VeskaFlow.Web.Tests;

/// <summary>Automatic customer emails (confirmation and reminder) and the daily limit that stops them.</summary>
[Collection("http")]
public class CustomerEmailTests
{
    private static string Id(string prefix) => prefix + Guid.NewGuid().ToString("N")[..10];

    private static IConfiguration Config(int dailyLimit = 300, int ownerReserve = 40, int paidReserve = 100, int freeShopMax = 30) =>
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Email:DailyLimit"] = dailyLimit.ToString(), ["Email:OwnerReserve"] = ownerReserve.ToString(),
            ["Email:PaidReserve"] = paidReserve.ToString(), ["Email:FreeShopDailyMax"] = freeShopMax.ToString(),
        }).Build();

    private sealed record Ctx(TestDbFactory Db, TestClock Clock, EmailQuota Quota, CapturingEmailSender Sender, CustomerMailer Mailer);

    /// <summary>The test database is shared and the day is the clock's day, so the day's counts are cleared first (these tests run one at a time).</summary>
    private static async Task<Ctx> SetupAsync(IConfiguration? config = null)
    {
        var db = new TestDbFactory(await TestDb.ConnectionStringAsync());
        var clock = new TestClock(Seed.BaseNow);
        await using (var ctx = db.CreateDbContext())
        {
            var day = DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime);
            await ctx.EmailDailies.Where(e => e.Day == day).ExecuteDeleteAsync();
        }
        var quota = new EmailQuota(db, clock, config ?? Config());
        var sender = new CapturingEmailSender();
        return new Ctx(db, clock, quota, sender, new CustomerMailer(sender, quota, db, NullLogger<CustomerMailer>.Instance));
    }

    private static async Task<(VeskaFlow.Web.Tests.Shop Shop, BookingSummary Booking)> BookingAsync(Ctx c, string? email, int hour = 10, Guid? shopId = null)
    {
        var shop = await Seed.CreateShopAsync();
        var bookings = new BookingService(c.Db, c.Clock);
        var request = Seed.Request(shop, Seed.Manila(5, hour)) with { Email = email };
        return (shop, (await bookings.CreateAsync(request)).Value!.Booking);
    }

    [Fact]
    public async Task A_confirmation_is_emailed_once_with_the_details_and_the_manage_link_and_is_counted()
    {
        var c = await SetupAsync();
        var (shop, booking) = await BookingAsync(c, "maria@example.com");

        Assert.Equal(MailResult.Sent, await c.Mailer.SendConfirmationAsync(shop.BusinessId, booking.Id, "https://flow.example.com/"));
        var mail = Assert.Single(c.Sender.Sent);
        Assert.Equal("maria@example.com", mail.To);
        Assert.Contains("Test Shop", mail.Subject);
        Assert.Contains($"https://flow.example.com/booking/{booking.ManageToken}", mail.Body);
        Assert.Contains("Haircut", mail.Body);
        Assert.Contains("Please do not reply", mail.Body);
        Assert.Equal(1, await c.Quota.TodayTotalAsync());
    }

    [Fact]
    public async Task Nothing_is_sent_without_an_email_a_switch_a_confirmed_booking_or_an_email_service()
    {
        var c = await SetupAsync();
        var (noEmailShop, noEmail) = await BookingAsync(c, null);
        Assert.Equal(MailResult.NotSent, await c.Mailer.SendConfirmationAsync(noEmailShop.BusinessId, noEmail.Id, "https://x.example.com"));

        var (offShop, off) = await BookingAsync(c, "off@example.com");
        await using (var ctx = c.Db.CreateDbContext())
            await ctx.Businesses.Where(b => b.Id == offShop.BusinessId).ExecuteUpdateAsync(u => u.SetProperty(b => b.CustomerEmails, false));
        Assert.Equal(MailResult.NotSent, await c.Mailer.SendConfirmationAsync(offShop.BusinessId, off.Id, "https://x.example.com"));

        var (cancelShop, cancelled) = await BookingAsync(c, "cancelled@example.com");
        await using (var ctx = c.Db.CreateDbContext())
            await ctx.Bookings.Where(b => b.Id == cancelled.Id).ExecuteUpdateAsync(u => u.SetProperty(b => b.Status, BookingStatus.Cancelled));
        Assert.Equal(MailResult.NotSent, await c.Mailer.SendConfirmationAsync(cancelShop.BusinessId, cancelled.Id, "https://x.example.com"));

        var (otherShop, other) = await BookingAsync(c, "other@example.com");
        Assert.Equal(MailResult.NotSent, await c.Mailer.SendConfirmationAsync(Guid.NewGuid(), other.Id, "https://x.example.com")); // another shop's id
        var noService = new CustomerMailer(new NoEmailSender(), c.Quota, c.Db, NullLogger<CustomerMailer>.Instance);
        Assert.Equal(MailResult.NotSent, await noService.SendConfirmationAsync(otherShop.BusinessId, other.Id, "https://x.example.com"));
        Assert.Empty(c.Sender.Sent);
        Assert.Equal(0, await c.Quota.TodayTotalAsync());
    }

    [Fact]
    public async Task A_shop_without_the_add_on_stops_at_its_share_and_one_with_it_goes_on_until_the_pool_is_used()
    {
        var c = await SetupAsync(Config(dailyLimit: 20, ownerReserve: 4, paidReserve: 6, freeShopMax: 3)); // customers 16, free pool 10
        var (freeShop, free) = await BookingAsync(c, "free@example.com");
        var (paidShop, paid) = await BookingAsync(c, "paid@example.com");
        await new AccountControlService(c.Db, c.Clock, new ConfigurationBuilder().Build(), new NoEmailSender()).ExtendEmailAddonAsync(paidShop.BusinessId, 30);

        for (var i = 0; i < 3; i++)
            Assert.Equal(MailResult.Sent, await c.Mailer.SendConfirmationAsync(freeShop.BusinessId, free.Id, "https://x.example.com"));
        Assert.Equal(MailResult.ShopLimit, await c.Mailer.SendConfirmationAsync(freeShop.BusinessId, free.Id, "https://x.example.com")); // its share is used

        // The paying shop is not held back by the free shops, up to what is left of the day (16 for customers).
        var sentByPaid = 0;
        MailResult last;
        do
        {
            last = await c.Mailer.SendConfirmationAsync(paidShop.BusinessId, paid.Id, "https://x.example.com");
            if (last == MailResult.Sent) sentByPaid++;
        } while (last == MailResult.Sent && sentByPaid < 50);
        Assert.Equal(MailResult.PoolLimit, last);
        Assert.Equal(13, sentByPaid); // 16 for customers minus the 3 the free shop used
        Assert.Equal(16, await c.Quota.TodayTotalAsync()); // never past the customer cap
    }

    [Fact]
    public async Task The_owners_own_emails_are_counted_and_keep_their_reserve_when_customers_stop()
    {
        var c = await SetupAsync(Config(dailyLimit: 10, ownerReserve: 4, paidReserve: 0, freeShopMax: 100)); // customers may use 6
        var counted = new CountedEmailSender(new CapturingEmailSender(), c.Quota);
        Assert.True(await counted.SendAsync("owner@example.com", "Hello", "Body"));
        Assert.Equal(1, await c.Quota.TodayTotalAsync()); // the system's own emails count

        var (shop, booking) = await BookingAsync(c, "late@example.com");
        var results = new List<MailResult>();
        for (var i = 0; i < 8; i++) results.Add(await c.Mailer.SendConfirmationAsync(shop.BusinessId, booking.Id, "https://x.example.com"));
        Assert.Equal(5, results.Count(r => r == MailResult.Sent)); // 6 for customers, 1 already used by the owner email
        Assert.Contains(MailResult.PoolLimit, results);

        // The reserve is still there for the owners.
        for (var i = 0; i < 4; i++) Assert.True(await counted.SendAsync("owner@example.com", "Hello", "Body"));
        Assert.Equal(10, await c.Quota.TodayTotalAsync());
    }

    [Fact]
    public async Task The_reminder_goes_by_email_once_waits_for_a_new_booking_and_leaves_the_manual_list()
    {
        var c = await SetupAsync();
        var (shop, booking) = await BookingAsync(c, "remind@example.com", hour: 10);
        var plain = await Seed.CreateShopAsync();
        var plainBooking = (await new BookingService(c.Db, c.Clock).CreateAsync(Seed.Request(plain, Seed.Manila(5, 11)))).Value!.Booking; // no email
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["App:PublicBaseUrl"] = "https://flow.example.com" }).Build();
        var auto = new AutoReminderService(c.Db, c.Clock, c.Mailer, config);

        c.Clock.Now = new DateTimeOffset(Seed.Manila(4, 18), TimeSpan.Zero); // about 16 hours before, and made hours ago
        Assert.True(await auto.SendDueAsync() >= 1);
        var mail = Assert.Single(c.Sender.Sent, m => m.To == "remind@example.com");
        Assert.Contains("reminder", mail.Body, StringComparison.OrdinalIgnoreCase);
        Assert.Contains($"/booking/{booking.ManageToken}", mail.Body);

        await using var check = c.Db.CreateDbContext();
        Assert.NotNull((await check.Bookings.AsNoTracking().SingleAsync(b => b.Id == booking.Id)).RemindedAtUtc); // off the manual list
        Assert.Null((await check.Bookings.AsNoTracking().SingleAsync(b => b.Id == plainBooking.Id)).RemindedAtUtc); // no email: still manual

        var before = c.Sender.Sent.Count;
        await auto.SendDueAsync();
        Assert.Equal(before, c.Sender.Sent.Count); // once

        // A booking made less than three hours ago has just had its confirmation; the reminder waits.
        var (_, fresh) = await BookingAsync(c, "fresh@example.com", hour: 12);
        await check.Bookings.Where(b => b.Id == fresh.Id).ExecuteUpdateAsync(u => u.SetProperty(b => b.CreatedAtUtc, c.Clock.GetUtcNow().UtcDateTime.AddHours(-1)));
        await auto.SendDueAsync();
        Assert.DoesNotContain(c.Sender.Sent, m => m.To == "fresh@example.com");

        // Without a public address there is no manage link to send, so nothing goes out.
        Assert.Equal(0, await new AutoReminderService(c.Db, c.Clock, c.Mailer, new ConfigurationBuilder().Build()).SendDueAsync());
    }

    [Fact]
    public async Task The_platform_owner_adds_days_to_the_email_add_on_from_the_later_of_today_and_its_end()
    {
        var c = await SetupAsync();
        var shop = await Seed.CreateShopAsync();
        var accounts = new AccountControlService(c.Db, c.Clock, new ConfigurationBuilder().Build(), new NoEmailSender());
        var now = c.Clock.GetUtcNow().UtcDateTime;

        Assert.Equal(now.AddDays(30), (await accounts.ExtendEmailAddonAsync(shop.BusinessId, 30)).Value, TimeSpan.FromSeconds(1));
        Assert.Equal(now.AddDays(37), (await accounts.ExtendEmailAddonAsync(shop.BusinessId, 7)).Value, TimeSpan.FromSeconds(1)); // from its end
        c.Clock.Now = c.Clock.Now.AddDays(100);
        Assert.Equal(c.Clock.GetUtcNow().UtcDateTime.AddDays(7), (await accounts.ExtendEmailAddonAsync(shop.BusinessId, 7)).Value, TimeSpan.FromSeconds(1)); // lapsed: from today
        Assert.Equal(FailureKind.Validation, (await accounts.ExtendEmailAddonAsync(shop.BusinessId, 5)).Failure);
        Assert.Equal(FailureKind.NotFound, (await accounts.ExtendEmailAddonAsync(Guid.NewGuid(), 30)).Failure);
    }


    [Fact]
    public async Task Booking_online_with_an_email_sends_a_confirmation_and_the_settings_switch_stops_it()
    {
        Environment.SetEnvironmentVariable("Email__DailyLimit", "100000"); // the test database is shared and the real day carries over
        Environment.SetEnvironmentVariable("Email__FreeShopDailyMax", "1000");
        try
        {
            var (factory, _) = await HttpApp.StartAsync();
            using var _f = factory;
            var sender = new CapturingEmailSender();
            var client = factory.WithWebHostBuilder(b => b.ConfigureServices(s =>
            {
                s.AddKeyedSingleton<IEmailSender>("raw", sender);
                s.AddSingleton<IEmailSender>(sender);
            })).CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = true });

            var db = new TestDbFactory(await TestDb.ConnectionStringAsync());
            var slug = Id("ce");
            var owner = (await new OnboardingService(db, TimeProvider.System).RegisterAsync(
                new RegisterRequest("Email Shop", slug, "Owner", $"{Id("o")}@example.com", "correct-horse-1", TrialDays: 30))).Value!;
            await using var ctx = db.CreateDbContext();
            var serviceId = await ctx.Services.Where(s => s.BusinessId == owner.BusinessId && !s.IsOther).Select(s => s.Id).SingleAsync();
            var bookings = new BookingService(db, TimeProvider.System);
            var day = Seed.NextOpenDay();
            var starts = (await bookings.GetFreeStartsAsync(owner.BusinessId, serviceId, null, day)).Value!;

            async Task<HttpResponseMessage> Book(DateTime start, string email, string phone)
            {
                var url = $"/b/{slug}/confirm?serviceId={serviceId}&start={DisplayFormat.StartParam(start)}";
                var page = await client.GetStringAsync(url);
                var fields = new Dictionary<string, string>();
                foreach (System.Text.RegularExpressions.Match tag in System.Text.RegularExpressions.Regex.Matches(page, "<input[^>]*>"))
                {
                    if (!System.Text.RegularExpressions.Regex.IsMatch(tag.Value, "type=\"hidden\"")) continue;
                    fields[System.Text.RegularExpressions.Regex.Match(tag.Value, "name=\"([^\"]*)\"").Groups[1].Value] =
                        WebUtility.HtmlDecode(System.Text.RegularExpressions.Regex.Match(tag.Value, "value=\"([^\"]*)\"").Groups[1].Value);
                }
                fields["Input.Name"] = "Maria Santos";
                fields["Input.Phone"] = phone;
                fields["Input.Email"] = email;
                return await client.PostAsync($"/b/{slug}/confirm", new FormUrlEncodedContent(fields));
            }

            var first = await Book(starts[0], "maria@example.com", "09175550701");
            Assert.Equal(HttpStatusCode.Redirect, first.StatusCode);
            var mail = Assert.Single(sender.Sent, m => m.To == "maria@example.com");
            Assert.Contains("Email Shop", mail.Subject);

            var noEmail = await Book(starts[2], "", "09175550702");
            Assert.Equal(HttpStatusCode.Redirect, noEmail.StatusCode);
            Assert.Single(sender.Sent, m => m.To == "maria@example.com"); // nothing more

            await ctx.Businesses.Where(b => b.Id == owner.BusinessId).ExecuteUpdateAsync(u => u.SetProperty(b => b.CustomerEmails, false));
            var off = await Book(starts[4], "second@example.com", "09175550703");
            Assert.Equal(HttpStatusCode.Redirect, off.StatusCode);
            Assert.DoesNotContain(sender.Sent, m => m.To == "second@example.com"); // the shop switched it off
        }
        finally
        {
            Environment.SetEnvironmentVariable("Email__DailyLimit", null);
            Environment.SetEnvironmentVariable("Email__FreeShopDailyMax", null);
        }
    }
}
