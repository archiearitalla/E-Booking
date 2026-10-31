using System.Net;
using VeskaFlow.Core.Domain;
using VeskaFlow.Web.Services;
using Microsoft.EntityFrameworkCore;
using static VeskaFlow.Web.Tests.Seed;

namespace VeskaFlow.Web.Tests;

/// <summary>A deposit paid by hand: the amount on the service, the snapshot on the booking, the staff mark, and what the customer sees.</summary>
[Collection("http")]
public class DepositTests
{
    private static string Id(string prefix) => prefix + Guid.NewGuid().ToString("N")[..10];

    private sealed record Shop(TestDbFactory Db, TestClock Clock, BookingService Bookings, CatalogAdminService Catalog, RegisteredOwner Owner, Guid ServiceId, Guid StaffId);

    private static async Task<Shop> ShopAsync(decimal price = 300m, decimal deposit = 100m)
    {
        var db = new TestDbFactory(await TestDb.ConnectionStringAsync());
        var clock = new TestClock(BaseNow);
        var owner = (await new OnboardingService(db, clock).RegisterAsync(
            new RegisterRequest("Deposit Shop", Id("dp"), "Owner", $"{Id("o")}@example.com", "correct-horse-1", TrialDays: 30))).Value!;
        await using var ctx = db.CreateDbContext();
        var service = await ctx.Services.Where(s => s.BusinessId == owner.BusinessId && !s.IsOther).SingleAsync();
        var staff = await ctx.StaffMembers.Where(s => s.BusinessId == owner.BusinessId).Select(s => s.Id).SingleAsync();
        var catalog = new CatalogAdminService(db, clock);
        var saved = await catalog.SaveServiceAsync(owner.BusinessId,
            new ServiceInput(service.Id, service.Name, service.DurationMinutes, service.BufferMinutes, price, true, service.SortOrder, service.FollowUpDays, true, deposit));
        Assert.True(saved.IsSuccess, string.Join(";", saved.FieldErrors.Values));
        return new Shop(db, clock, new BookingService(db, clock), catalog, owner, service.Id, staff);
    }

    private static async Task<BookingSummary> BookAsync(Shop s, int day, int hour, string phone) =>
        (await s.Bookings.CreateAsync(new CreateBookingRequest(s.Owner.BusinessId, s.ServiceId, s.StaffId, Manila(day, hour), "Maria", phone, null,
            Id("k"), BookingActor.Customer("Maria")))).Value!.Booking;

    private static BookingActor Staff(Shop s) => BookingActor.Staff(s.Owner.AdminUserId, "Owner");

    [Fact]
    public async Task A_deposit_must_be_between_zero_and_the_price_and_is_saved_on_the_service()
    {
        var s = await ShopAsync(price: 300m, deposit: 100m);
        var service = (await s.Catalog.GetServiceAsync(s.Owner.BusinessId, s.ServiceId))!;
        Assert.Equal(100m, service.DepositAmount);

        ServiceInput With(decimal deposit, decimal price = 300m) =>
            new(service.Id, service.Name, service.DurationMinutes, service.BufferMinutes, price, true, service.SortOrder, service.FollowUpDays, true, deposit);
        Assert.Contains("deposit", (await s.Catalog.SaveServiceAsync(s.Owner.BusinessId, With(300.01m))).FieldErrors.Keys); // more than the price
        Assert.Contains("deposit", (await s.Catalog.SaveServiceAsync(s.Owner.BusinessId, With(-1m))).FieldErrors.Keys);
        Assert.Contains("deposit", (await s.Catalog.SaveServiceAsync(s.Owner.BusinessId, With(50m, price: 0m))).FieldErrors.Keys); // a free service has none
        Assert.True((await s.Catalog.SaveServiceAsync(s.Owner.BusinessId, With(300m))).IsSuccess); // the whole price is allowed
        Assert.True((await s.Catalog.SaveServiceAsync(s.Owner.BusinessId, With(0m))).IsSuccess); // 0 switches it off
        Assert.Equal(0m, (await s.Catalog.GetServiceAsync(s.Owner.BusinessId, s.ServiceId))!.DepositAmount);
    }

