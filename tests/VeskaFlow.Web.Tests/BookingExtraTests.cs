using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using VeskaFlow.Core.Domain;
using VeskaFlow.Core.Scheduling;
using VeskaFlow.Web;
using VeskaFlow.Web.Data;
using VeskaFlow.Web.Services;

namespace VeskaFlow.Web.Tests;

/// <summary>Services done on top of the booked one: the itemized receipt, its prices and promo, the payment total that follows it, and who may change it.</summary>
[Collection("http")]
public class BookingExtraTests
{
    private sealed record Ctx(TestDbFactory Db, TestClock Clock, BookingService Booking, BookingExtraService Extras, PromoBannerService Promo, Shop Shop, Guid ColorId, Guid OtherId);

    /// <summary>A shop with a Haircut (150.00), a Color (300.00) and the "Other" service, on the clock of Monday 2030-03-04, 08:00 in Manila.</summary>
    private static async Task<Ctx> SetupAsync()
    {
        var db = new TestDbFactory(await TestDb.ConnectionStringAsync());
        var clock = new TestClock(Seed.BaseNow);
        var shop = await Seed.CreateShopAsync();
        Guid color = Guid.NewGuid(), other = Guid.NewGuid();
        await using (var ctx = db.CreateDbContext())
        {
            ctx.Services.Add(new Service { Id = color, BusinessId = shop.BusinessId, Name = "Color", Price = 300m, DurationMinutes = 60 });
            ctx.Services.Add(new Service { Id = other, BusinessId = shop.BusinessId, Name = "Other", Price = 0m, DurationMinutes = 30, IsOther = true });
            await ctx.SaveChangesAsync();
        }
        return new Ctx(db, clock, new BookingService(db, clock), new BookingExtraService(db, clock), new PromoBannerService(db, clock), shop, color, other);
    }

    private static readonly BookingActor Staff = BookingActor.Staff(Guid.NewGuid(), "Jun (staff)");

    private static async Task<BookingSummary> CompletedAsync(Ctx c, int day = 5, int hour = 10, Shop? shop = null)
    {
        var s = shop ?? c.Shop;
        var made = (await c.Booking.CreateAsync(Seed.Request(s, Seed.Manila(day, hour)))).Value!.Booking;
        await using var ctx = c.Db.CreateDbContext();
        await ctx.Bookings.Where(b => b.Id == made.Id).ExecuteUpdateAsync(u => u.SetProperty(b => b.Status, BookingStatus.Completed));
        return made;
    }

    private static Task<ServiceResult<bool>> SetPromo(Ctx c, int percent, bool active = true, DateOnly? lastDay = null) =>
        c.Promo.SaveAsync(c.Shop.BusinessId, null, false, "Promo", null, null, "plain", active, lastDay, percent);

    // ---------------------------------------------------------------- adding and removing

