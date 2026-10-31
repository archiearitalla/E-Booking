using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using VeskaFlow.Core.Domain;
using VeskaFlow.Core.Scheduling;
using VeskaFlow.Web;
using VeskaFlow.Web.Data;
using VeskaFlow.Web.Services;

namespace VeskaFlow.Web.Tests;

/// <summary>
/// The promo discount taken off bookings made while it runs, the regular and net prices shown to customers, the services that are
/// left out, the payment total entered when a visit is done, and what the reports count.
/// </summary>
[Collection("http")]
public class PromoPricingTests
{
    private static DateOnly Day(int d) => new(2030, 3, d);

    private sealed record Ctx(TestDbFactory Db, TestClock Clock, PromoBannerService Promo, BookingService Booking, Shop Shop);

    /// <summary>A shop whose one service costs 150.00, on the clock of Monday 2030-03-04, 08:00 in Manila.</summary>
    private static async Task<Ctx> SetupAsync()
    {
        var db = new TestDbFactory(await TestDb.ConnectionStringAsync());
        var clock = new TestClock(Seed.BaseNow);
        return new Ctx(db, clock, new PromoBannerService(db, clock), new BookingService(db, clock), await Seed.CreateShopAsync());
    }

    private static Task<ServiceResult<bool>> SetPromo(Ctx c, int? percent, bool active = true, DateOnly? lastDay = null, Shop? shop = null) =>
        c.Promo.SaveAsync((shop ?? c.Shop).BusinessId, null, false, "Promo", null, null, "plain", active, lastDay, percent);

    private static async Task<BookingSummary> BookAsync(Ctx c, DateTime start, Guid? serviceId = null, string? note = null, BookingActor? actor = null, Shop? shop = null)
    {
        var s = shop ?? c.Shop;
        var request = Seed.Request(s, start, actor: actor) with { ServiceId = serviceId ?? s.ServiceId, ServiceNote = note };
        var result = await c.Booking.CreateAsync(request);
        Assert.True(result.IsSuccess, result.Message);
        return result.Value!.Booking;
    }

    // ---------------------------------------------------------------- the price at booking time

    [Fact]
    public async Task A_booking_made_while_the_promo_runs_pays_the_discounted_price_and_keeps_both_prices_whatever_happens_later()
    {
        var c = await SetupAsync();
        Assert.True((await SetPromo(c, 20, lastDay: Day(5))).IsSuccess);                                                       // 20% until Tuesday

        var first = await BookAsync(c, Seed.Manila(5, 10));
        Assert.Equal((120.00m, 150.00m, 20), (first.Price, first.RegularPrice, first.DiscountPercent));

        // Booked now, for a day after the promo ends: still what the customer saw when booking.
        var later = await BookAsync(c, Seed.Manila(8, 10));
        Assert.Equal((120.00m, 150.00m, 20), (later.Price, later.RegularPrice, later.DiscountPercent));

        // Changing the promo and the price afterwards never touches bookings that exist.
        Assert.True((await SetPromo(c, 50)).IsSuccess);
        await using (var ctx = c.Db.CreateDbContext())
            await ctx.Services.Where(s => s.Id == c.Shop.ServiceId).ExecuteUpdateAsync(u => u.SetProperty(s => s.Price, 200m));
        var kept = (await c.Booking.LoadSummaryAsync(c.Shop.BusinessId, first.Id))!;
        Assert.Equal((120.00m, 150.00m, 20), (kept.Price, kept.RegularPrice, kept.DiscountPercent));
        var third = await BookAsync(c, Seed.Manila(6, 10));
        Assert.Equal((100.00m, 200.00m, 50), (third.Price, third.RegularPrice, third.DiscountPercent));                          // the new promo and price

        // The promo ends; a booking made after that pays the regular price and keeps no discount.
        Assert.True((await SetPromo(c, 50, lastDay: Day(5))).IsSuccess);
        c.Clock.Now = new DateTimeOffset(2030, 3, 6, 1, 0, 0, TimeSpan.Zero);
        var after = await BookAsync(c, Seed.Manila(9, 10));
        Assert.Equal(200.00m, after.Price);
        Assert.Null(after.RegularPrice);
        Assert.Null(after.DiscountPercent);
    }

