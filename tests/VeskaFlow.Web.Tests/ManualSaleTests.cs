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