    [Fact]
    public async Task A_service_done_on_top_is_its_own_line_at_its_own_price_and_only_a_completed_visit_can_have_it()
    {
        var c = await SetupAsync();
        var confirmed = (await c.Booking.CreateAsync(Seed.Request(c.Shop, Seed.Manila(5, 9)))).Value!.Booking;
        var done = await CompletedAsync(c);

        var notYet = await c.Extras.AddAsync(c.Shop.BusinessId, confirmed.Id, c.ColorId, null, null, Staff);
        Assert.Equal(FailureKind.PolicyViolation, notYet.Failure);
        Assert.Equal(Text.ExtraOnlyCompleted, notYet.Message);

        var added = await c.Extras.AddAsync(c.Shop.BusinessId, done.Id, c.ColorId, null, null, Staff);
        Assert.True(added.IsSuccess, added.Message);
        Assert.Equal(("Color", 300m, (decimal?)null, (int?)null), (added.Value!.Line!.Name, added.Value.Line.Price, added.Value.Line.RegularPrice, added.Value.Line.DiscountPercent));
        Assert.False(added.Value.PaymentReset);
        var lines = await c.Extras.ListAsync(c.Shop.BusinessId, done.Id);
        Assert.Equal(["Color"], lines.Select(l => l.Name));

        // The same service can be done twice, and the booked service can be added again (a second haircut).
        Assert.True((await c.Extras.AddAsync(c.Shop.BusinessId, done.Id, c.ColorId, null, null, Staff)).IsSuccess);
        Assert.True((await c.Extras.AddAsync(c.Shop.BusinessId, done.Id, c.Shop.ServiceId, null, null, Staff)).IsSuccess);
        Assert.Equal(3, (await c.Extras.ListAsync(c.Shop.BusinessId, done.Id)).Count);

        // An unknown service, an inactive one and another shop's one are "not found".
        var other = await Seed.CreateShopAsync();
        Assert.Equal(FailureKind.NotFound, (await c.Extras.AddAsync(c.Shop.BusinessId, done.Id, Guid.NewGuid(), null, null, Staff)).Failure);
        Assert.Equal(FailureKind.NotFound, (await c.Extras.AddAsync(c.Shop.BusinessId, done.Id, other.ServiceId, null, null, Staff)).Failure);
        await using (var ctx = c.Db.CreateDbContext())
            await ctx.Services.Where(s => s.Id == c.ColorId).ExecuteUpdateAsync(u => u.SetProperty(s => s.IsActive, false));
        Assert.Equal(FailureKind.NotFound, (await c.Extras.AddAsync(c.Shop.BusinessId, done.Id, c.ColorId, null, null, Staff)).Failure);
    }

    [Fact]
    public async Task The_Other_service_takes_a_description_and_a_price_typed_by_staff_and_nothing_else_does()
    {
        var c = await SetupAsync();
        var done = await CompletedAsync(c);
        await SetPromo(c, 20);                                                                                                // a promo never touches "Other"

        var noNote = await c.Extras.AddAsync(c.Shop.BusinessId, done.Id, c.OtherId, "  ", 80m, Staff);
        Assert.Equal(Text.ExtraNoteInvalid, noNote.FieldErrors["note"]);
        var noPrice = await c.Extras.AddAsync(c.Shop.BusinessId, done.Id, c.OtherId, "Beard dye", null, Staff);
        Assert.Equal(Text.ExtraPriceInvalid, noPrice.FieldErrors["price"]);
        var both = await c.Extras.AddAsync(c.Shop.BusinessId, done.Id, c.OtherId, new string('x', 61), -1m, Staff);
        Assert.Equal(["note", "price"], both.FieldErrors.Keys.Order());
        Assert.Equal(Text.ExtraPriceInvalid, (await c.Extras.AddAsync(c.Shop.BusinessId, done.Id, c.OtherId, "Beard dye", BookingExtraService.MaxPrice + 1, Staff)).FieldErrors["price"]);
        Assert.Empty(await c.Extras.ListAsync(c.Shop.BusinessId, done.Id));                                                    // none of that was kept

        var ok = await c.Extras.AddAsync(c.Shop.BusinessId, done.Id, c.OtherId, " Beard dye ", 80.456m, Staff);
        Assert.True(ok.IsSuccess, ok.Message);
        Assert.Equal(("Other: Beard dye", 80.46m, (decimal?)null), (ok.Value!.Line!.Name, ok.Value.Line.Price, ok.Value.Line.RegularPrice));

        // A typed price is ignored for a service that has its own price (300.00, here less the 20% promo that is running).
        var color = await c.Extras.AddAsync(c.Shop.BusinessId, done.Id, c.ColorId, "ignored", 1m, Staff);
        Assert.Equal(240.00m, color.Value!.Line!.Price);
    }