    [Fact]
    public async Task A_service_left_out_the_Other_service_a_free_service_and_a_promo_that_is_off_or_has_no_discount_get_no_discount()
    {
        var c = await SetupAsync();
        Guid leftOut = Guid.NewGuid(), other = Guid.NewGuid(), free = Guid.NewGuid();
        await using (var ctx = c.Db.CreateDbContext())
        {
            foreach (var (id, name, price, isOther, eligible) in new[]
                { (leftOut, "Color", 300m, false, false), (other, "Other", 80m, true, true), (free, "Consult", 0m, false, true) })
            {
                ctx.Services.Add(new Service { Id = id, BusinessId = c.Shop.BusinessId, Name = name, Price = price, DurationMinutes = 30, IsOther = isOther, PromoEligible = eligible });
                ctx.StaffServices.Add(new StaffService { StaffId = c.Shop.StaffIds[0], ServiceId = id, BusinessId = c.Shop.BusinessId });
            }
            await ctx.SaveChangesAsync();
        }
        Assert.True((await SetPromo(c, 20)).IsSuccess);

        var inPromo = await BookAsync(c, Seed.Manila(5, 9));
        Assert.Equal(120.00m, inPromo.Price);
        foreach (var (id, hour, regular) in new[] { (leftOut, 10, 300m), (other, 11, 80m), (free, 12, 0m) })
        {
            var made = await BookAsync(c, Seed.Manila(5, hour), id, note: id == other ? "Beard dye" : null);
            Assert.Equal(regular, made.Price);
            Assert.Null(made.RegularPrice);
            Assert.Null(made.DiscountPercent);
        }

        Assert.True((await SetPromo(c, 20, active: false)).IsSuccess);                                                         // switched off
        Assert.Equal(150.00m, (await BookAsync(c, Seed.Manila(6, 9))).Price);
        Assert.True((await SetPromo(c, null)).IsSuccess);                                                                      // a banner only
        Assert.Equal(150.00m, (await BookAsync(c, Seed.Manila(6, 10))).Price);
        await c.Promo.RemoveAsync(c.Shop.BusinessId);                                                                          // no banner at all
        Assert.Equal(150.00m, (await BookAsync(c, Seed.Manila(6, 11))).Price);

        var other2 = await Seed.CreateShopAsync();                                                                             // another shop's promo is not ours
        Assert.True((await SetPromo(c, 90, shop: other2)).IsSuccess);
        Assert.Equal(150.00m, (await BookAsync(c, Seed.Manila(6, 12))).Price);
    }

    [Fact]
    public async Task A_walk_in_booked_by_staff_gets_the_promo_like_any_booking_made_while_it_runs()
    {
        var c = await SetupAsync();
        Assert.True((await SetPromo(c, 25)).IsSuccess);
        var walkIn = await BookAsync(c, Seed.Manila(5, 10), actor: BookingActor.Staff(Guid.NewGuid(), "Staff"));
        Assert.Equal((112.50m, 150.00m, 25), (walkIn.Price, walkIn.RegularPrice, walkIn.DiscountPercent));
    }

    [Fact]
    public async Task The_promo_form_checks_the_discount_and_the_database_agrees()
    {
        var c = await SetupAsync();
        foreach (var bad in new int?[] { 0, 91, -5, 1000 })
            Assert.Equal(Text.PromoDiscountInvalid, (await SetPromo(c, bad)).FieldErrors["discount"]);
        Assert.True((await SetPromo(c, 1)).IsSuccess);
        Assert.True((await SetPromo(c, 90)).IsSuccess);
        Assert.Equal(90, (await c.Promo.GetActivePercentAsync(c.Shop.BusinessId)));
        Assert.True((await SetPromo(c, 90, lastDay: Day(4))).IsSuccess);
        c.Clock.Now = new DateTimeOffset(2030, 3, 5, 1, 0, 0, TimeSpan.Zero);                                                    // after the last day
        Assert.Null(await c.Promo.GetActivePercentAsync(c.Shop.BusinessId));

        await using var ctx = c.Db.CreateDbContext();
        var id = c.Shop.BusinessId;
        var promo = await Assert.ThrowsAnyAsync<Exception>(() => ctx.Database.ExecuteSqlInterpolatedAsync($"UPDATE business_banner SET discount_percent = 95 WHERE business_id = {id}"));
        Assert.Contains("business_banner_discount", promo.ToString());
        await c.Promo.RemoveAsync(id);
        Assert.True((await SetPromo(c, 20)).IsSuccess);
        var booking = await BookAsync(c, Seed.Manila(9, 10));
        var pair = await Assert.ThrowsAnyAsync<Exception>(() => ctx.Database.ExecuteSqlInterpolatedAsync($"UPDATE booking SET regular_price_snapshot = NULL WHERE id = {booking.Id}"));
        Assert.Contains("booking_discount_pair", pair.ToString());
        var paid = await Assert.ThrowsAnyAsync<Exception>(() => ctx.Database.ExecuteSqlInterpolatedAsync($"UPDATE booking SET paid_amount = -1 WHERE id = {booking.Id}"));
        Assert.Contains("booking_paid_amount", paid.ToString());
    }