    [Fact]
    public async Task The_booking_keeps_the_deposit_as_it_was_and_never_more_than_the_price()
    {
        var s = await ShopAsync(price: 300m, deposit: 100m);
        var first = await BookAsync(s, 5, 10, "09175550801");

        // The owner changes the deposit afterwards: the booking made before keeps its own amount.
        var service = (await s.Catalog.GetServiceAsync(s.Owner.BusinessId, s.ServiceId))!;
        await s.Catalog.SaveServiceAsync(s.Owner.BusinessId,
            new ServiceInput(service.Id, service.Name, service.DurationMinutes, service.BufferMinutes, 300m, true, service.SortOrder, service.FollowUpDays, true, 150m));
        var second = await BookAsync(s, 5, 14, "09175550802");

        // A deposit larger than the price (for example the price was lowered by hand in the database) is capped at the price.
        await using (var ctx = s.Db.CreateDbContext())
            await ctx.Services.Where(x => x.Id == s.ServiceId).ExecuteUpdateAsync(u => u.SetProperty(x => x.Price, 80m));
        var third = await BookAsync(s, 6, 10, "09175550803");

        await using var check = s.Db.CreateDbContext();
        Assert.Equal(100m, (await check.Bookings.SingleAsync(b => b.Id == first.Id)).DepositSnapshot);
        Assert.Equal(150m, (await check.Bookings.SingleAsync(b => b.Id == second.Id)).DepositSnapshot);
        Assert.Equal(80m, (await check.Bookings.SingleAsync(b => b.Id == third.Id)).DepositSnapshot);
        Assert.All(await check.Bookings.Where(b => b.BusinessId == s.Owner.BusinessId).ToListAsync(), b => Assert.Equal(BookingStatus.Confirmed, b.Status)); // the slot is held either way
    }

    [Fact]
    public async Task Staff_mark_the_deposit_received_and_can_undo_it_with_an_audit_trail()
    {
        var s = await ShopAsync();
        var booking = await BookAsync(s, 5, 10, "09175550804");

        Assert.True((await s.Bookings.SetDepositReceivedAsync(s.Owner.BusinessId, booking.Id, true, Staff(s))).IsSuccess);
        await using (var check = s.Db.CreateDbContext())
            Assert.NotNull((await check.Bookings.SingleAsync(b => b.Id == booking.Id)).DepositReceivedAtUtc);

        Assert.True((await s.Bookings.SetDepositReceivedAsync(s.Owner.BusinessId, booking.Id, true, Staff(s))).IsSuccess); // again: nothing changes
        Assert.True((await s.Bookings.SetDepositReceivedAsync(s.Owner.BusinessId, booking.Id, false, Staff(s))).IsSuccess);

        await using var ctx = s.Db.CreateDbContext();
        Assert.Null((await ctx.Bookings.SingleAsync(b => b.Id == booking.Id)).DepositReceivedAtUtc);
        var actions = await ctx.AuditLogs.Where(a => a.BookingId == booking.Id && a.Action.StartsWith("DEPOSIT")).OrderBy(a => a.Action).Select(a => a.Action).ToListAsync(); // the test clock is fixed, so both rows share one time
        Assert.Equal(["DEPOSIT_RECEIVED", "DEPOSIT_UNDONE"], actions); // one of each: the repeat left no extra row
    }

    [Fact]
    public async Task Only_staff_of_the_same_shop_can_mark_and_only_a_confirmed_booking_that_asks_for_a_deposit()
    {
        var s = await ShopAsync();
        var other = await ShopAsync();
        var booking = await BookAsync(s, 5, 10, "09175550805");

        Assert.Equal(FailureKind.PolicyViolation, (await s.Bookings.SetDepositReceivedAsync(s.Owner.BusinessId, booking.Id, true, BookingActor.Customer("Maria"))).Failure);
        Assert.Equal(FailureKind.NotFound, (await other.Bookings.SetDepositReceivedAsync(other.Owner.BusinessId, booking.Id, true, Staff(other))).Failure);
        Assert.Equal(FailureKind.NotFound, (await s.Bookings.SetDepositReceivedAsync(s.Owner.BusinessId, Guid.NewGuid(), true, Staff(s))).Failure);

        var cancelled = await BookAsync(s, 5, 14, "09175550806");
        s.Clock.Now = new DateTimeOffset(Manila(4, 9), TimeSpan.Zero);
        Assert.True((await s.Bookings.TransitionAsync(s.Owner.BusinessId, cancelled.Id, BookingStatus.Cancelled, Staff(s), reason: "test")).IsSuccess);
        Assert.Equal(FailureKind.PolicyViolation, (await s.Bookings.SetDepositReceivedAsync(s.Owner.BusinessId, cancelled.Id, true, Staff(s))).Failure);

        var free = await ShopAsync(price: 300m, deposit: 0m);
        var noDeposit = await BookAsync(free, 5, 10, "09175550807");
        Assert.Equal(FailureKind.PolicyViolation, (await free.Bookings.SetDepositReceivedAsync(free.Owner.BusinessId, noDeposit.Id, true, Staff(free))).Failure);
    }

