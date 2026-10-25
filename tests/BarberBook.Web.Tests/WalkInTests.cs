using System.Net;
using BarberBook.Web.Data;
using BarberBook.Web.Services;
using Microsoft.EntityFrameworkCore;
using static BarberBook.Web.Tests.HttpApp;
using static BarberBook.Web.Tests.Seed;

namespace BarberBook.Web.Tests;

/// <summary>Staff-created walk-in bookings (BUSINESS_RULES 8.2): service level first, then the page over HTTP.</summary>
public class WalkInServiceTests
{
    private static string Id(string prefix) => prefix + Guid.NewGuid().ToString("N")[..10];

    private static async Task<(TestDbFactory Db, BookingService Bookings, RegisteredOwner Owner, Guid ServiceId, Guid StaffId)> SetupAsync()
    {
        var db = new TestDbFactory(await TestDb.ConnectionStringAsync());
        var clock = new TestClock(BaseNow);
        var owner = (await new OnboardingService(db, clock).RegisterAsync(
            new RegisterRequest("Clinic", Id("wi"), "Dr Reyes", $"{Id("o")}@example.com", "correct-horse-1", Industry: "clinic", TrialDays: 30))).Value!;
        await using var ctx = db.CreateDbContext();
        return (db, new BookingService(db, clock), owner,
            await ctx.Services.Where(s => s.BusinessId == owner.BusinessId).Select(s => s.Id).SingleAsync(),
            await ctx.StaffMembers.Where(s => s.BusinessId == owner.BusinessId).Select(s => s.Id).SingleAsync());
    }

    private static CreateBookingRequest Req(RegisteredOwner o, Guid service, Guid staff, DateTime start, string phone, BookingActor actor) =>
        new(o.BusinessId, service, staff, start, "Walk-in", phone, null, Id("k"), actor);

    [Fact]
    public async Task A_staff_walk_in_without_a_phone_is_its_own_customer_and_two_of_them_do_not_merge()
    {
        var (db, bookings, owner, service, staff) = await SetupAsync();
        var actor = BookingActor.Staff(owner.AdminUserId, "Dr Reyes");

        var first = await bookings.CreateAsync(Req(owner, service, staff, Manila(5, 10), "", actor));
        var second = await bookings.CreateAsync(Req(owner, service, staff, Manila(5, 11), "  ", actor));

        Assert.True(first.IsSuccess, first.Message);
        Assert.True(second.IsSuccess, second.Message);
        await using var ctx = db.CreateDbContext();
        var phones = await ctx.Customers.Where(c => c.BusinessId == owner.BusinessId).Select(c => c.Phone).ToListAsync();
        Assert.Equal(2, phones.Count);
        Assert.All(phones, p => Assert.True(DisplayFormat.IsPlaceholderPhone(p)));
        Assert.Equal(2, phones.Distinct().Count());
    }

    [Fact]
    public async Task A_staff_walk_in_with_a_phone_is_matched_with_that_customer_as_before()
    {
        var (db, bookings, owner, service, staff) = await SetupAsync();
        var actor = BookingActor.Staff(owner.AdminUserId, "Dr Reyes");

        Assert.True((await bookings.CreateAsync(Req(owner, service, staff, Manila(5, 10), "0917 000 0001", actor))).IsSuccess);
        Assert.True((await bookings.CreateAsync(Req(owner, service, staff, Manila(5, 11), "09170000001", actor))).IsSuccess);

        await using var ctx = db.CreateDbContext();
        Assert.Equal(1, await ctx.Customers.CountAsync(c => c.BusinessId == owner.BusinessId));
    }

    [Fact]
    public async Task A_customer_booking_still_needs_a_valid_phone_number()
    {
        var (_, bookings, owner, service, staff) = await SetupAsync();

        var result = await bookings.CreateAsync(Req(owner, service, staff, Manila(5, 10), "", BookingActor.Customer("Maria")));

        Assert.Equal(FailureKind.Validation, result.Failure);
        Assert.Contains("phone", result.FieldErrors.Keys);
    }

    [Fact]
    public async Task A_walk_in_cannot_take_a_time_that_is_already_booked()
    {
        var (_, bookings, owner, service, staff) = await SetupAsync();
        var actor = BookingActor.Staff(owner.AdminUserId, "Dr Reyes");
        Assert.True((await bookings.CreateAsync(Req(owner, service, staff, Manila(5, 10), "", actor))).IsSuccess);

        var clash = await bookings.CreateAsync(Req(owner, service, staff, Manila(5, 10), "", actor));

        Assert.Equal(FailureKind.SlotTaken, clash.Failure);
    }
}