    // ---------------------------------------------------------------- the payment total

    private static async Task<BookingSummary> CompletedAsync(Ctx c, int day = 5, int hour = 10, Shop? shop = null)
    {
        var made = await BookAsync(c, Seed.Manila(day, hour), shop: shop);
        await using var ctx = c.Db.CreateDbContext();
        await ctx.Bookings.Where(b => b.Id == made.Id).ExecuteUpdateAsync(u => u.SetProperty(b => b.Status, BookingStatus.Completed));
        return made;
    }

    [Fact]
    public async Task The_payment_total_is_saved_for_a_completed_visit_only_and_only_in_range()
    {
        var c = await SetupAsync();
        var records = new VisitRecordService(c.Db);
        var confirmed = await BookAsync(c, Seed.Manila(5, 9));
        var done = await CompletedAsync(c);

        Assert.Equal(FailureKind.PolicyViolation, (await records.SetPaidAmountAsync(c.Shop.BusinessId, confirmed.Id, 100m)).Failure);   // not done yet
        Assert.True((await records.SetPaidAmountAsync(c.Shop.BusinessId, done.Id, 230.456m)).IsSuccess);
        Assert.Equal(230.46m, (await c.Booking.LoadSummaryAsync(c.Shop.BusinessId, done.Id))!.PaidAmount);                       // to the centavo
        Assert.True((await records.SetPaidAmountAsync(c.Shop.BusinessId, done.Id, 0m)).IsSuccess);                              // a free visit
        Assert.Equal(0m, (await c.Booking.LoadSummaryAsync(c.Shop.BusinessId, done.Id))!.PaidAmount);
        foreach (var bad in new[] { -0.01m, VisitRecordService.MaxPaidAmount + 1 })
            Assert.Equal(Text.AfterVisitPaymentInvalid, (await records.SetPaidAmountAsync(c.Shop.BusinessId, done.Id, bad)).FieldErrors["paidAmount"]);
        Assert.True((await records.SetPaidAmountAsync(c.Shop.BusinessId, done.Id, VisitRecordService.MaxPaidAmount)).IsSuccess);
        Assert.True((await records.SetPaidAmountAsync(c.Shop.BusinessId, done.Id, null)).IsSuccess);                            // cleared: the quoted price counts again
        Assert.Null((await c.Booking.LoadSummaryAsync(c.Shop.BusinessId, done.Id))!.PaidAmount);
    }

    [Fact]
    public async Task One_shop_or_a_limited_staff_login_cannot_set_the_payment_of_a_booking_that_is_not_theirs()
    {
        var c = await SetupAsync();
        var records = new VisitRecordService(c.Db);
        var other = await Seed.CreateShopAsync();
        var mine = await CompletedAsync(c);

        Assert.Equal(FailureKind.NotFound, (await records.SetPaidAmountAsync(other.BusinessId, mine.Id, 1m)).Failure);           // another shop's id
        Assert.Equal(FailureKind.NotFound, (await records.SetPaidAmountAsync(c.Shop.BusinessId, Guid.NewGuid(), 1m)).Failure);
        Assert.Equal(FailureKind.NotFound, (await records.SetPaidAmountAsync(c.Shop.BusinessId, mine.Id, 1m, default, Guid.NewGuid())).Failure); // another staff member's
        Assert.True((await records.SetPaidAmountAsync(c.Shop.BusinessId, mine.Id, 1m, default, c.Shop.StaffIds[0])).IsSuccess);
        Assert.Null((await c.Booking.LoadSummaryAsync(other.BusinessId, mine.Id)));                                             // and it cannot be read through them either
    }