    [Fact]
    public async Task At_most_ten_services_can_be_added_to_one_visit()
    {
        var c = await SetupAsync();
        var done = await CompletedAsync(c);
        for (var i = 0; i < BookingExtraService.MaxExtras; i++)
            Assert.True((await c.Extras.AddAsync(c.Shop.BusinessId, done.Id, c.ColorId, null, null, Staff)).IsSuccess);
        var eleventh = await c.Extras.AddAsync(c.Shop.BusinessId, done.Id, c.ColorId, null, null, Staff);
        Assert.Equal(FailureKind.PolicyViolation, eleventh.Failure);
        Assert.Equal(Text.ExtraTooMany(BookingExtraService.MaxExtras), eleventh.Message);
        Assert.Equal(BookingExtraService.MaxExtras, (await c.Extras.ListAsync(c.Shop.BusinessId, done.Id)).Count);
    }

    [Fact]
    public async Task A_promo_running_today_takes_its_discount_off_an_added_service_that_is_part_of_it()
    {
        var c = await SetupAsync();
        var done = await CompletedAsync(c);
        Guid leftOut = Guid.NewGuid();
        await using (var ctx = c.Db.CreateDbContext())
        {
            ctx.Services.Add(new Service { Id = leftOut, BusinessId = c.Shop.BusinessId, Name = "Spa", Price = 500m, DurationMinutes = 60, PromoEligible = false });
            await ctx.SaveChangesAsync();
        }
        Assert.True((await SetPromo(c, 20, lastDay: Day(5))).IsSuccess);

        var color = (await c.Extras.AddAsync(c.Shop.BusinessId, done.Id, c.ColorId, null, null, Staff)).Value!.Line!;
        Assert.Equal((240.00m, 300.00m, 20), (color.Price, color.RegularPrice, color.DiscountPercent));
        var spa = (await c.Extras.AddAsync(c.Shop.BusinessId, done.Id, leftOut, null, null, Staff)).Value!.Line!;
        Assert.Equal((500.00m, (decimal?)null, (int?)null), (spa.Price, spa.RegularPrice, spa.DiscountPercent));              // left out of the promo

        c.Clock.Now = new DateTimeOffset(2030, 3, 5, 16, 0, 0, TimeSpan.Zero);                                                // after the last day
        var later = (await c.Extras.AddAsync(c.Shop.BusinessId, done.Id, c.ColorId, null, null, Staff)).Value!.Line!;
        Assert.Equal((300.00m, (decimal?)null), (later.Price, later.RegularPrice));
        Assert.Equal(240.00m, (await c.Extras.ListAsync(c.Shop.BusinessId, done.Id)).First(l => l.Name == "Color").Price);     // the first line keeps its price
    }

    private static DateOnly Day(int d) => new(2030, 3, d);