[Collection("http")]
public class WalkInWebTests
{
    [Fact]
    public async Task The_owner_can_open_the_walk_in_page_and_book_one_and_it_shows_in_the_day_view_without_a_number()
    {
        var (factory, _) = await StartAsync();
        using var _f = factory;
        var client = NewClient(factory);
        var slug = Unique("wi");
        var reg = await PostFormAsync(client, "/Register", new()
        {
            ["Input.BusinessName"] = "Walk-in Clinic", ["Input.Slug"] = slug, ["Input.OwnerName"] = "Dr Reyes",
            ["Input.Email"] = $"{Unique("o")}@example.com", ["Input.Password"] = "correct-horse-1", ["Input.Industry"] = "clinic",
        });
        Assert.Equal(HttpStatusCode.Redirect, reg.StatusCode);

        var db = new TestDbFactory(await TestDb.ConnectionStringAsync());
        await using var ctx = db.CreateDbContext();
        var businessId = await ctx.Businesses.Where(b => b.Slug == slug).Select(b => b.Id).SingleAsync();
        var serviceId = await ctx.Services.Where(s => s.BusinessId == businessId).Select(s => s.Id).SingleAsync();

        var page = WebUtility.HtmlDecode(await client.GetStringAsync("/Admin/WalkIn"));
        Assert.Contains(Text.WalkInTitle, page);
        var listed = WebUtility.HtmlDecode(await client.GetStringAsync($"/Admin/WalkIn?serviceId={serviceId}"));
        Assert.True(listed.Contains(Text.WalkInNoTimes) || listed.Contains(Text.WalkInSave), "The page shows either free times or the no-times message.");

        // Book a time tomorrow or later (always inside working hours), as the page would post it.
        var svc = new BookingService(db, TimeProvider.System);
        var today = DateOnly.FromDateTime(DateTime.UtcNow.AddHours(8));
        DateTime? start = null;
        for (var d = 1; d < 14 && start is null; d++)
        {
            var free = await svc.GetFreeStartsAsync(businessId, serviceId, null, today.AddDays(d), walkIn: true);
            if (free.Value!.Count > 0) start = free.Value[0];
        }
        Assert.NotNull(start);

        var post = await PostFormAsync(client, "/Admin/WalkIn", new()
        {
            ["ServiceId"] = serviceId.ToString(), ["StaffId"] = "", ["Input.Start"] = DisplayFormat.StartParam(start!.Value),
            ["Input.Name"] = "Walk-in", ["Input.Phone"] = "", ["Input.IdempotencyKey"] = Unique("k"),
        }, tokenPage: "/Admin");

        Assert.Equal(HttpStatusCode.Redirect, post.StatusCode);
        Assert.Equal("/Admin", post.Headers.Location!.OriginalString);
        Assert.Equal(1, await ctx.Bookings.CountAsync(b => b.BusinessId == businessId && b.StartsAtUtc == start));

        var day = DateOnly.FromDateTime(start.Value.AddHours(8)).ToString("yyyy-MM-dd");
        var dayView = WebUtility.HtmlDecode(await client.GetStringAsync($"/Admin?date={day}"));
        Assert.Contains(Text.WalkInNoPhone, dayView);
        Assert.DoesNotContain("walkin-", dayView);
    }

    [Fact]
    public async Task The_walk_in_page_needs_a_sign_in()
    {
        var (factory, _) = await StartAsync();
        using var _f = factory;
        var anonymous = NewClient(factory);

        var response = await anonymous.GetAsync("/Admin/WalkIn");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Contains("/Login", response.Headers.Location!.OriginalString);
    }
}

/// <summary>Bugs found by clicking through the app by hand, kept as tests.</summary>
public class CancelAuditLabelTests
{
    [Fact]
    public async Task A_customer_cancellation_is_audited_under_the_customers_name_not_a_generic_label()
    {
        var shop = await Seed.CreateShopAsync();
        var db = new TestDbFactory(shop.ConnectionString);
        var svc = new BookingService(db, new TestClock(BaseNow));
        var created = await svc.CreateAsync(new CreateBookingRequest(
            shop.BusinessId, shop.ServiceId, shop.StaffIds[0], Manila(5, 10), "Maria Santos", "09175550123", null,
            "k" + Guid.NewGuid().ToString("N")[..10], BookingActor.Customer("Maria Santos")));
        Assert.True(created.IsSuccess, created.Message);

        var cancelled = await svc.CancelByTokenAsync(created.Value!.Booking.ManageToken);

        Assert.True(cancelled.IsSuccess, cancelled.Message);
        await using var ctx = db.CreateDbContext();
        var label = await ctx.AuditLogs.Where(a => a.BookingId == created.Value.Booking.Id && a.Action == "CANCELLED")
            .Select(a => a.ActorLabel).SingleAsync();
        Assert.Equal("Maria Santos", label);
    }
}