    [Fact]
    public async Task Pay_instructions_are_saved_cleared_and_limited_in_length()
    {
        var s = await ShopAsync();
        var settings = new ShopSettingsService(s.Db, s.Clock);
        var current = (await settings.GetSettingsAsync(s.Owner.BusinessId))!;
        SettingsInput Input(string? pay) => new(current.Name, current.TimeZone, current.SlotMinutes, current.LeadTimeMinutes, current.HorizonDays, current.CancelCutoffHours, PayInstructions: pay);

        Assert.True((await settings.SaveSettingsAsync(s.Owner.BusinessId, Input("  GCash 0917 000 0000, Juan  "))).IsSuccess);
        Assert.Equal("GCash 0917 000 0000, Juan", (await settings.GetSettingsAsync(s.Owner.BusinessId))!.PayInstructions);
        Assert.True((await settings.SaveSettingsAsync(s.Owner.BusinessId, Input(null))).IsSuccess); // not sent: unchanged
        Assert.Equal("GCash 0917 000 0000, Juan", (await settings.GetSettingsAsync(s.Owner.BusinessId))!.PayInstructions);
        Assert.Contains("payInstructions", (await settings.SaveSettingsAsync(s.Owner.BusinessId, Input(new string('x', 301)))).FieldErrors.Keys);
        Assert.True((await settings.SaveSettingsAsync(s.Owner.BusinessId, Input(""))).IsSuccess);
        Assert.Null((await settings.GetSettingsAsync(s.Owner.BusinessId))!.PayInstructions);
    }

    [Fact]
    public async Task The_day_view_shows_the_deposit_and_the_button_marks_it_and_a_service_page_saves_it()
    {
        var (factory, _) = await HttpApp.StartAsync();
        using var _f = factory;
        var db = new TestDbFactory(await TestDb.ConnectionStringAsync());
        var email = $"{Id("o")}@example.com";
        var owner = (await new OnboardingService(db, TimeProvider.System).RegisterAsync(
            new RegisterRequest("Deposit Day", Id("dd"), "Owner", email, "correct-horse-1", TrialDays: 30))).Value!;
        Guid serviceId, staffId;
        await using (var ctx = db.CreateDbContext())
        {
            var service = await ctx.Services.Where(x => x.BusinessId == owner.BusinessId && !x.IsOther).SingleAsync();
            serviceId = service.Id;
            staffId = await ctx.StaffMembers.Where(x => x.BusinessId == owner.BusinessId).Select(x => x.Id).SingleAsync();
            service.Price = 400m;
        }
        var admin = HttpApp.NewClient(factory);
        Assert.Equal(HttpStatusCode.Redirect, (await HttpApp.PostFormAsync(admin, "/Login", new() { ["Input.Email"] = email, ["Input.Password"] = "correct-horse-1" })).StatusCode);

        // The Edit page saves a deposit (and refuses one above the price).
        var editPath = $"/Admin/Services/Edit/{serviceId}";
        Assert.Equal(HttpStatusCode.OK, (await HttpApp.PostFormAsync(admin, editPath, new()
        {
            ["Input.Name"] = "Haircut", ["Input.DurationMinutes"] = "30", ["Input.BufferMinutes"] = "0", ["Input.Price"] = "400.00",
            ["Input.Deposit"] = "500.00", ["Input.SortOrder"] = "0", ["Input.IsActive"] = "true", ["Input.PromoEligible"] = "true",
        }, tokenPage: editPath)).StatusCode); // the form is shown again with the error
        var saved = await HttpApp.PostFormAsync(admin, editPath, new()
        {
            ["Input.Name"] = "Haircut", ["Input.DurationMinutes"] = "30", ["Input.BufferMinutes"] = "0", ["Input.Price"] = "400.00",
            ["Input.Deposit"] = "150.00", ["Input.SortOrder"] = "0", ["Input.IsActive"] = "true", ["Input.PromoEligible"] = "true",
        }, tokenPage: editPath);
        Assert.Equal(HttpStatusCode.Redirect, saved.StatusCode);

        var bookings = new BookingService(db, TimeProvider.System);
        var day = NextOpenDay();
        var free = (await bookings.GetFreeStartsAsync(owner.BusinessId, serviceId, staffId, day)).Value!;
        var booking = (await bookings.CreateAsync(new CreateBookingRequest(owner.BusinessId, serviceId, staffId, free[0], "Maria", "09175550809", null,
            Id("k"), BookingActor.Customer("Maria")))).Value!.Booking;

        var path = $"/Admin?date={DisplayFormat.DateParam(day)}";
        var html = System.Net.WebUtility.HtmlDecode(await admin.GetStringAsync(path));
        Assert.Contains("Deposit received", html); // the button
        Assert.Contains("Not yet", html);

        var marked = await HttpApp.PostFormAsync(admin, "/Admin?handler=Deposit",
            new() { ["id"] = booking.Id.ToString(), ["received"] = "true", ["date"] = DisplayFormat.DateParam(day) }, tokenPage: path);
        Assert.Equal(HttpStatusCode.Redirect, marked.StatusCode);
        await using (var check = db.CreateDbContext())
            Assert.NotNull((await check.Bookings.SingleAsync(b => b.Id == booking.Id)).DepositReceivedAtUtc);
        Assert.Contains("Undo deposit received", System.Net.WebUtility.HtmlDecode(await admin.GetStringAsync(path)));

        // A booking id from another shop is "not found" for this login.
        var stranger = await ShopAsync();
        var foreign = await BookAsync(stranger, 5, 10, "09175550810");
        var refused = await HttpApp.PostFormAsync(admin, "/Admin?handler=Deposit",
            new() { ["id"] = foreign.Id.ToString(), ["received"] = "true" }, tokenPage: path);
        Assert.Equal(HttpStatusCode.NotFound, refused.StatusCode);
    }

