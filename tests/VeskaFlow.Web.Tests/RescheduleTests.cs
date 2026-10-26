using VeskaFlow.Core.Domain;
using VeskaFlow.Web.Services;
using Microsoft.EntityFrameworkCore;
using static VeskaFlow.Web.Tests.Seed;

namespace VeskaFlow.Web.Tests;

/// <summary>Staff moving a confirmed booking: availability, the audit trail, taken slots, and isolation between shops.</summary>
[Collection("http")]
public class RescheduleTests
{
    private static string Id(string prefix) => prefix + Guid.NewGuid().ToString("N")[..10];

    private sealed record Shop(TestDbFactory Db, TestClock Clock, BookingService Bookings, RegisteredOwner Owner, Guid ServiceId, Guid StaffId);

    private static async Task<Shop> ShopAsync()
    {
        var db = new TestDbFactory(await TestDb.ConnectionStringAsync());
        var clock = new TestClock(BaseNow);
        var owner = (await new OnboardingService(db, clock).RegisterAsync(
            new RegisterRequest("Cuts", Id("rs"), "Owner", $"{Id("o")}@example.com", "correct-horse-1", TrialDays: 30))).Value!;
        await using var ctx = db.CreateDbContext();
        var service = await ctx.Services.Where(s => s.BusinessId == owner.BusinessId && !s.IsOther).Select(s => s.Id).SingleAsync();
        var staff = await ctx.StaffMembers.Where(s => s.BusinessId == owner.BusinessId).Select(s => s.Id).SingleAsync();
        return new Shop(db, clock, new BookingService(db, clock), owner, service, staff);
    }

    private static async Task<BookingSummary> BookAsync(Shop s, int day, int hour, string phone) =>
        (await s.Bookings.CreateAsync(new CreateBookingRequest(s.Owner.BusinessId, s.ServiceId, s.StaffId, Manila(day, hour), "Maria", phone, null,
            Id("k"), BookingActor.Customer("Maria")))).Value!.Booking;

    private static BookingActor Staff(Shop s) => BookingActor.Staff(s.Owner.AdminUserId, "Owner");

    [Fact]
    public async Task Staff_can_move_a_booking_to_a_free_time_and_it_is_audited_and_due_a_new_reminder()
    {
        var s = await ShopAsync();
        var booking = await BookAsync(s, 5, 10, "09175550501");
        await using (var ctx = s.Db.CreateDbContext())
            await ctx.Bookings.Where(b => b.Id == booking.Id).ExecuteUpdateAsync(u => u.SetProperty(b => b.RemindedAtUtc, DateTime.UtcNow));

        var times = (await s.Bookings.GetRescheduleStartsAsync(s.Owner.BusinessId, booking.Id, new DateOnly(2030, 3, 6))).Value!;
        Assert.Contains(Manila(6, 14), times);

        var moved = await s.Bookings.RescheduleAsync(s.Owner.BusinessId, booking.Id, Manila(6, 14), Staff(s));
        Assert.True(moved.IsSuccess, moved.Message);
        Assert.Equal(Manila(6, 14), moved.Value!.StartsAtUtc);

        await using var check = s.Db.CreateDbContext();
        var row = await check.Bookings.SingleAsync(b => b.Id == booking.Id);
        Assert.Null(row.RemindedAtUtc);
        Assert.True(row.EndsAtUtc > row.StartsAtUtc);
        Assert.Contains(await check.AuditLogs.Where(a => a.BookingId == booking.Id).Select(a => a.Action).ToListAsync(), a => a == "RESCHEDULED");
    }

    [Fact]
    public async Task A_booking_may_shift_into_its_own_old_slot_but_not_onto_another_booking_and_never_after_it_started()
    {
        var s = await ShopAsync();
        var a = await BookAsync(s, 5, 10, "09175550502");
        var b = await BookAsync(s, 5, 14, "09175550503");

        Assert.Equal(FailureKind.SlotTaken, (await s.Bookings.RescheduleAsync(s.Owner.BusinessId, a.Id, Manila(5, 14), Staff(s))).Failure);
        Assert.True((await s.Bookings.RescheduleAsync(s.Owner.BusinessId, a.Id, Manila(5, 11), Staff(s))).IsSuccess);

        s.Clock.Now = new DateTimeOffset(Manila(5, 12), TimeSpan.Zero); // a has started
        Assert.Equal(FailureKind.PolicyViolation, (await s.Bookings.RescheduleAsync(s.Owner.BusinessId, a.Id, Manila(6, 10), Staff(s))).Failure);
        Assert.Equal(FailureKind.PolicyViolation, (await s.Bookings.RescheduleAsync(s.Owner.BusinessId, b.Id, Manila(6, 10), BookingActor.Customer("x"))).Failure);
    }

    [Fact]
    public async Task Another_shop_cannot_move_or_list_times_for_this_booking()
    {
        var s = await ShopAsync();
        var other = await ShopAsync();
        var booking = await BookAsync(s, 5, 10, "09175550504");
        Assert.Equal(FailureKind.NotFound, (await other.Bookings.RescheduleAsync(other.Owner.BusinessId, booking.Id, Manila(6, 10), Staff(other))).Failure);
        Assert.Equal(FailureKind.NotFound, (await other.Bookings.GetRescheduleStartsAsync(other.Owner.BusinessId, booking.Id, new DateOnly(2030, 3, 6))).Failure);
    }
}