    [Fact]
    public async Task Reports_and_customer_totals_count_the_payment_total_and_show_the_discounts_given()
    {
        var c = await SetupAsync();
        var records = new VisitRecordService(c.Db);
        Assert.True((await SetPromo(c, 20)).IsSuccess);
        var promoVisit = await CompletedAsync(c, 5, 9);                                                                         // quoted 120.00, regular 150.00
        var tipped = await CompletedAsync(c, 5, 10);                                                                            // quoted 120.00, paid 200.00 (added a service)
        Assert.True((await records.SetPaidAmountAsync(c.Shop.BusinessId, tipped.Id, 200m)).IsSuccess);
        await c.Promo.RemoveAsync(c.Shop.BusinessId);
        var regularVisit = await CompletedAsync(c, 5, 11);                                                                      // no promo: 150.00
        await using (var ctx = c.Db.CreateDbContext())                                                                          // a visit marked done by an old build: no payment, no discount
            await ctx.Bookings.Where(b => b.Id == regularVisit.Id).ExecuteUpdateAsync(u => u.SetProperty(b => b.PaidAmount, (decimal?)null));
        _ = promoVisit;

        var report = (await new ReportService(c.Db, c.Clock).BuildRangeAsync(c.Shop.BusinessId, Day(5), Day(5)))!;
        Assert.Equal(3, report.Completed);
        Assert.Equal(120.00m + 200.00m + 150.00m, report.Revenue);                                                              // the payment total where there is one
        Assert.Equal(60.00m, report.Discounts);                                                                                 // 30.00 given on each of the two promo bookings
        Assert.Equal(390.00m, Assert.Single(report.ByService).Revenue);                                                         // the lists show service prices: 120 + 120 + 150
        Assert.Equal(390.00m, Assert.Single(report.ByStaff).Revenue);
        Assert.Equal(80.00m, report.Adjustments);                                                                               // the 80.00 typed on top of one payment total

        var customers = await new CustomerListService(c.Db).ListAsync(c.Shop.BusinessId, null);
        Assert.Equal(470.00m, customers.Sum(x => x.Spent));
    }

    [Fact]
    public async Task A_customers_export_carries_the_regular_price_the_discount_and_the_payment_total()
    {
        var c = await SetupAsync();
        Assert.True((await SetPromo(c, 20)).IsSuccess);
        var done = await CompletedAsync(c);
        Assert.True((await new VisitRecordService(c.Db).SetPaidAmountAsync(c.Shop.BusinessId, done.Id, 135m)).IsSuccess);
        Guid customerId;
        await using (var ctx = c.Db.CreateDbContext())
            customerId = await ctx.Customers.Where(x => x.BusinessId == c.Shop.BusinessId).Select(x => x.Id).SingleAsync();

        using var json = JsonDocument.Parse((await new CustomerDataService(c.Db).ExportJsonAsync(c.Shop.BusinessId, customerId))!);
        var row = json.RootElement.GetProperty("bookings")[0];
        Assert.Equal(120.00m, row.GetProperty("Price").GetDecimal());
        Assert.Equal(150.00m, row.GetProperty("RegularPrice").GetDecimal());
        Assert.Equal(20, row.GetProperty("DiscountPercent").GetInt32());
        Assert.Equal(135.00m, row.GetProperty("PaidAmount").GetDecimal());
    }

    // ---------------------------------------------------------------- the assistant