    [Fact]
    public async Task Changing_the_services_sends_a_saved_payment_total_back_to_the_items_total_and_every_change_is_audited()
    {
        var c = await SetupAsync();
        var records = new VisitRecordService(c.Db, c.Clock);
        var done = await CompletedAsync(c);
        var days = new AdminDayService(c.Db, c.Clock);

        async Task<DayBooking> Day5() => (await days.GetBookingAsync(c.Shop.BusinessId, done.Id))!;
        Assert.Equal(150m, (await Day5()).Amount);

        var color = (await c.Extras.AddAsync(c.Shop.BusinessId, done.Id, c.ColorId, null, null, Staff)).Value!.Line!;
        var withColor = await Day5();
        Assert.Equal((450m, 450m, 1), (withColor.ItemsTotal, withColor.Amount, withColor.ExtraLines.Count));                  // 150 + 300

        Assert.True((await records.SetPaidAmountAsync(c.Shop.BusinessId, done.Id, 500m, default, null, Staff)).IsSuccess);     // a tip on top
        Assert.Equal(500m, (await Day5()).Amount);

        var second = await c.Extras.AddAsync(c.Shop.BusinessId, done.Id, c.ColorId, null, null, Staff);
        Assert.True(second.Value!.PaymentReset);                                                                              // the saved 500 no longer fits
        var reset = await Day5();
        Assert.Equal((750m, 750m), (reset.ItemsTotal, reset.Amount));
        Assert.Null(reset.PaidAmount);

        Assert.True((await records.SetPaidAmountAsync(c.Shop.BusinessId, done.Id, 700m, default, null, Staff)).IsSuccess);
        var removed = await c.Extras.RemoveAsync(c.Shop.BusinessId, done.Id, color.Id, Staff);
        Assert.True(removed.IsSuccess);
        Assert.True(removed.Value!.PaymentReset);
        Assert.Equal((450m, 450m), ((await Day5()).ItemsTotal, (await Day5()).Amount));

        await using var ctx = c.Db.CreateDbContext();
        var audit = await ctx.AuditLogs.AsNoTracking().Where(a => a.BookingId == done.Id && (a.Action.StartsWith("EXTRA") || a.Action == "PAYMENT_SET")).OrderBy(a => a.Id).ToListAsync();
        Assert.Equal(["EXTRA_ADDED", "PAYMENT_SET", "EXTRA_ADDED", "PAYMENT_SET", "EXTRA_REMOVED"], audit.Select(a => a.Action));
        Assert.All(audit, a => { Assert.Equal("STAFF", a.ActorType); Assert.Equal("Jun (staff)", a.ActorLabel); });
        Assert.Equal("Color: ₱300.00", audit[0].Reason);
        Assert.Equal("₱500.00 (was not set)", audit[1].Reason);
        Assert.Equal("Color: ₱300.00 (payment total reset)", audit[2].Reason);
        Assert.Equal("₱700.00 (was not set)", audit[3].Reason);
        Assert.Equal("Color: ₱300.00 (payment total reset)", audit[4].Reason);
    }

