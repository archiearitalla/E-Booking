using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using VeskaFlow.Web.Data;
using VeskaFlow.Web.Services;

namespace VeskaFlow.Web.Tests;

/// <summary>The waitlist (BUSINESS_RULES 7.3): joining only a full day, tenant and staff scoping, expiry, and the one email when a time opens.</summary>
[Collection("http")]
public class WaitlistTests
{
    private static readonly DateOnly Day = new(2030, 3, 5); // Tuesday; the test clock starts on Monday 2030-03-04 08:00 in Manila

    private static IConfiguration Config(bool baseUrl = true, int dailyLimit = 300, int ownerReserve = 40, int paidReserve = 100, int freeShopMax = 30)
    {
        var values = new Dictionary<string, string?>
        {
            ["Email:DailyLimit"] = dailyLimit.ToString(), ["Email:OwnerReserve"] = ownerReserve.ToString(),
            ["Email:PaidReserve"] = paidReserve.ToString(), ["Email:FreeShopDailyMax"] = freeShopMax.ToString(),
        };
        if (baseUrl) values["App:PublicBaseUrl"] = "https://flow.example.com";
        return new ConfigurationBuilder().AddInMemoryCollection(values).Build();
    }

    private sealed record Ctx(TestDbFactory Db, TestClock Clock, WaitlistService Waitlist, ShopCatalogService Catalog, CapturingEmailSender Sender, EmailQuota Quota);