    [Fact]
    public async Task The_assistant_is_told_both_prices_and_a_booking_it_makes_gets_the_promo()
    {
        var db = new TestDbFactory(await TestDb.ConnectionStringAsync());
        var clock = new TestClock(Seed.BaseNow);
        var shop = await Seed.CreateShopAsync();
        var promo = new PromoBannerService(db, clock);
        Assert.True((await promo.SaveAsync(shop.BusinessId, null, false, "Promo", null, null, "plain", true, null, 20)).IsSuccess);
        await using (var ctx = db.CreateDbContext())
            await ctx.Businesses.Where(b => b.Id == shop.BusinessId).ExecuteUpdateAsync(u => u.SetProperty(b => b.AiAddonUntilUtc, Seed.BaseNow.UtcDateTime.AddDays(30)));
        string slug;
        await using (var ctx = db.CreateDbContext()) slug = await ctx.Businesses.Where(b => b.Id == shop.BusinessId).Select(b => b.Slug).SingleAsync();

        var model = new FakeAiModel((n, _) => n == 1
            ? FakeAiModel.Uses(FakeAiModel.Use("a", "create_booking", new { service_id = shop.ServiceId, date = "2030-03-05", time = "10:00", customer_name = "Mark", phone = "09171234567" }))
            : FakeAiModel.Reply("Booked."));
        var access = new AiAccess(db, clock, new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build(), model);
        var chat = new AiReceptionistService(new ShopCatalogService(db, clock), new BookingService(db, clock), access, model, db, clock,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<AiReceptionistService>.Instance);

        Assert.True((await chat.SendAsync(slug, null, "book me 10am", "https://flow.example.com")).IsSuccess);

        Assert.Contains("₱120.00 now (regular ₱150.00, 20% off)", model.Systems[0]);
        Assert.Contains("tell the customer both the promo price and the regular price", model.Systems[0]);
        var result = model.LastToolResult();
        Assert.False(result.IsError);
        using var json = JsonDocument.Parse(result.Text);
        Assert.Equal(("₱120.00", "₱150.00", 20), (json.RootElement.GetProperty("price").GetString(), json.RootElement.GetProperty("regular_price").GetString(), json.RootElement.GetProperty("discount_percent").GetInt32()));
        await using var check = db.CreateDbContext();
        var booking = await check.Bookings.SingleAsync(b => b.BusinessId == shop.BusinessId);
        Assert.Equal((120.00m, 150.00m, 20), (booking.PriceSnapshot, booking.RegularPriceSnapshot, booking.DiscountPercentSnapshot));
    }

    // ---------------------------------------------------------------- the web pages

    private sealed record Owner(HttpClient Client, string Slug, Guid BusinessId, Guid ServiceId, TestDbFactory Db);

    private static async Task<Owner> RegisterOwnerAsync(AppFactory factory, decimal servicePrice = 150m)
    {
        var client = HttpApp.NewClient(factory);
        var slug = HttpApp.Unique("pp");
        Assert.Equal(HttpStatusCode.Redirect, (await HttpApp.PostFormAsync(client, "/Register", new()
        {
            ["Input.Accept"] = "true", ["Input.BusinessName"] = "Price Shop", ["Input.Slug"] = slug, ["Input.OwnerName"] = "Ben Santos",
            ["Input.Email"] = $"{HttpApp.Unique("o")}@example.com", ["Input.Password"] = "correct-horse-1",
        })).StatusCode);
        var db = new TestDbFactory(await TestDb.ConnectionStringAsync());
        await using var ctx = db.CreateDbContext();
        var businessId = await ctx.Businesses.Where(b => b.Slug == slug).Select(b => b.Id).SingleAsync();
        var serviceId = await ctx.Services.Where(s => s.BusinessId == businessId && !s.IsOther).Select(s => s.Id).SingleAsync();
        await ctx.Services.Where(s => s.Id == serviceId).ExecuteUpdateAsync(u => u.SetProperty(s => s.Price, servicePrice));
        return new Owner(client, slug, businessId, serviceId, db);
    }

    private static async Task<HttpResponseMessage> PostPromoAsync(Owner o, string discount, string badge = "", string caption = "Big sale")
    {
        var page = await o.Client.GetStringAsync("/Admin/Promo");
        using var form = new MultipartFormDataContent
        {
            { new StringContent(HttpApp.TokenIn(page, "/Admin/Promo")), "__RequestVerificationToken" },
            { new StringContent(caption), "Input.Caption" }, { new StringContent("plain"), "Input.Theme" },
            { new StringContent(discount), "Input.Discount" }, { new StringContent("true"), "Input.Active" },
        };
        if (badge.Length > 0) form.Add(new StringContent(badge), "Input.Badge");
        return await o.Client.PostAsync("/Admin/Promo", form);
    }

