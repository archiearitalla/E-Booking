using System.Net;
using VeskaFlow.Core.Domain;
using VeskaFlow.Web.Services;
using Microsoft.EntityFrameworkCore;
using static VeskaFlow.Web.Tests.Seed;

namespace VeskaFlow.Web.Tests;

/// <summary>The customer moving their own booking from the private link: cutoff, taken slots, audit, wrong links.</summary>
[Collection("http")]
public class CustomerMoveTests
{
    private static string Id(string prefix) => prefix + Guid.NewGuid().ToString("N")[..10];

    private sealed record Shop(TestDbFactory Db, TestClock Clock, BookingService Bookings, RegisteredOwner Owner, Guid ServiceId, Guid StaffId);

    private static async Task<Shop> ShopAsync()
    {
        var db = new TestDbFactory(await TestDb.ConnectionStringAsync());
        var clock = new TestClock(BaseNow);
        var owner = (await new OnboardingService(db, clock).RegisterAsync(
            new RegisterRequest("Cuts", Id("cm"), "Owner", $"{Id("o")}@example.com", "correct-horse-1", TrialDays: 30))).Value!;
        await using var ctx = db.CreateDbContext();
        var service = await ctx.Services.Where(s => s.BusinessId == owner.BusinessId && !s.IsOther).Select(s => s.Id).SingleAsync();
        var staff = await ctx.StaffMembers.Where(s => s.BusinessId == owner.BusinessId).Select(s => s.Id).SingleAsync();
        return new Shop(db, clock, new BookingService(db, clock), owner, service, staff);
    }

    private static async Task<BookingSummary> BookAsync(Shop s, int day, int hour, string phone) =>
        (await s.Bookings.CreateAsync(new CreateBookingRequest(s.Owner.BusinessId, s.ServiceId, s.StaffId, Manila(day, hour), "Maria", phone, null,
            Id("k"), BookingActor.Customer("Maria")))).Value!.Booking;

    [Fact]
    public async Task A_customer_moves_their_own_booking_to_a_free_time_and_it_is_audited_as_the_customer()
    {
        var s = await ShopAsync();
        var booking = await BookAsync(s, 5, 10, "09175550701");
        await using (var ctx = s.Db.CreateDbContext())
            await ctx.Bookings.Where(b => b.Id == booking.Id).ExecuteUpdateAsync(u => u.SetProperty(b => b.RemindedAtUtc, DateTime.UtcNow));

        var times = (await s.Bookings.GetRescheduleStartsByTokenAsync(booking.ManageToken, new DateOnly(2030, 3, 6))).Value!;
        Assert.Contains(Manila(6, 14), times);

        var moved = await s.Bookings.RescheduleByTokenAsync(booking.ManageToken, Manila(6, 14));
        Assert.True(moved.IsSuccess, moved.Message);
        Assert.Equal(Manila(6, 14), moved.Value!.StartsAtUtc);

        await using var check = s.Db.CreateDbContext();
        var row = await check.Bookings.SingleAsync(b => b.Id == booking.Id);
        Assert.Null(row.RemindedAtUtc); // due a reminder for the new time
        Assert.Equal(BookingStatus.Confirmed, row.Status);
        var audit = await check.AuditLogs.Where(a => a.BookingId == booking.Id && a.Action == "RESCHEDULED").SingleAsync();
        Assert.Equal("CUSTOMER", audit.ActorType);
        Assert.Equal("Maria", audit.ActorLabel);
    }

    [Fact]
    public async Task A_customer_cannot_move_onto_another_booking_but_may_shift_within_their_own_slot()
    {
        var s = await ShopAsync();
        var a = await BookAsync(s, 5, 10, "09175550702");
        await BookAsync(s, 5, 14, "09175550703");

        Assert.Equal(FailureKind.SlotTaken, (await s.Bookings.RescheduleByTokenAsync(a.ManageToken, Manila(5, 14))).Failure);
        Assert.True((await s.Bookings.RescheduleByTokenAsync(a.ManageToken, Manila(5, 11))).IsSuccess);
        // A made-up time that is not in the offered list is refused, not trusted.
        Assert.Equal(FailureKind.SlotTaken, (await s.Bookings.RescheduleByTokenAsync(a.ManageToken, Manila(5, 3))).Failure);
    }

    [Fact]
    public async Task The_customer_cutoff_applies_but_staff_can_still_move_after_it()
    {
        var s = await ShopAsync();
        var booking = await BookAsync(s, 5, 10, "09175550704");

        s.Clock.Now = new DateTimeOffset(Manila(5, 9), TimeSpan.Zero); // 1 hour before, cutoff is 2 hours
        var late = await s.Bookings.RescheduleByTokenAsync(booking.ManageToken, Manila(6, 14));
        Assert.Equal(FailureKind.PolicyViolation, late.Failure);
        Assert.Contains("2 hour", late.Message);
        Assert.Equal(FailureKind.PolicyViolation, (await s.Bookings.GetRescheduleStartsByTokenAsync(booking.ManageToken, new DateOnly(2030, 3, 6))).Failure);
        Assert.False((await s.Bookings.GetByTokenAsync(booking.ManageToken))!.CanMove);

        Assert.True((await s.Bookings.RescheduleAsync(s.Owner.BusinessId, booking.Id, Manila(6, 14), BookingActor.Staff(s.Owner.AdminUserId, "Owner"))).IsSuccess);
    }

