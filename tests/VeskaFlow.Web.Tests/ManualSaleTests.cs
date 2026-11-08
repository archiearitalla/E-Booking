using Microsoft.EntityFrameworkCore;
using VeskaFlow.Core.Domain;
using VeskaFlow.Web;
using VeskaFlow.Web.Data;
using VeskaFlow.Web.Services;

namespace VeskaFlow.Web.Tests;

/// <summary>"Record a sale" (BUSINESS_RULES 6.y20): a service and a payment entered by hand, saved as a completed visit.</summary>
[Collection("http")]
public class ManualSaleTests
{
    private static readonly BookingActor Manager = BookingActor.Staff(Guid.NewGuid(), "Ana (manager)");
    private static readonly DateOnly Today = new(2030, 3, 4);

    private sealed record Ctx(TestDbFactory Db, TestClock Clock, ManualSaleService Sales, Shop Shop, Guid OtherId);

    private static async Task<Ctx> SetupAsync(bool switchedOn = true)
    {
        var db = new TestDbFactory(await TestDb.ConnectionStringAsync());
        var clock = new TestClock(Seed.BaseNow);
        var shop = await Seed.CreateShopAsync();
        var other = Guid.NewGuid();
        await using (var ctx = db.CreateDbContext())
        {
            ctx.Services.Add(new Service { Id = other, BusinessId = shop.BusinessId, Name = "Other", Price = 0m, DurationMinutes = 30, IsOther = true });
            await ctx.SaveChangesAsync();
        }
        if (switchedOn) await SwitchAsync(db, shop, true);
        return new Ctx(db, clock, new ManualSaleService(db, clock), shop, other);
    }

    private static async Task SwitchAsync(TestDbFactory db, Shop shop, bool on)
    {
        await using var ctx = db.CreateDbContext();
        await ctx.Businesses.Where(b => b.Id == shop.BusinessId).ExecuteUpdateAsync(u => u.SetProperty(b => b.AllowManualSales, on));
    }