    [Fact]
    public async Task The_customer_sees_the_regular_price_and_the_promo_price_on_every_page_and_the_total_paid_when_the_visit_is_done()
    {
        var (factory, _) = await HttpApp.StartAsync();
        using var _f = factory;
        var owner = await RegisterOwnerAsync(factory);
        var visitor = HttpApp.NewClient(factory);

        // No promo yet: just the price.
        var before = WebUtility.HtmlDecode(await visitor.GetStringAsync($"/b/{owner.Slug}"));
        Assert.Contains("Haircut · ₱150.00<", before.Replace("</option>", "<"));
        Assert.DoesNotContain("(was ", before);

        // A discount typed as text or outside 1-90 is refused and nothing changes.
        foreach (var bad in new[] { "abc", "12.5", "0", "91" })
        {
            var refused = await PostPromoAsync(owner, bad);
            Assert.Equal(HttpStatusCode.OK, refused.StatusCode);
            Assert.Contains(Text.PromoDiscountInvalid, WebUtility.HtmlDecode(await refused.Content.ReadAsStringAsync()));
        }
        Assert.DoesNotContain("(was ", await visitor.GetStringAsync($"/b/{owner.Slug}"));

        // A 20% promo with no badge of its own: the card says the discount for the owner.
        Assert.Equal(HttpStatusCode.Redirect, (await PostPromoAsync(owner, "20")).StatusCode);
        var page = WebUtility.HtmlDecode(await visitor.GetStringAsync($"/b/{owner.Slug}"));
        Assert.Contains("Haircut · ₱120.00 (was ₱150.00)", page);
        Assert.Contains(">20% OFF</p>", page);
        Assert.Equal(HttpStatusCode.Redirect, (await PostPromoAsync(owner, "20", badge: "FREE WASH")).StatusCode);                  // an own badge wins
        page = WebUtility.HtmlDecode(await visitor.GetStringAsync($"/b/{owner.Slug}"));
        Assert.Contains(">FREE WASH</p>", page);
        Assert.DoesNotContain(">20% OFF</p>", page);

        // The owner sees the price list with and without the promo, and leaves a service out in its own page.
        var admin = WebUtility.HtmlDecode(await owner.Client.GetStringAsync("/Admin/Promo"));
        Assert.Contains("₱150.00", admin);
        Assert.Contains("₱120.00", admin);                                                                                       // the net price of the haircut
        Assert.Contains("id=\"promo-prices\"", admin);

        // The confirm page for a free time.
        var day = Seed.NextOpenDay();
        var start = LocalTime.ToUtc(day.ToDateTime(new TimeOnly(10, 0)), LocalTime.Zone("Asia/Manila"));
        var confirm = WebUtility.HtmlDecode(await visitor.GetStringAsync($"/b/{owner.Slug}/confirm?serviceId={owner.ServiceId}&start={DisplayFormat.StartParam(start)}"));
        Assert.Matches("<s class=\"price-was\"><span class=\"sr-only\">Regular price </span>₱150.00</s> <strong class=\"price-now\">₱120.00</strong> <span class=\"badge\">20% off</span>", confirm);

        // The booking made with the promo: the manage page shows both prices; once done it shows the total paid.
        var made = (await new BookingService(owner.Db, TimeProvider.System).CreateAsync(new CreateBookingRequest(
            owner.BusinessId, owner.ServiceId, null, start, "Juan", "09175550123", null, Guid.NewGuid().ToString("N"), BookingActor.Customer("Juan")))).Value!.Booking;
        var manage = WebUtility.HtmlDecode(await visitor.GetStringAsync($"/booking/{made.ManageToken}"));
        Assert.Contains("₱150.00</s>", manage);
        Assert.Contains("₱120.00</strong>", manage);
        Assert.Contains("20% off", manage);
        Assert.DoesNotContain(Text.ManageTotalPaid, manage);

        await using (var ctx = owner.Db.CreateDbContext())
            await ctx.Bookings.Where(b => b.Id == made.Id).ExecuteUpdateAsync(u => u.SetProperty(b => b.Status, BookingStatus.Completed));
        manage = WebUtility.HtmlDecode(await visitor.GetStringAsync($"/booking/{made.ManageToken}"));
        Assert.Contains($"{Text.ManageTotalPaid}</dt><dd><strong>₱120.00</strong>", manage);                                       // no payment entered: the quoted price
        Assert.True((await new VisitRecordService(owner.Db).SetPaidAmountAsync(owner.BusinessId, made.Id, 1234.5m)).IsSuccess);
        manage = WebUtility.HtmlDecode(await visitor.GetStringAsync($"/booking/{made.ManageToken}"));
        Assert.Contains($"{Text.ManageTotalPaid}</dt><dd><strong>₱1,234.50</strong>", manage);
    }