    [Fact]
    public async Task A_cancelled_or_started_booking_cannot_be_moved_and_an_unknown_link_is_not_found()
    {
        var s = await ShopAsync();
        var cancelled = await BookAsync(s, 5, 10, "09175550705");
        Assert.True((await s.Bookings.CancelByTokenAsync(cancelled.ManageToken)).IsSuccess);
        Assert.Equal(FailureKind.PolicyViolation, (await s.Bookings.RescheduleByTokenAsync(cancelled.ManageToken, Manila(6, 14))).Failure);

        var started = await BookAsync(s, 5, 14, "09175550706");
        s.Clock.Now = new DateTimeOffset(Manila(5, 15), TimeSpan.Zero);
        Assert.Equal(FailureKind.PolicyViolation, (await s.Bookings.RescheduleByTokenAsync(started.ManageToken, Manila(6, 14))).Failure);

        Assert.Equal(FailureKind.NotFound, (await s.Bookings.RescheduleByTokenAsync("no-such-token", Manila(6, 14))).Failure);
        Assert.Equal(FailureKind.NotFound, (await s.Bookings.RescheduleByTokenAsync("  ", Manila(6, 14))).Failure);
        Assert.Equal(FailureKind.NotFound, (await s.Bookings.GetRescheduleStartsByTokenAsync("no-such-token", new DateOnly(2030, 3, 6))).Failure);
    }

    [Fact]
    public async Task One_link_moves_only_its_own_booking_even_between_two_shops()
    {
        var s = await ShopAsync();
        var other = await ShopAsync();
        var mine = await BookAsync(s, 5, 10, "09175550707");
        var theirs = await BookAsync(other, 5, 10, "09175550708");

        Assert.True((await s.Bookings.RescheduleByTokenAsync(mine.ManageToken, Manila(6, 14))).IsSuccess);
        await using var check = s.Db.CreateDbContext();
        Assert.Equal(Manila(5, 10), (await check.Bookings.SingleAsync(b => b.Id == theirs.Id)).StartsAtUtc); // untouched
    }

    [Fact]
    public async Task The_manage_and_move_pages_work_end_to_end_and_hide_the_button_when_it_is_closed()
    {
        var (factory, _) = await HttpApp.StartAsync();
        using var _f = factory;
        var db = new TestDbFactory(await TestDb.ConnectionStringAsync());
        var owner = (await new OnboardingService(db, TimeProvider.System).RegisterAsync(
            new RegisterRequest("Move Shop", Id("mv"), "Owner", $"{Id("o")}@example.com", "correct-horse-1", TrialDays: 30))).Value!;
        Guid serviceId, staffId;
        await using (var ctx = db.CreateDbContext())
        {
            serviceId = await ctx.Services.Where(x => x.BusinessId == owner.BusinessId && !x.IsOther).Select(x => x.Id).SingleAsync();
            staffId = await ctx.StaffMembers.Where(x => x.BusinessId == owner.BusinessId).Select(x => x.Id).SingleAsync();
        }
        var bookings = new BookingService(db, TimeProvider.System);
        var firstDay = NextOpenDay();
        var free = (await bookings.GetFreeStartsAsync(owner.BusinessId, serviceId, staffId, firstDay)).Value!;
        Assert.True(free.Count >= 2);
        var booking = (await bookings.CreateAsync(new CreateBookingRequest(owner.BusinessId, serviceId, staffId, free[0], "Maria", "09175550709", null,
            Id("k"), BookingActor.Customer("Maria")))).Value!.Booking;
        var token = booking.ManageToken;

        var visitor = HttpApp.NewClient(factory);
        Assert.Contains($"/booking/{token}/move", await visitor.GetStringAsync($"/booking/{token}"));

        var date = DisplayFormat.DateParam(firstDay);
        var movePage = $"/booking/{token}/move?date={date}";
        var page = await visitor.GetStringAsync(movePage);
        Assert.Contains("name=\"start\"", page);

        var target = free[1];
        var response = await HttpApp.PostFormAsync(visitor, $"/booking/{token}/move",
            new() { ["start"] = DisplayFormat.StartParam(target) }, tokenPage: movePage);
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.EndsWith($"/booking/{token}", response.Headers.Location!.OriginalString);
        Assert.Equal(target, (await bookings.GetByTokenAsync(token))!.Booking.StartsAtUtc);

        // An unknown link is not found on both pages.
        Assert.Equal(HttpStatusCode.NotFound, (await visitor.GetAsync("/booking/not-a-real-token/move")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await visitor.GetAsync("/booking/not-a-real-token")).StatusCode);

        // After cancelling there is nothing to move: the move page sends the customer back to the booking page.
        Assert.True((await bookings.CancelByTokenAsync(token)).IsSuccess);
        var closed = await visitor.GetAsync($"/booking/{token}/move");
        Assert.Equal(HttpStatusCode.Redirect, closed.StatusCode);
        Assert.DoesNotContain("/move", await visitor.GetStringAsync($"/booking/{token}"));
    }
}