    [Fact]
    public async Task One_shop_or_a_limited_staff_login_cannot_add_or_remove_the_services_of_a_visit_that_is_not_theirs()
    {
        var c = await SetupAsync();
        var other = await Seed.CreateShopAsync();
        var mine = await CompletedAsync(c);
        var added = (await c.Extras.AddAsync(c.Shop.BusinessId, mine.Id, c.ColorId, null, null, Staff)).Value!.Line!;

        Assert.Equal(FailureKind.NotFound, (await c.Extras.AddAsync(other.BusinessId, mine.Id, other.ServiceId, null, null, Staff)).Failure);  // another shop's id
        Assert.Equal(FailureKind.NotFound, (await c.Extras.AddAsync(c.Shop.BusinessId, mine.Id, c.ColorId, null, null, Staff, default, Guid.NewGuid())).Failure); // another person's visit
        Assert.Equal(FailureKind.NotFound, (await c.Extras.RemoveAsync(other.BusinessId, mine.Id, added.Id, Staff)).Failure);
        Assert.Equal(FailureKind.NotFound, (await c.Extras.RemoveAsync(c.Shop.BusinessId, mine.Id, Guid.NewGuid(), Staff)).Failure);
        Assert.Equal(FailureKind.NotFound, (await c.Extras.RemoveAsync(c.Shop.BusinessId, Guid.NewGuid(), added.Id, Staff)).Failure);
        Assert.Equal(FailureKind.NotFound, (await c.Extras.RemoveAsync(c.Shop.BusinessId, mine.Id, added.Id, Staff, default, Guid.NewGuid())).Failure);
        Assert.Empty(await c.Extras.ListAsync(other.BusinessId, mine.Id));                                                     // and it cannot be read through them
        Assert.Single(await c.Extras.ListAsync(c.Shop.BusinessId, mine.Id));
        Assert.True((await c.Extras.AddAsync(c.Shop.BusinessId, mine.Id, c.ColorId, null, null, Staff, default, c.Shop.StaffIds[0])).IsSuccess);

        // The database agrees: an extra cannot be tied to a booking of another shop, and goes with its booking.
        await using var ctx = c.Db.CreateDbContext();
        var mismatch = await Assert.ThrowsAnyAsync<Exception>(() => ctx.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO booking_extra (business_id, booking_id, service_id, service_name_snapshot, price_snapshot)
            VALUES ({other.BusinessId}, {mine.Id}, {other.ServiceId}, 'x', 1)
            """));
        Assert.Contains("booking_extra_booking_id_business_id_fkey", mismatch.ToString());
        var mine2 = await CompletedAsync(c, 5, 11);
        Assert.True((await c.Extras.AddAsync(c.Shop.BusinessId, mine2.Id, c.ColorId, null, null, Staff)).IsSuccess);
        await ctx.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM audit_log WHERE booking_id = {mine2.Id}");
        await ctx.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM booking WHERE id = {mine2.Id}");
        Assert.Equal(0, await ctx.BookingExtras.CountAsync(e => e.BookingId == mine2.Id));
    }

    [Fact]
    public async Task The_payment_total_change_is_audited_with_who_and_what_it_was_before()
    {
        var c = await SetupAsync();
        var records = new VisitRecordService(c.Db, c.Clock);
        var done = await CompletedAsync(c);

        Assert.True((await records.SetPaidAmountAsync(c.Shop.BusinessId, done.Id, 200m, default, null, Staff)).IsSuccess);
        Assert.True((await records.SetPaidAmountAsync(c.Shop.BusinessId, done.Id, 250m, default, null, Staff)).IsSuccess);
        Assert.True((await records.SetPaidAmountAsync(c.Shop.BusinessId, done.Id, null, default, null, Staff)).IsSuccess);
        await using var ctx = c.Db.CreateDbContext();
        var reasons = await ctx.AuditLogs.AsNoTracking().Where(a => a.BookingId == done.Id && a.Action == "PAYMENT_SET").OrderBy(a => a.Id).Select(a => a.Reason).ToListAsync();
        Assert.Equal(["₱200.00 (was not set)", "₱250.00 (was ₱200.00)", "cleared (was ₱250.00)"], reasons);
        Assert.False(await ctx.AuditLogs.AnyAsync(a => a.BookingId == done.Id && a.Action == "PAYMENT_SET" && a.ActorLabel != "Jun (staff)"));
    }

    // ---------------------------------------------------------------- reports, customers, the customer's receipt

    [Fact]
    public async Task Reports_and_customer_totals_count_the_services_done_on_top_and_show_what_the_payment_total_changed()
    {
        var c = await SetupAsync();
        var records = new VisitRecordService(c.Db, c.Clock);
        var first = await CompletedAsync(c, 5, 9);                                                                              // haircut 150 + color 300 = 450, no payment typed
        await c.Extras.AddAsync(c.Shop.BusinessId, first.Id, c.ColorId, null, null, Staff);
        var second = await CompletedAsync(c, 5, 10);                                                                            // haircut 150 + color 300 + other 80, paid 500 (a tip)
        await c.Extras.AddAsync(c.Shop.BusinessId, second.Id, c.ColorId, null, null, Staff);
        await c.Extras.AddAsync(c.Shop.BusinessId, second.Id, c.OtherId, "Beard dye", 80m, Staff);
        Assert.True((await records.SetPaidAmountAsync(c.Shop.BusinessId, second.Id, 500m, default, null, Staff)).IsSuccess);
        await CompletedAsync(c, 5, 11);                                                                                         // haircut 150 alone

        var report = (await new ReportService(c.Db, c.Clock).BuildRangeAsync(c.Shop.BusinessId, Day(5), Day(5)))!;
        Assert.Equal(3, report.Completed);
        Assert.Equal(450m + 500m + 150m, report.Revenue);                                                                       // payments, else what the services came to
        var lines = report.ByService.ToDictionary(l => l.Label);
        Assert.Equal((3, 450m), (lines["Haircut"].Completed, lines["Haircut"].Revenue));
        Assert.Equal((2, 600m), (lines["Color"].Completed, lines["Color"].Revenue));                                            // done twice, 300 each
        Assert.Equal((1, 80m), (lines["Other: Beard dye"].Completed, lines["Other: Beard dye"].Revenue));
        Assert.Equal(1130m, report.ByService.Sum(l => l.Revenue));                                                              // the service prices
        Assert.Equal(1130m, Assert.Single(report.ByStaff).Revenue);                                                             // credited to the booked staff member
        Assert.Equal(-30m, report.Adjustments);                                                                                 // revenue 1100 vs service prices 1130: a discount typed in the total
        Assert.Equal(1100m, report.ByService.Sum(l => l.Revenue) + report.Adjustments);                                       // the two always add up to the revenue
        Assert.Equal(1100m, (await new CustomerListService(c.Db).ListAsync(c.Shop.BusinessId, null)).Sum(x => x.Spent));
    }

    [Fact]
    public async Task The_discounts_given_include_the_services_added_while_a_promo_ran()
    {
        var c = await SetupAsync();
        Assert.True((await SetPromo(c, 20)).IsSuccess);
        var done = await CompletedAsync(c);                                                                                      // haircut 150 -> 120 (30 off)
        await c.Extras.AddAsync(c.Shop.BusinessId, done.Id, c.ColorId, null, null, Staff);                                      // color 300 -> 240 (60 off)

        var report = (await new ReportService(c.Db, c.Clock).BuildRangeAsync(c.Shop.BusinessId, Day(5), Day(5)))!;
        Assert.Equal(90m, report.Discounts);
        Assert.Equal(360m, report.Revenue);
        Assert.Equal(0m, report.Adjustments);
    }

    [Fact]
    public async Task A_customers_export_lists_the_services_done_on_top_and_deleting_the_customer_removes_them()
    {
        var c = await SetupAsync();
        var done = await CompletedAsync(c);
        await c.Extras.AddAsync(c.Shop.BusinessId, done.Id, c.ColorId, null, null, Staff);
        Guid customerId;
        await using (var ctx = c.Db.CreateDbContext())
            customerId = await ctx.Customers.Where(x => x.BusinessId == c.Shop.BusinessId).Select(x => x.Id).SingleAsync();
        var data = new CustomerDataService(c.Db);

        using var json = JsonDocument.Parse((await data.ExportJsonAsync(c.Shop.BusinessId, customerId))!);
        var row = Assert.Single(json.RootElement.GetProperty("servicesDoneOnTop").EnumerateArray());
        Assert.Equal(("Color", 300.00m, done.Id), (row.GetProperty("Service").GetString(), row.GetProperty("Price").GetDecimal(), row.GetProperty("BookingId").GetGuid()));

        Assert.True((await data.DeleteAsync(c.Shop.BusinessId, customerId, "Juan Dela Cruz")).IsSuccess);
        await using var check = c.Db.CreateDbContext();
        Assert.Equal(0, await check.BookingExtras.CountAsync(e => e.BookingId == done.Id));
    }

    // ---------------------------------------------------------------- the web pages

    private sealed record Owner(HttpClient Client, string Slug, Guid BusinessId, Guid ServiceId, Guid ColorId, Guid OtherId, TestDbFactory Db);

    private static async Task<Owner> RegisterOwnerAsync(AppFactory factory)
    {
        var client = HttpApp.NewClient(factory);
        var slug = HttpApp.Unique("bx");
        Assert.Equal(HttpStatusCode.Redirect, (await HttpApp.PostFormAsync(client, "/Register", new()
        {
            ["Input.Accept"] = "true", ["Input.BusinessName"] = "Receipt Shop", ["Input.Slug"] = slug, ["Input.OwnerName"] = "Ben Santos",
            ["Input.Email"] = $"{HttpApp.Unique("o")}@example.com", ["Input.Password"] = "correct-horse-1",
        })).StatusCode);
        var db = new TestDbFactory(await TestDb.ConnectionStringAsync());
        await using var ctx = db.CreateDbContext();
        var businessId = await ctx.Businesses.Where(b => b.Slug == slug).Select(b => b.Id).SingleAsync();
        var haircut = await ctx.Services.Where(s => s.BusinessId == businessId && !s.IsOther).Select(s => s.Id).SingleAsync();
        var other = await ctx.Services.Where(s => s.BusinessId == businessId && s.IsOther).Select(s => s.Id).SingleAsync();
        var color = Guid.NewGuid();
        await ctx.Services.Where(s => s.Id == haircut).ExecuteUpdateAsync(u => u.SetProperty(s => s.Price, 150m));
        ctx.Services.Add(new Service { Id = color, BusinessId = businessId, Name = "Color", Price = 300m, DurationMinutes = 60 });
        await ctx.SaveChangesAsync();
        return new Owner(client, slug, businessId, haircut, color, other, db);
    }

    [Fact]
    public async Task Staff_add_and_remove_services_on_the_page_after_Done_the_payment_total_follows_and_the_customer_gets_the_receipt()
    {
        var (factory, _) = await HttpApp.StartAsync();
        using var _f = factory;
        var o = await RegisterOwnerAsync(factory);
        var neighbour = await RegisterOwnerAsync(factory);
        var visitor = HttpApp.NewClient(factory);
        var bookings = new BookingService(o.Db, TimeProvider.System);
        var start = LocalTime.ToUtc(Seed.NextOpenDay().ToDateTime(new TimeOnly(10, 0)), LocalTime.Zone("Asia/Manila"));
        var made = (await bookings.CreateAsync(new CreateBookingRequest(o.BusinessId, o.ServiceId, null, start, "Juan", "09175550999", null,
            Guid.NewGuid().ToString("N"), BookingActor.Customer("Juan")))).Value!.Booking;
        await using (var ctx = o.Db.CreateDbContext())
            await ctx.Bookings.Where(b => b.Id == made.Id).ExecuteUpdateAsync(u => u.SetProperty(b => b.Status, BookingStatus.Completed));
        var page = $"/Admin/AfterVisit/{made.Id}";

        async Task<HttpResponseMessage> Post(string handler, Dictionary<string, string> fields, HttpClient? client = null) =>
            await HttpApp.PostFormAsync(client ?? o.Client, $"{page}?handler={handler}", fields, page);
        async Task<string> Html() => WebUtility.HtmlDecode(await o.Client.GetStringAsync(page));

        var html = await Html();
        Assert.Contains(Text.ExtraTitle, html);
        Assert.Contains($"{Text.ExtraItemsTotal}:</strong> ₱150.00", html);
        Assert.Contains("value=\"150.00\"", html);

        // Add a Color: the items total and the payment total move to 450.
        Assert.Equal(HttpStatusCode.Redirect, (await Post("AddService", new() { ["serviceId"] = o.ColorId.ToString() })).StatusCode);
        html = await Html();
        Assert.Contains(Text.ExtraAdded, html);
        Assert.Contains("<strong>Color</strong>", html);
        Assert.Contains($"{Text.ExtraItemsTotal}:</strong> ₱450.00", html);
        Assert.Contains("value=\"450.00\"", html);

        // Other with a typed description and price; a bad price is refused and nothing is added.
        Assert.Equal(HttpStatusCode.Redirect, (await Post("AddService", new() { ["serviceId"] = o.OtherId.ToString(), ["note"] = "Beard dye", ["price"] = "₱1,250.50" })).StatusCode);
        html = await Html();
        Assert.Contains("Other: Beard dye", html);
        Assert.Contains($"{Text.ExtraItemsTotal}:</strong> ₱1,700.50", html);
        await Post("AddService", new() { ["serviceId"] = o.OtherId.ToString(), ["note"] = "Wax", ["price"] = "abc" });
        Assert.Contains(Text.ExtraPriceInvalid, await Html());
        await Post("AddService", new() { ["serviceId"] = o.OtherId.ToString(), ["note"] = "", ["price"] = "50" });
        Assert.Contains(Text.ExtraNoteInvalid, await Html());
        Assert.Equal(2, (await new BookingExtraService(o.Db, TimeProvider.System).ListAsync(o.BusinessId, made.Id)).Count);

        // A saved payment total goes back to the items total when the services change, and the page says so.
        await Post("Payment", new() { ["paidAmount"] = "1800" });
        Assert.Contains("value=\"1800.00\"", await Html());
        Assert.Equal(HttpStatusCode.Redirect, (await Post("AddService", new() { ["serviceId"] = o.ColorId.ToString() })).StatusCode);
        html = await Html();
        Assert.Contains(Text.ExtraPaymentReset.Trim(), html);
        Assert.Contains("value=\"2000.50\"", html);

        // The customer's receipt.
        var manage = WebUtility.HtmlDecode(await visitor.GetStringAsync($"/booking/{made.ManageToken}"));
        Assert.Contains($"{Text.ManageAlsoDone}</dt><dd>Color", manage);
        Assert.Contains("Other: Beard dye", manage);
        Assert.Contains($"{Text.ManageTotalPaid}</dt><dd><strong>₱2,000.50</strong>", manage);

        // Another shop's owner cannot add, remove or even see the page of this visit.
        Assert.Equal(HttpStatusCode.NotFound, (await neighbour.Client.GetAsync(page)).StatusCode);
        var extraId = (await new BookingExtraService(o.Db, TimeProvider.System).ListAsync(o.BusinessId, made.Id))[0].Id;
        var forged = await HttpApp.PostFormAsync(neighbour.Client, $"/Admin/AfterVisit/{made.Id}?handler=RemoveService", new() { ["extraId"] = extraId.ToString() }, "/Admin");
        Assert.Equal(HttpStatusCode.NotFound, forged.StatusCode);
        Assert.Equal(3, (await new BookingExtraService(o.Db, TimeProvider.System).ListAsync(o.BusinessId, made.Id)).Count);

        // Remove one: the receipt and the totals follow.
        Assert.Equal(HttpStatusCode.Redirect, (await Post("RemoveService", new() { ["extraId"] = extraId.ToString() })).StatusCode);
        html = await Html();
        Assert.Contains(Text.ExtraRemoved, html);
        Assert.Equal(2, (await new BookingExtraService(o.Db, TimeProvider.System).ListAsync(o.BusinessId, made.Id)).Count);

        // The audit page shows the money changes.
        var audit = WebUtility.HtmlDecode(await o.Client.GetStringAsync("/Admin/Audit"));
        Assert.Contains("EXTRA_ADDED", audit);
        Assert.Contains("EXTRA_REMOVED", audit);
        Assert.Contains("PAYMENT_SET", audit);
    }

    [Fact]
    public async Task The_page_after_Done_offers_services_only_for_a_completed_visit()
    {
        var (factory, _) = await HttpApp.StartAsync();
        using var _f = factory;
        var o = await RegisterOwnerAsync(factory);
        var start = LocalTime.ToUtc(Seed.NextOpenDay().ToDateTime(new TimeOnly(11, 0)), LocalTime.Zone("Asia/Manila"));
        var made = (await new BookingService(o.Db, TimeProvider.System).CreateAsync(new CreateBookingRequest(o.BusinessId, o.ServiceId, null, start, "Maria", "09175550888", null,
            Guid.NewGuid().ToString("N"), BookingActor.Customer("Maria")))).Value!.Booking;

        Assert.Equal(HttpStatusCode.Redirect, (await o.Client.GetAsync($"/Admin/AfterVisit/{made.Id}")).StatusCode);          // not done: back to the day list
        var posted = await HttpApp.PostFormAsync(o.Client, $"/Admin/AfterVisit/{made.Id}?handler=AddService", new() { ["serviceId"] = o.ColorId.ToString() }, "/Admin");
        Assert.Equal(HttpStatusCode.Redirect, posted.StatusCode);
        Assert.Empty(await new BookingExtraService(o.Db, TimeProvider.System).ListAsync(o.BusinessId, made.Id));
    }
}