    [Fact]
    public async Task Staff_enter_the_payment_total_on_the_page_that_opens_after_Done_and_it_starts_at_the_quoted_price()
    {
        var (factory, _) = await HttpApp.StartAsync();
        using var _f = factory;
        var owner = await RegisterOwnerAsync(factory);
        var bookings = new BookingService(owner.Db, TimeProvider.System);
        await new PromoBannerService(owner.Db, TimeProvider.System).SaveAsync(owner.BusinessId, null, false, "Promo", null, null, "plain", true, null, 20);
        var start = LocalTime.ToUtc(Seed.NextOpenDay().ToDateTime(new TimeOnly(10, 0)), LocalTime.Zone("Asia/Manila"));
        var made = (await bookings.CreateAsync(new CreateBookingRequest(owner.BusinessId, owner.ServiceId, null, start, "Juan", "09175550124", null,
            Guid.NewGuid().ToString("N"), BookingActor.Customer("Juan")))).Value!.Booking;

        // Not done yet: the page sends staff back to the day list.
        Assert.Equal(HttpStatusCode.Redirect, (await owner.Client.GetAsync($"/Admin/AfterVisit/{made.Id}")).StatusCode);
        await using (var ctx = owner.Db.CreateDbContext())
            await ctx.Bookings.Where(b => b.Id == made.Id).ExecuteUpdateAsync(u => u.SetProperty(b => b.Status, BookingStatus.Completed));

        var html = WebUtility.HtmlDecode(await owner.Client.GetStringAsync($"/Admin/AfterVisit/{made.Id}"));
        Assert.Contains(Text.AfterVisitPaymentTitle, html);
        Assert.Contains("name=\"paidAmount\"", html);
        Assert.Contains("value=\"120.00\"", html);                                                                                // the quoted price, after the promo
        Assert.Contains("₱150.00</s>", html);

        async Task<HttpResponseMessage> Save(string typed) => await HttpApp.PostFormAsync(owner.Client, $"/Admin/AfterVisit/{made.Id}?handler=Payment",
            new() { ["paidAmount"] = typed }, $"/Admin/AfterVisit/{made.Id}");

        Assert.Equal(HttpStatusCode.Redirect, (await Save("₱1,250.50")).StatusCode);                                              // a peso sign and a comma are fine
        Assert.Equal(1250.50m, (await bookings.LoadSummaryAsync(owner.BusinessId, made.Id))!.PaidAmount);
        html = WebUtility.HtmlDecode(await owner.Client.GetStringAsync($"/Admin/AfterVisit/{made.Id}"));
        Assert.Contains(Text.AfterVisitPaymentSaved, html);
        Assert.Contains("value=\"1250.50\"", html);

        Assert.Equal(HttpStatusCode.Redirect, (await Save("abc")).StatusCode);                                                   // refused, nothing changes
        Assert.Contains(Text.AfterVisitPaymentInvalid, WebUtility.HtmlDecode(await owner.Client.GetStringAsync($"/Admin/AfterVisit/{made.Id}")));
        Assert.Equal(1250.50m, (await bookings.LoadSummaryAsync(owner.BusinessId, made.Id))!.PaidAmount);
        await Save("-5");
        await Save("99999999");
        Assert.Equal(1250.50m, (await bookings.LoadSummaryAsync(owner.BusinessId, made.Id))!.PaidAmount);

        Assert.Equal(HttpStatusCode.Redirect, (await Save("")).StatusCode);                                                       // blank: the quoted price counts again
        Assert.Null((await bookings.LoadSummaryAsync(owner.BusinessId, made.Id))!.PaidAmount);

        var day = WebUtility.HtmlDecode(await owner.Client.GetStringAsync($"/Admin?date={DisplayFormat.DateParam(LocalTime.LocalDateOf(start, "Asia/Manila"))}&status=done"));
        Assert.Contains("₱120.00", day);                                                                                          // the day view quotes it
    }

    // ---------------------------------------------------------------- unticked boxes