    private static ManualSaleInput Sale(Ctx c, decimal? amount = 250m, string? name = null, string? phone = null, DateOnly? date = null, Guid? serviceId = null, string? note = null, string? key = null) =>
        new(serviceId ?? c.Shop.ServiceId, note, date ?? Today, null, amount, name, phone, null, key ?? Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task A_sale_recorded_for_a_time_that_is_still_running_blocks_that_time_for_new_bookings()
    {
        var c = await SetupAsync();
        c.Clock.Now = new DateTimeOffset(Seed.Manila(4, 10, 10), TimeSpan.Zero); // 10:10, Monday
        var bookings = new BookingService(c.Db, c.Clock);
        var shop = c.Shop;
        async Task<IReadOnlyList<DateTime>> FreeNow() => (await bookings.GetFreeStartsAsync(shop.BusinessId, shop.ServiceId, null, Today, walkIn: true)).Value!;

        Assert.Contains(Seed.Manila(4, 10), await FreeNow()); // the 10:00 slot is free before the sale

        // A 30-minute service done from 10:00 (it is still running at 10:10).
        var made = await c.Sales.RecordAsync(shop.BusinessId, Sale(c, 250m) with { Time = new TimeOnly(10, 0) }, Manager);
        Assert.True(made.IsSuccess);

        var free = await FreeNow();
        Assert.DoesNotContain(Seed.Manila(4, 10), free);   // that time is no longer available
        Assert.Contains(Seed.Manila(4, 10, 30), free);     // the next one is
        var taken = await bookings.CreateAsync(Seed.Request(shop, Seed.Manila(4, 10), actor: Manager));
        Assert.Equal(FailureKind.SlotTaken, taken.Failure);
        Assert.True((await bookings.CreateAsync(Seed.Request(shop, Seed.Manila(4, 10, 30), actor: Manager))).IsSuccess);

        await using var ctx = c.Db.CreateDbContext();
        Assert.True((await ctx.Bookings.SingleAsync(b => b.Id == made.Value)).IsManualSale);
    }

    [Fact]
    public async Task A_sale_whose_time_is_over_blocks_nothing_and_an_ordinary_booking_is_not_a_manual_sale()
    {
        var c = await SetupAsync();
        c.Clock.Now = new DateTimeOffset(Seed.Manila(4, 14), TimeSpan.Zero); // 14:00
        var bookings = new BookingService(c.Db, c.Clock);
        var shop = c.Shop;

        var made = await c.Sales.RecordAsync(shop.BusinessId, Sale(c, 250m) with { Time = new TimeOnly(10, 0) }, Manager); // done 10:00 to 10:30
        Assert.True(made.IsSuccess);
        // Looking back at 10:00 the slot is not offered because it is in the past; the point is the sale added no block now.
        var free = (await bookings.GetFreeStartsAsync(shop.BusinessId, shop.ServiceId, null, Today, walkIn: true)).Value!;
        Assert.Contains(Seed.Manila(4, 14), free);
        Assert.Contains(Seed.Manila(4, 14, 30), free);

        var ordinary = await bookings.CreateAsync(Seed.Request(shop, Seed.Manila(4, 15), actor: Manager));
        await using var ctx = c.Db.CreateDbContext();
        Assert.False((await ctx.Bookings.SingleAsync(b => b.Id == ordinary.Value!.Booking.Id)).IsManualSale);
    }

    [Fact]
    public async Task The_time_defaults_to_when_it_was_entered_stays_inside_the_working_hours_and_the_day_view_labels_it()
    {
        var c = await SetupAsync(); // hours are 9:00 to 18:00 every day, the service takes 30 minutes
        async Task<DateTime> StartOf(Guid id) { await using var ctx = c.Db.CreateDbContext(); return (await ctx.Bookings.SingleAsync(b => b.Id == id)).StartsAtUtc; }

        c.Clock.Now = new DateTimeOffset(Seed.Manila(4, 11, 20), TimeSpan.Zero); // entered at 11:20, no time typed
        var now = await c.Sales.RecordAsync(c.Shop.BusinessId, Sale(c), Manager);
        Assert.Equal(Seed.Manila(4, 11, 20), await StartOf(now.Value));

        c.Clock.Now = new DateTimeOffset(Seed.Manila(4, 20), TimeSpan.Zero); // entered at 20:00, after closing, no time typed
        var late = await c.Sales.RecordAsync(c.Shop.BusinessId, Sale(c), Manager);
        Assert.Equal(Seed.Manila(4, 17, 30), await StartOf(late.Value)); // the last 30 minutes of the working hours

        c.Clock.Now = new DateTimeOffset(Seed.Manila(4, 8), TimeSpan.Zero); // entered at 8:00, before opening: kept as entered
        var early = await c.Sales.RecordAsync(c.Shop.BusinessId, Sale(c), Manager);
        Assert.Equal(Seed.Manila(4, 8), await StartOf(early.Value));

        // A time typed outside the hours is refused (nothing saved); inside the hours it is kept.
        c.Clock.Now = new DateTimeOffset(Seed.Manila(4, 20), TimeSpan.Zero);
        var outside = await c.Sales.RecordAsync(c.Shop.BusinessId, Sale(c) with { Time = new TimeOnly(7, 0) }, Manager);
        Assert.Contains("time", outside.FieldErrors.Keys);
        Assert.Contains("time", (await c.Sales.RecordAsync(c.Shop.BusinessId, Sale(c) with { Time = new TimeOnly(18, 0) }, Manager)).FieldErrors.Keys); // closing time itself is outside
        var typed = await c.Sales.RecordAsync(c.Shop.BusinessId, Sale(c) with { Time = new TimeOnly(13, 15) }, Manager);
        Assert.Equal(Seed.Manila(4, 13, 15), await StartOf(typed.Value));

        var day = (await new AdminDayService(c.Db, c.Clock).GetDayAsync(c.Shop.BusinessId, Today))!;
        Assert.All(day.Bookings, b => Assert.True(b.IsManualSale)); // every one of these was typed in by hand
    }

    [Fact]
    public async Task The_label_shows_in_the_day_list_and_in_the_list_under_the_Done_and_Earned_cards()
    {
        var (factory, client) = await HttpApp.StartAsync();
        using var _f = factory;
        var db = new TestDbFactory(await TestDb.ConnectionStringAsync());
        var email = $"o{Guid.NewGuid():N}"[..12] + "@example.com";
        var owner = (await new OnboardingService(db, TimeProvider.System).RegisterAsync(
            new RegisterRequest("Label Shop", "lb" + Guid.NewGuid().ToString("N")[..10], "Owner", email, "correct-horse-1", TrialDays: 30))).Value!;
        await using (var ctx = db.CreateDbContext())
            await ctx.Businesses.Where(b => b.Id == owner.BusinessId).ExecuteUpdateAsync(u => u.SetProperty(b => b.AllowManualSales, true));
        var serviceId = await db.CreateDbContext().Services.Where(s => s.BusinessId == owner.BusinessId && !s.IsOther).Select(s => s.Id).SingleAsync();
        var today = DateOnly.FromDateTime(DateTime.UtcNow.AddHours(8));
        var made = await new ManualSaleService(db, TimeProvider.System).RecordAsync(owner.BusinessId,
            new ManualSaleInput(serviceId, null, today, null, 200m, "Mark Santos", null, null, Guid.NewGuid().ToString("N")),
            BookingActor.Staff(owner.AdminUserId, "Owner"));
        Assert.True(made.IsSuccess, made.Message);

        Assert.Equal(System.Net.HttpStatusCode.Redirect, (await HttpApp.PostFormAsync(client, "/Login", new() { ["Input.Email"] = email, ["Input.Password"] = "correct-horse-1" })).StatusCode);
        var date = DisplayFormat.DateParam(today);
        string Page(string html) => System.Net.WebUtility.HtmlDecode(html);
        Assert.Contains(Text.ManualSaleBadge, Page(await client.GetStringAsync($"/Admin?date={date}&status=done")));   // the Done list
        Assert.Contains(Text.ManualSaleBadge, Page(await client.GetStringAsync($"/Admin?date={date}&kpi=done")));     // under the Done card
        Assert.Contains(Text.ManualSaleBadge, Page(await client.GetStringAsync($"/Admin?date={date}&kpi=earned")));   // under the Earned card
    }

    [Fact]
    public async Task It_is_off_until_the_shop_switches_it_on_in_Settings()
    {
        var c = await SetupAsync(switchedOn: false);
        var settings = new ShopSettingsService(c.Db, TimeProvider.System);
        Assert.False((await settings.GetSettingsAsync(c.Shop.BusinessId))!.AllowManualSales);
        var refused = await c.Sales.RecordAsync(c.Shop.BusinessId, Sale(c), Manager);
        Assert.Equal(FailureKind.PolicyViolation, refused.Failure);
        Assert.Equal(Text.ManualSaleOff, refused.Message);
        await using (var ctx = c.Db.CreateDbContext()) Assert.Empty(await ctx.Bookings.Where(b => b.BusinessId == c.Shop.BusinessId).ToListAsync());

        var current = (await settings.GetSettingsAsync(c.Shop.BusinessId))!;
        await settings.SaveSettingsAsync(c.Shop.BusinessId, new SettingsInput(current.Name, current.TimeZone, current.SlotMinutes, current.LeadTimeMinutes, current.HorizonDays, current.CancelCutoffHours, AllowManualSales: true));
        Assert.True((await settings.GetSettingsAsync(c.Shop.BusinessId))!.AllowManualSales);
        Assert.True((await c.Sales.RecordAsync(c.Shop.BusinessId, Sale(c), Manager)).IsSuccess);
    }

    [Fact]
    public async Task A_sale_is_a_completed_visit_that_reports_and_the_customer_history_count_and_it_is_audited()
    {
        var c = await SetupAsync();
        var made = await c.Sales.RecordAsync(c.Shop.BusinessId, Sale(c, 180.555m, "Maria Santos", "0917 555 0199"), Manager);
        Assert.True(made.IsSuccess, made.Message);

        await using var ctx = c.Db.CreateDbContext();
        var booking = await ctx.Bookings.SingleAsync(b => b.Id == made.Value);
        Assert.Equal((BookingStatus.Completed, 180.56m), (booking.Status, booking.PaidAmount));
        var customer = await ctx.Customers.SingleAsync(x => x.Id == booking.CustomerId);
        Assert.Equal("Maria Santos", customer.Name);
        Assert.False(DisplayFormat.IsPlaceholderPhone(customer.Phone));
        var audit = await ctx.AuditLogs.SingleAsync(a => a.BookingId == booking.Id);
        Assert.Equal(("MANUAL_SALE", "COMPLETED", "Ana (manager)"), (audit.Action, audit.ToStatus, audit.ActorLabel));

        var report = (await new ReportService(c.Db, c.Clock).BuildRangeAsync(c.Shop.BusinessId, Today, Today))!;
        Assert.Equal((1, 180.56m), (report.Completed, report.Revenue));
        var row = (await new CustomerListService(c.Db).ListAsync(c.Shop.BusinessId, null)).Single();
        Assert.Equal((1, 180.56m), (row.Visits, row.Spent));

        // The same number again is the same customer, and keeps the name on file.
        Assert.True((await c.Sales.RecordAsync(c.Shop.BusinessId, Sale(c, 100m, "Someone Else", "+639175550199"), Manager)).IsSuccess);
        var again = (await new CustomerListService(c.Db).ListAsync(c.Shop.BusinessId, null)).Single();
        Assert.Equal(("Maria Santos", 2), (again.Name, again.Visits));
    }

    [Fact]
    public async Task A_customer_with_no_number_is_saved_as_a_walk_in_each_time()
    {
        var c = await SetupAsync();
        Assert.True((await c.Sales.RecordAsync(c.Shop.BusinessId, Sale(c), Manager)).IsSuccess);
        Assert.True((await c.Sales.RecordAsync(c.Shop.BusinessId, Sale(c, 300m, "Kuya Ben"), Manager)).IsSuccess);
        await using var ctx = c.Db.CreateDbContext();
        var customers = await ctx.Customers.Where(x => x.BusinessId == c.Shop.BusinessId).ToListAsync();
        Assert.Equal(2, customers.Count);
        Assert.All(customers, x => Assert.True(DisplayFormat.IsPlaceholderPhone(x.Phone)));
        Assert.Contains(customers, x => x.Name == Text.WalkInNameDefault);
        Assert.Contains(customers, x => x.Name == "Kuya Ben");
    }

    [Fact]
    public async Task The_amount_the_date_the_note_and_the_number_are_checked_and_nothing_is_saved_on_error()
    {
        var c = await SetupAsync();
        async Task<IReadOnlyDictionary<string, string>> Errors(ManualSaleInput i) => (await c.Sales.RecordAsync(c.Shop.BusinessId, i, Manager)).FieldErrors;

        Assert.Contains("amount", (await Errors(Sale(c, null))).Keys);
        Assert.Contains("amount", (await Errors(Sale(c, -1m))).Keys);
        Assert.Contains("amount", (await Errors(Sale(c, BookingExtraService.MaxPrice + 1))).Keys);
        Assert.Contains("date", (await Errors(Sale(c, date: Today.AddDays(1)))).Keys);                                     // not in the future
        Assert.Contains("date", (await Errors(Sale(c, date: Today.AddDays(-ManualSaleService.MaxDaysBack - 1)))).Keys);    // not more than a year back
        Assert.Contains("phone", (await Errors(Sale(c, phone: "abc"))).Keys);
        Assert.Contains("name", (await Errors(Sale(c, name: new string('x', 81)))).Keys);
        Assert.Contains("note", (await Errors(Sale(c, serviceId: c.OtherId, note: ""))).Keys);                             // Other needs what was done
        Assert.Contains("idempotencyKey", (await Errors(Sale(c, key: " "))).Keys);
        await using (var ctx = c.Db.CreateDbContext()) Assert.Empty(await ctx.Bookings.Where(b => b.BusinessId == c.Shop.BusinessId).ToListAsync());

        Assert.True((await c.Sales.RecordAsync(c.Shop.BusinessId, Sale(c, 0m, date: Today.AddDays(-ManualSaleService.MaxDaysBack)), Manager)).IsSuccess);   // a free visit, a year back
        var other = await c.Sales.RecordAsync(c.Shop.BusinessId, Sale(c, 80m, serviceId: c.OtherId, note: "Beard dye"), Manager);
        await using var ctx2 = c.Db.CreateDbContext();
        var line = await ctx2.Bookings.SingleAsync(b => b.Id == other.Value);
        Assert.Equal(("Other: Beard dye", 80m), (line.ServiceNameSnapshot, line.PriceSnapshot));
    }

    [Fact]
    public async Task The_same_form_sent_twice_is_one_sale_and_other_shops_and_staff_scope_are_respected()
    {
        var c = await SetupAsync();
        var key = Guid.NewGuid().ToString("N");
        var first = await c.Sales.RecordAsync(c.Shop.BusinessId, Sale(c, key: key), Manager);
        var second = await c.Sales.RecordAsync(c.Shop.BusinessId, Sale(c, key: key), Manager);
        Assert.Equal(first.Value, second.Value);
        await using (var ctx = c.Db.CreateDbContext()) Assert.Single(await ctx.Bookings.Where(b => b.BusinessId == c.Shop.BusinessId).ToListAsync());

        var stranger = await Seed.CreateShopAsync();
        await SwitchAsync(c.Db, stranger, true);
        Assert.Equal(FailureKind.NotFound, (await c.Sales.RecordAsync(stranger.BusinessId, Sale(c), Manager)).Failure);                 // another shop's service
        Assert.Equal(FailureKind.NotFound, (await c.Sales.RecordAsync(c.Shop.BusinessId, Sale(c), Manager, default, Guid.NewGuid())).Failure); // a staff member who is not in this shop
        Assert.Equal(FailureKind.NotFound, (await c.Sales.RecordAsync(c.Shop.BusinessId, Sale(c) with { StaffId = Guid.NewGuid() }, Manager)).Failure);
    }
}