    [Fact]
    public async Task The_customer_sees_the_deposit_and_how_to_pay_and_then_that_it_was_received()
    {
        var (factory, _) = await HttpApp.StartAsync();
        using var _f = factory;
        var db = new TestDbFactory(await TestDb.ConnectionStringAsync());
        var owner = (await new OnboardingService(db, TimeProvider.System).RegisterAsync(
            new RegisterRequest("Deposit Web", Id("dw"), "Owner", $"{Id("o")}@example.com", "correct-horse-1", TrialDays: 30))).Value!;
        Guid serviceId, staffId;
        await using (var ctx = db.CreateDbContext())
        {
            var service = await ctx.Services.Where(x => x.BusinessId == owner.BusinessId && !x.IsOther).SingleAsync();
            serviceId = service.Id;
            staffId = await ctx.StaffMembers.Where(x => x.BusinessId == owner.BusinessId).Select(x => x.Id).SingleAsync();
            service.Price = 400m;
            service.DepositAmount = 120m;
            (await ctx.Businesses.SingleAsync(b => b.Id == owner.BusinessId)).PayInstructions = "GCash 0917 123 4567 (Juan)";
            await ctx.SaveChangesAsync();
        }
        var bookings = new BookingService(db, TimeProvider.System);
        var day = NextOpenDay();
        var free = (await bookings.GetFreeStartsAsync(owner.BusinessId, serviceId, staffId, day)).Value!;
        var booking = (await bookings.CreateAsync(new CreateBookingRequest(owner.BusinessId, serviceId, staffId, free[0], "Maria", "09175550808", null,
            Id("k"), BookingActor.Customer("Maria")))).Value!.Booking;

        var visitor = HttpApp.NewClient(factory);
        var page = System.Net.WebUtility.HtmlDecode(await visitor.GetStringAsync($"/booking/{booking.ManageToken}"));
        Assert.Contains("Deposit: ", page);
        Assert.Contains("120", page);
        Assert.Contains("GCash 0917 123 4567 (Juan)", page);
        Assert.Contains("Your time stays booked", page);

        Assert.True((await bookings.SetDepositReceivedAsync(owner.BusinessId, booking.Id, true, BookingActor.Staff(owner.AdminUserId, "Owner"))).IsSuccess);
        var after = System.Net.WebUtility.HtmlDecode(await visitor.GetStringAsync($"/booking/{booking.ManageToken}"));
        Assert.Contains("was received", after);
        Assert.DoesNotContain("GCash 0917 123 4567", after);
    }
}