    [Fact]
    public async Task An_unticked_box_means_off_in_the_service_and_staff_forms_and_a_new_one_starts_ticked()
    {
        var (factory, _) = await HttpApp.StartAsync();
        using var _f = factory;
        var owner = await RegisterOwnerAsync(factory);

        // A new service and a new person start active (and the service starts in the promo).
        var newService = WebUtility.HtmlDecode(await owner.Client.GetStringAsync("/Admin/Services/Edit"));
        Assert.Matches("name=\"Input.IsActive\" value=\"true\" checked", newService);
        Assert.Matches("name=\"Input.PromoEligible\" value=\"true\" checked", newService);
        Assert.Matches("name=\"Input.IsActive\" value=\"true\" checked", WebUtility.HtmlDecode(await owner.Client.GetStringAsync("/Admin/Staff/Edit")));

        async Task<HttpResponseMessage> SaveService(bool active, bool promo) => await HttpApp.PostFormAsync(owner.Client, $"/Admin/Services/Edit/{owner.ServiceId}",
            ServiceFields(active, promo), $"/Admin/Services/Edit/{owner.ServiceId}");

        // The browser sends nothing for an unticked box.
        Assert.Equal(HttpStatusCode.Redirect, (await SaveService(active: false, promo: false)).StatusCode);
        await using (var ctx = owner.Db.CreateDbContext())
        {
            var service = await ctx.Services.AsNoTracking().SingleAsync(s => s.Id == owner.ServiceId);
            Assert.False(service.IsActive);
            Assert.False(service.PromoEligible);
        }
        var form = WebUtility.HtmlDecode(await owner.Client.GetStringAsync($"/Admin/Services/Edit/{owner.ServiceId}"));
        Assert.DoesNotMatch("name=\"Input.IsActive\" value=\"true\" checked", form);                                               // the form shows what is saved
        Assert.DoesNotMatch("name=\"Input.PromoEligible\" value=\"true\" checked", form);
        Assert.Contains(Text.ServiceNotInPromo, WebUtility.HtmlDecode(await owner.Client.GetStringAsync("/Admin/Services")));

        Assert.Equal(HttpStatusCode.Redirect, (await SaveService(active: true, promo: true)).StatusCode);
        await using (var ctx = owner.Db.CreateDbContext())
        {
            var service = await ctx.Services.AsNoTracking().SingleAsync(s => s.Id == owner.ServiceId);
            Assert.True(service.IsActive);
            Assert.True(service.PromoEligible);
        }

        // A second person, then switched off with an unticked box.
        var added = await HttpApp.PostFormAsync(owner.Client, "/Admin/Staff/Edit?handler=Save",
            new() { ["Input.Name"] = "Carl", ["Input.SortOrder"] = "1", ["Input.IsActive"] = "true", ["Input.ServiceIds"] = owner.ServiceId.ToString() }, "/Admin/Staff/Edit");
        Assert.Equal(HttpStatusCode.Redirect, added.StatusCode);
        Guid carl;
        await using (var ctx = owner.Db.CreateDbContext()) carl = await ctx.StaffMembers.Where(s => s.BusinessId == owner.BusinessId && s.Name == "Carl").Select(s => s.Id).SingleAsync();
        var off = await HttpApp.PostFormAsync(owner.Client, $"/Admin/Staff/Edit/{carl}?handler=Save",
            new() { ["Input.Name"] = "Carl", ["Input.SortOrder"] = "1", ["Input.ServiceIds"] = owner.ServiceId.ToString() }, $"/Admin/Staff/Edit/{carl}");
        Assert.Equal(HttpStatusCode.Redirect, off.StatusCode);
        await using var check = owner.Db.CreateDbContext();
        Assert.False((await check.StaffMembers.AsNoTracking().SingleAsync(s => s.Id == carl)).IsActive);
    }

    private static Dictionary<string, string> ServiceFields(bool active, bool promo)
    {
        var fields = new Dictionary<string, string>
            { ["Input.Name"] = "Haircut", ["Input.DurationMinutes"] = "30", ["Input.BufferMinutes"] = "0", ["Input.Price"] = "150.00", ["Input.SortOrder"] = "0" };
        if (active) fields["Input.IsActive"] = "true";
        if (promo) fields["Input.PromoEligible"] = "true";
        return fields;
    }
}