    private static async Task<Ctx> SetupAsync(IConfiguration? config = null)
    {
        var db = new TestDbFactory(await TestDb.ConnectionStringAsync());
        var clock = new TestClock(Seed.BaseNow);
        config ??= Config();
        await using (var ctx = db.CreateDbContext())
        {
            var day = DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime);
            await ctx.EmailDailies.Where(e => e.Day == day).ExecuteDeleteAsync(); // the day's counts are shared by the tests, which run one at a time
            await ctx.WaitlistEntries.ExecuteDeleteAsync();                       // the run is for every shop, so earlier tests' entries must not be there
        }
        var quota = new EmailQuota(db, clock, config);
        var sender = new CapturingEmailSender();
        var booking = new BookingService(db, clock);
        var mailer = new CustomerMailer(sender, quota, db, NullLogger<CustomerMailer>.Instance);
        return new Ctx(db, clock, new WaitlistService(db, clock, booking, mailer, config), new ShopCatalogService(db, clock), sender, quota);
    }

    /// <summary>A shop whose Tuesday to Saturday are closed by one whole-shop closure, so those days are full. Returns the shop, its slug and the closure's id.</summary>
    private static async Task<(Shop Shop, string Slug, Guid ClosureId)> FullShopAsync(Ctx c, int staffCount = 1)
    {
        var shop = await Seed.CreateShopAsync(staffCount);
        await using var ctx = c.Db.CreateDbContext();
        var closure = new Blackout { BusinessId = shop.BusinessId, StaffId = null, StartsAtUtc = Seed.Manila(5, 0), EndsAtUtc = Seed.Manila(10, 0), Reason = "test" };
        ctx.Blackouts.Add(closure);
        await ctx.SaveChangesAsync();
        var slug = await ctx.Businesses.Where(b => b.Id == shop.BusinessId).Select(b => b.Slug).SingleAsync();
        return (shop, slug, closure.Id);
    }

    private static async Task OpenUpAsync(Ctx c, Guid closureId)
    {
        await using var ctx = c.Db.CreateDbContext();
        await ctx.Blackouts.Where(b => b.Id == closureId).ExecuteDeleteAsync();
    }

    private static JoinWaitlistRequest Join(Shop shop, DateOnly? day = null, string phone = "09171234567", string? email = null, Guid? staffId = null, string name = "Maria Santos") =>
        new(shop.ServiceId, staffId, day ?? Day, name, phone, email);

    [Fact]
    public async Task A_person_can_join_only_a_full_day_with_a_valid_service_staff_day_and_contact()
    {
        var c = await SetupAsync();
        var (shop, slug, closure) = await FullShopAsync(c);
        var pub = (await c.Catalog.GetBySlugAsync(slug))!;

        // Still has free times on a day without the closure: they should book instead.
        var open = await c.Waitlist.JoinAsync(pub, Join(shop, new DateOnly(2030, 3, 11)));
        Assert.False(open.IsSuccess);
        Assert.Equal(FailureKind.PolicyViolation, open.Failure);
        Assert.Equal(Text.WaitlistSlotsFree, open.Message);

        var ok = await c.Waitlist.JoinAsync(pub, Join(shop, email: "maria@example.com"));
        Assert.True(ok.IsSuccess);
        await using (var ctx = c.Db.CreateDbContext())
        {
            var row = await ctx.WaitlistEntries.AsNoTracking().SingleAsync(e => e.BusinessId == shop.BusinessId);
            Assert.Equal(("Maria Santos", "maria@example.com", Day, shop.ServiceId), (row.CustomerName, row.CustomerEmail, row.Day, row.ServiceId));
            Assert.Null(row.StaffId);
            Assert.Null(row.NotifiedAtUtc);
        }

        // A bad contact, a day outside the booking range, a service or staff that is not this shop's, "Other", and a shop that takes no bookings.
        Assert.Equal(FailureKind.Validation, (await c.Waitlist.JoinAsync(pub, Join(shop, phone: "123"))).Failure);
        Assert.Equal(FailureKind.Validation, (await c.Waitlist.JoinAsync(pub, Join(shop, name: " "))).Failure);
        Assert.Equal(FailureKind.Validation, (await c.Waitlist.JoinAsync(pub, Join(shop, email: "not an email"))).Failure);
        Assert.Equal(Text.WaitlistDayInvalid, (await c.Waitlist.JoinAsync(pub, Join(shop, new DateOnly(2030, 3, 3)))).Message);           // yesterday
        Assert.Equal(Text.WaitlistDayInvalid, (await c.Waitlist.JoinAsync(pub, Join(shop, Day.AddDays(pub.HorizonDays + 5)))).Message);  // beyond the horizon
        Assert.Equal(FailureKind.NotFound, (await c.Waitlist.JoinAsync(pub, Join(shop) with { ServiceId = Guid.NewGuid() })).Failure);
        Assert.Equal(FailureKind.NotFound, (await c.Waitlist.JoinAsync(pub, Join(shop, staffId: Guid.NewGuid()))).Failure);
        Assert.Equal(FailureKind.NotFound, (await c.Waitlist.JoinAsync(pub with { AcceptingBookings = false }, Join(shop))).Failure);
        var other = pub with { Services = [.. pub.Services, new PublicService(Guid.NewGuid(), "Other", 30, 0, IsOther: true)] };
        Assert.Equal(FailureKind.NotFound, (await c.Waitlist.JoinAsync(other, Join(shop) with { ServiceId = other.Services[^1].Id })).Failure);

        // Another shop's id inside this shop's request finds nothing (the shop comes from the page's slug, never from the form).
        var (otherShop, _, _) = await FullShopAsync(c);
        Assert.Equal(FailureKind.NotFound, (await c.Waitlist.JoinAsync(pub, Join(otherShop))).Failure);
        _ = closure;
    }

    [Fact]
    public async Task The_same_request_twice_is_one_entry_and_a_person_and_a_shop_have_a_limit()
    {
        var c = await SetupAsync();
        var (shop, slug, _) = await FullShopAsync(c);
        var pub = (await c.Catalog.GetBySlugAsync(slug))!;

        Assert.True((await c.Waitlist.JoinAsync(pub, Join(shop, phone: "+63 917 123 4567"))).IsSuccess);
        Assert.True((await c.Waitlist.JoinAsync(pub, Join(shop, phone: "0917-123-4567"))).IsSuccess);                       // the same number typed another way: still one entry
        await using (var ctx = c.Db.CreateDbContext())
            Assert.Equal(1, await ctx.WaitlistEntries.CountAsync(e => e.BusinessId == shop.BusinessId));

        Assert.True((await c.Waitlist.JoinAsync(pub, Join(shop, Day.AddDays(1)))).IsSuccess);
        Assert.True((await c.Waitlist.JoinAsync(pub, Join(shop, Day.AddDays(2)))).IsSuccess);
        var fourth = await c.Waitlist.JoinAsync(pub, Join(shop, Day.AddDays(3)));
        Assert.Equal(Text.WaitlistTooMany, fourth.Message);                                                                  // three at a time for one number
        Assert.True((await c.Waitlist.JoinAsync(pub, Join(shop, Day, phone: "09179999999"))).IsSuccess);                    // another person is fine

        // A shop holds at most MaxPerShop entries, so a flood can not fill the database.
        await using (var ctx = c.Db.CreateDbContext())
        {
            var have = await ctx.WaitlistEntries.CountAsync(e => e.BusinessId == shop.BusinessId);
            for (var i = have; i < WaitlistService.MaxPerShop; i++)
                ctx.WaitlistEntries.Add(new WaitlistEntry { BusinessId = shop.BusinessId, ServiceId = shop.ServiceId, Day = Day, CustomerName = "Filler", CustomerPhone = "0918" + (1000000 + i), CreatedAtUtc = c.Clock.GetUtcNow().UtcDateTime });
            await ctx.SaveChangesAsync();
        }
        Assert.Equal(Text.WaitlistShopFull, (await c.Waitlist.JoinAsync(pub, Join(shop, phone: "09175550000"))).Message);
    }

    [Fact]
    public async Task One_shop_never_sees_or_removes_another_shops_entries_and_a_limited_staff_login_sees_only_theirs_and_any_staff()
    {
        var c = await SetupAsync();
        var (shopA, slugA, _) = await FullShopAsync(c, staffCount: 2);
        var (shopB, slugB, _) = await FullShopAsync(c);
        var pubA = (await c.Catalog.GetBySlugAsync(slugA))!;
        var pubB = (await c.Catalog.GetBySlugAsync(slugB))!;

        Assert.True((await c.Waitlist.JoinAsync(pubA, Join(shopA, staffId: shopA.StaffIds[0], phone: "09170000001", name: "For One"))).IsSuccess);
        Assert.True((await c.Waitlist.JoinAsync(pubA, Join(shopA, staffId: shopA.StaffIds[1], phone: "09170000002", name: "For Two"))).IsSuccess);
        Assert.True((await c.Waitlist.JoinAsync(pubA, Join(shopA, phone: "09170000003", name: "For Any"))).IsSuccess);
        Assert.True((await c.Waitlist.JoinAsync(pubB, Join(shopB, phone: "09170000004", name: "Other Shop"))).IsSuccess);

        var all = (await c.Waitlist.ListAsync(shopA.BusinessId, null))!;
        Assert.Equal(new[] { "For Any", "For One", "For Two" }, all.Rows.Select(r => r.CustomerName).Order());                // nothing of shop B
        Assert.DoesNotContain(all.Rows, r => r.CustomerName == "Other Shop");

        var onlyOne = (await c.Waitlist.ListAsync(shopA.BusinessId, shopA.StaffIds[0]))!;
        Assert.Equal(new[] { "For Any", "For One" }, onlyOne.Rows.Select(r => r.CustomerName).Order());                      // not "For Two"

        var forTwo = all.Rows.Single(r => r.CustomerName == "For Two");
        var other = all.Rows.Single(r => r.CustomerName == "For Any");
        Assert.Equal(FailureKind.NotFound, (await c.Waitlist.RemoveAsync(shopA.BusinessId, forTwo.Id, shopA.StaffIds[0])).Failure); // another staff member's entry
        Assert.Null(await c.Waitlist.GetContactAsync(shopA.BusinessId, forTwo.Id, shopA.StaffIds[0]));
        Assert.Equal(FailureKind.NotFound, (await c.Waitlist.RemoveAsync(shopB.BusinessId, forTwo.Id, null)).Failure);       // another shop's id
        Assert.Null(await c.Waitlist.GetContactAsync(shopB.BusinessId, forTwo.Id, null));
        Assert.Equal(FailureKind.NotFound, (await c.Waitlist.RemoveAsync(shopA.BusinessId, Guid.NewGuid(), null)).Failure);  // an unknown id
        Assert.Equal(3, (await c.Waitlist.ListAsync(shopA.BusinessId, null))!.Rows.Count);                                   // nothing was removed

        var contact = await c.Waitlist.GetContactAsync(shopA.BusinessId, other.Id, shopA.StaffIds[0]);                       // "any staff" is for everyone
        Assert.Equal("For Any", contact!.Value.Name);
        Assert.True((await c.Waitlist.RemoveAsync(shopA.BusinessId, other.Id, shopA.StaffIds[0])).IsSuccess);
        Assert.Equal(2, (await c.Waitlist.ListAsync(shopA.BusinessId, null))!.Rows.Count);
    }

    [Fact]
    public async Task An_entry_shows_the_free_times_once_a_time_opens_and_expires_after_its_day()
    {
        var c = await SetupAsync();
        var (shop, slug, closure) = await FullShopAsync(c);
        var pub = (await c.Catalog.GetBySlugAsync(slug))!;
        Assert.True((await c.Waitlist.JoinAsync(pub, Join(shop))).IsSuccess);

        var full = (await c.Waitlist.ListAsync(shop.BusinessId, null))!;
        Assert.Empty(Assert.Single(full.Rows).FreeNow);                                                                      // still full

        await OpenUpAsync(c, closure);
        var opened = Assert.Single((await c.Waitlist.ListAsync(shop.BusinessId, null))!.Rows);
        Assert.NotEmpty(opened.FreeNow);                                                                                     // staff can now message them
        Assert.All(opened.FreeNow, t => Assert.Equal(Day, VeskaFlow.Core.Scheduling.LocalTime.LocalDateOf(t, "Asia/Manila")));

        c.Clock.Now = new DateTimeOffset(2030, 3, 6, 4, 0, 0, TimeSpan.Zero);                                                // Wednesday: the day has passed, still listed until a day after
        Assert.Empty((await c.Waitlist.ListAsync(shop.BusinessId, null))!.Rows);                                             // not shown after its day
        c.Clock.Now = new DateTimeOffset(2030, 3, 8, 4, 0, 0, TimeSpan.Zero);
        await c.Waitlist.ListAsync(shop.BusinessId, null);
        await using var ctx = c.Db.CreateDbContext();
        Assert.False(await ctx.WaitlistEntries.AnyAsync(e => e.BusinessId == shop.BusinessId));                              // and removed for good
    }

    [Fact]
    public async Task One_email_goes_out_when_a_time_opens_and_only_then_and_only_for_a_customer_with_an_email()
    {
        var c = await SetupAsync();
        var (shop, slug, closure) = await FullShopAsync(c);
        var pub = (await c.Catalog.GetBySlugAsync(slug))!;
        Assert.True((await c.Waitlist.JoinAsync(pub, Join(shop, email: "maria@example.com"))).IsSuccess);
        Assert.True((await c.Waitlist.JoinAsync(pub, Join(shop, phone: "09170000009", name: "No Email"))).IsSuccess);

        Assert.Equal(0, await c.Waitlist.NotifyFreedAsync());                                                                // still full: nothing to say
        Assert.Empty(c.Sender.Sent);

        await OpenUpAsync(c, closure);
        Assert.Equal(1, await c.Waitlist.NotifyFreedAsync());                                                                // only the one with an email
        var mail = Assert.Single(c.Sender.Sent);
        Assert.Equal("maria@example.com", mail.To);
        Assert.Contains("Test Shop", mail.Subject);
        Assert.Contains("Haircut", mail.Body);
        Assert.Contains("Tuesday, March 5", mail.Body);
        Assert.Contains("9:00 AM", mail.Body);
        Assert.Contains($"https://flow.example.com/b/{slug}?serviceId={shop.ServiceId}&date=2030-03-05", mail.Body);
        Assert.Contains("Please do not reply", mail.Body);
        Assert.Equal(1, await c.Quota.TodayTotalAsync());                                                                    // counted in the day's limit

        Assert.Equal(0, await c.Waitlist.NotifyFreedAsync());                                                                // never twice
        Assert.Single(c.Sender.Sent);
        await using var ctx = c.Db.CreateDbContext();
        Assert.NotNull((await ctx.WaitlistEntries.AsNoTracking().SingleAsync(e => e.CustomerEmail == "maria@example.com")).NotifiedAtUtc);
        Assert.Null((await ctx.WaitlistEntries.AsNoTracking().SingleAsync(e => e.CustomerName == "No Email")).NotifiedAtUtc);
    }

    [Fact]
    public async Task No_email_is_sent_when_the_shop_switch_is_off_the_shop_is_off_the_day_passed_or_there_is_no_address_for_the_link()
    {
        var c = await SetupAsync();
        var (shop, slug, closure) = await FullShopAsync(c);
        var pub = (await c.Catalog.GetBySlugAsync(slug))!;
        Assert.True((await c.Waitlist.JoinAsync(pub, Join(shop, email: "maria@example.com"))).IsSuccess);
        await OpenUpAsync(c, closure);

        await using (var ctx = c.Db.CreateDbContext())
            await ctx.Businesses.Where(b => b.Id == shop.BusinessId).ExecuteUpdateAsync(u => u.SetProperty(b => b.CustomerEmails, false));
        Assert.Equal(0, await c.Waitlist.NotifyFreedAsync());                                                                // the shop switched customer emails off
        await using (var ctx = c.Db.CreateDbContext())
            await ctx.Businesses.Where(b => b.Id == shop.BusinessId).ExecuteUpdateAsync(u => u.SetProperty(b => b.CustomerEmails, true).SetProperty(b => b.IsEnabled, false));
        Assert.Equal(0, await c.Waitlist.NotifyFreedAsync());                                                                // the platform switched the shop off
        await using (var ctx = c.Db.CreateDbContext())
            await ctx.Businesses.Where(b => b.Id == shop.BusinessId).ExecuteUpdateAsync(u => u.SetProperty(b => b.IsEnabled, true));

        var noAddress = await SetupAsync(Config(baseUrl: false));
        Assert.Equal(0, await noAddress.Waitlist.NotifyFreedAsync());                                                        // no public address: no link to send

        c.Clock.Now = new DateTimeOffset(2030, 3, 6, 4, 0, 0, TimeSpan.Zero);
        Assert.Equal(0, await c.Waitlist.NotifyFreedAsync());                                                                // the day has passed
        Assert.Empty(c.Sender.Sent);
        Assert.Empty(noAddress.Sender.Sent);
    }

    [Fact]
    public async Task Deleting_a_shop_deletes_its_waitlist_entries_and_leaves_the_others()
    {
        var c = await SetupAsync();
        var (shop, slug, _) = await FullShopAsync(c);
        var (keep, keepSlug, _) = await FullShopAsync(c);
        Assert.True((await c.Waitlist.JoinAsync((await c.Catalog.GetBySlugAsync(slug))!, Join(shop))).IsSuccess);
        Assert.True((await c.Waitlist.JoinAsync((await c.Catalog.GetBySlugAsync(keepSlug))!, Join(keep))).IsSuccess);

        var accounts = new AccountControlService(c.Db, c.Clock, new ConfigurationBuilder().Build(), new CapturingEmailSender());
        Assert.True((await accounts.DeleteShopAsync(shop.BusinessId, slug, null)).IsSuccess);
        await using var ctx = c.Db.CreateDbContext();
        Assert.False(await ctx.WaitlistEntries.AnyAsync(e => e.BusinessId == shop.BusinessId));
        Assert.True(await ctx.WaitlistEntries.AnyAsync(e => e.BusinessId == keep.BusinessId));
    }

    [Fact]
    public async Task The_daily_email_limit_stops_the_waitlist_email_and_the_entry_waits_for_the_next_run()
    {
        var c = await SetupAsync(Config(freeShopMax: 0)); // a shop without the paid add-on may send none today
        var (shop, slug, closure) = await FullShopAsync(c);
        var pub = (await c.Catalog.GetBySlugAsync(slug))!;
        Assert.True((await c.Waitlist.JoinAsync(pub, Join(shop, email: "maria@example.com"))).IsSuccess);
        await OpenUpAsync(c, closure);

        Assert.Equal(0, await c.Waitlist.NotifyFreedAsync());
        Assert.Empty(c.Sender.Sent);
        await using var ctx = c.Db.CreateDbContext();
        Assert.Null((await ctx.WaitlistEntries.AsNoTracking().SingleAsync(e => e.BusinessId == shop.BusinessId)).NotifiedAtUtc);   // not marked: the next run may send it
    }

    [Fact]
    public async Task The_public_page_and_the_back_office_work_over_HTTP_and_a_bot_or_another_shop_gets_nothing()
    {
        var (factory, _) = await HttpApp.StartAsync();
        using var _f = factory;
        var db = new TestDbFactory(await TestDb.ConnectionStringAsync());
        var tomorrow = Seed.NextOpenDay();
        var tomorrowUtcStart = new DateTime(tomorrow.Year, tomorrow.Month, tomorrow.Day, 0, 0, 0, DateTimeKind.Utc).AddHours(-8); // local midnight in Manila

        // A shop whose next open day is full.
        var shop = await Seed.CreateShopAsync();
        string slug;
        await using (var ctx = db.CreateDbContext())
        {
            ctx.Blackouts.Add(new Blackout { BusinessId = shop.BusinessId, StartsAtUtc = tomorrowUtcStart, EndsAtUtc = tomorrowUtcStart.AddDays(1) });
            await ctx.SaveChangesAsync();
            slug = await ctx.Businesses.Where(b => b.Id == shop.BusinessId).Select(b => b.Slug).SingleAsync();
        }
        var client = HttpApp.NewClient(factory);
        var dateParam = tomorrow.ToString("yyyy-MM-dd");
        var pageUrl = $"/b/{slug}/waitlist?serviceId={shop.ServiceId}&date={dateParam}";

        // The booking page offers the waitlist on a full day (and not on a day with times).
        var full = WebUtility.HtmlDecode(await client.GetStringAsync($"/b/{slug}?serviceId={shop.ServiceId}&date={dateParam}"));
        Assert.Contains(Text.WaitlistLink, full);
        Assert.Contains($"/b/{slug}/waitlist", full);
        var later = tomorrow.AddDays(3).ToString("yyyy-MM-dd");
        Assert.DoesNotContain(Text.WaitlistLink, WebUtility.HtmlDecode(await client.GetStringAsync($"/b/{slug}?serviceId={shop.ServiceId}&date={later}")));

        var form = WebUtility.HtmlDecode(await client.GetStringAsync(pageUrl));
        Assert.Contains(Text.WaitlistSubmit, form);
        Assert.Contains("Haircut", form);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/b/{slug}/waitlist?serviceId={Guid.NewGuid()}&date={dateParam}")).StatusCode);  // a service that is not this shop's
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/b/{slug}/waitlist?serviceId={shop.ServiceId}")).StatusCode);                  // no day

        Dictionary<string, string> Fields(string phone, string website = "", string? date = null) => new()
        {
            ["Input.ServiceId"] = shop.ServiceId.ToString(), ["Input.Date"] = date ?? dateParam, ["Input.Name"] = "Maria Santos", ["Input.Phone"] = phone,
            ["Input.Email"] = "", ["Input.Website"] = website,
        };

        // A bot fills the hidden field: it is told it worked and nothing is kept.
        var bot = await HttpApp.PostFormAsync(client, pageUrl.Split('?')[0], Fields("09175550001", "http://spam.example"), tokenPage: pageUrl);
        Assert.Equal(HttpStatusCode.Redirect, bot.StatusCode);
        await using (var ctx = db.CreateDbContext())
            Assert.False(await ctx.WaitlistEntries.AnyAsync(e => e.BusinessId == shop.BusinessId));

        // A bad number shows the error and keeps nothing; a good one is kept and the page says so.
        var bad = await HttpApp.PostFormAsync(client, pageUrl.Split('?')[0], Fields("12"), tokenPage: pageUrl);
        Assert.Equal(HttpStatusCode.OK, bad.StatusCode);
        Assert.Contains(Text.PhoneInvalid, WebUtility.HtmlDecode(await bad.Content.ReadAsStringAsync()));
        var joined = await HttpApp.PostFormAsync(client, pageUrl.Split('?')[0], Fields("09175550002"), tokenPage: pageUrl);
        Assert.Equal(HttpStatusCode.Redirect, joined.StatusCode);
        Assert.Contains(Text.WaitlistJoined, WebUtility.HtmlDecode(await client.GetStringAsync(joined.Headers.Location!.ToString())));
        await using (var ctx = db.CreateDbContext())
            Assert.Equal("09175550002", (await ctx.WaitlistEntries.AsNoTracking().SingleAsync(e => e.BusinessId == shop.BusinessId)).CustomerPhone);

        // On a day with free times the form refuses with the reason.
        var freeDay = tomorrow.AddDays(3).ToString("yyyy-MM-dd");
        var refused = await HttpApp.PostFormAsync(client, pageUrl.Split('?')[0], Fields("09175550003", date: freeDay), tokenPage: pageUrl);
        Assert.Contains(Text.WaitlistSlotsFree, WebUtility.HtmlDecode(await refused.Content.ReadAsStringAsync()));

        // Back office: the owner of another shop can not see or remove this shop's entry, a signed-out visitor is sent to sign in.
        var bossEmail = $"own{Guid.NewGuid():N}@example.com";
        var owner = HttpApp.NewClient(factory);
        await HttpApp.PostFormAsync(owner, "/Register", new()
        {
            ["Input.BusinessName"] = "Other Salon", ["Input.OwnerName"] = "Owner", ["Input.Email"] = bossEmail, ["Input.Password"] = "correct-horse-battery",
            ["Input.Accept"] = "true", ["Input.Industry"] = "barbershop",
        });
        Guid entryId;
        await using (var ctx = db.CreateDbContext()) entryId = await ctx.WaitlistEntries.Where(e => e.BusinessId == shop.BusinessId).Select(e => e.Id).SingleAsync();
        Assert.DoesNotContain("Maria Santos", await owner.GetStringAsync("/Admin/Waitlist"));
        Assert.Equal(HttpStatusCode.NotFound, (await HttpApp.PostFormAsync(owner, $"/Admin/Waitlist?handler=Remove", new() { ["id"] = entryId.ToString() }, tokenPage: "/Admin/Waitlist")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await owner.GetAsync($"/Admin/Waitlist?handler=Contact&id={entryId}")).StatusCode);
        Assert.Equal(HttpStatusCode.Redirect, (await HttpApp.NewClient(factory).GetAsync("/Admin/Waitlist")).StatusCode);
        await using (var ctx = db.CreateDbContext())
            Assert.True(await ctx.WaitlistEntries.AnyAsync(e => e.Id == entryId));

        // A shop's own owner sees the entry, can save the contact, and removes it (after the in-app confirm).
        var ownerShop = HttpApp.NewClient(factory);
        var email2 = $"own{Guid.NewGuid():N}@example.com";
        await HttpApp.PostFormAsync(ownerShop, "/Register", new()
        {
            ["Input.BusinessName"] = "Own Salon", ["Input.OwnerName"] = "Owner", ["Input.Email"] = email2, ["Input.Password"] = "correct-horse-battery",
            ["Input.Accept"] = "true", ["Input.Industry"] = "barbershop",
        });
        Guid businessId, serviceId;
        await using (var ctx = db.CreateDbContext())
        {
            businessId = await ctx.AdminUsers.Where(u => u.Email == email2).Select(u => u.BusinessId).SingleAsync();
            serviceId = await ctx.Services.Where(s => s.BusinessId == businessId && !s.IsOther).Select(s => s.Id).FirstAsync();
            ctx.WaitlistEntries.Add(new WaitlistEntry { BusinessId = businessId, ServiceId = serviceId, Day = tomorrow, CustomerName = "Pedro Reyes", CustomerPhone = "09176660000", CreatedAtUtc = DateTime.UtcNow });
            await ctx.SaveChangesAsync();
        }
        var list = WebUtility.HtmlDecode(await ownerShop.GetStringAsync("/Admin/Waitlist"));
        Assert.Contains("Pedro Reyes", list);
        Assert.Contains(Text.WaitlistRemoveAsk, list);                                                                       // every record-changing button asks first
        Assert.Contains("href=\"/Admin/Waitlist\"", WebUtility.HtmlDecode(await ownerShop.GetStringAsync("/Admin")));        // in the menu
        Guid mine;
        await using (var ctx = db.CreateDbContext()) mine = await ctx.WaitlistEntries.Where(e => e.BusinessId == businessId).Select(e => e.Id).SingleAsync();
        var vcf = await ownerShop.GetAsync($"/Admin/Waitlist?handler=Contact&id={mine}");
        Assert.Equal(HttpStatusCode.OK, vcf.StatusCode);
        Assert.Contains("Pedro Reyes", await vcf.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.Redirect, (await HttpApp.PostFormAsync(ownerShop, "/Admin/Waitlist?handler=Remove", new() { ["id"] = mine.ToString() }, tokenPage: "/Admin/Waitlist")).StatusCode);
        await using (var ctx = db.CreateDbContext())
            Assert.False(await ctx.WaitlistEntries.AnyAsync(e => e.Id == mine));
    }
}
