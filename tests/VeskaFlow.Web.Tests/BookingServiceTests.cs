using VeskaFlow.Core.Domain;
using VeskaFlow.Core.Scheduling;
using VeskaFlow.Web.Data;
using VeskaFlow.Web.Services;
using Microsoft.EntityFrameworkCore;
using static VeskaFlow.Web.Tests.Seed;

namespace VeskaFlow.Web.Tests;

public class BookingServiceTests
{
    private static TestClock Clock(DateTimeOffset? at = null) => new(at ?? BaseNow);

    private static async Task<int> CountBookingsAsync(Shop shop, Func<IQueryable<Booking>, IQueryable<Booking>>? filter = null)
    {
        await using var db = new TestDbFactory(shop.ConnectionString).CreateDbContext();
        var q = db.Bookings.Where(b => b.BusinessId == shop.BusinessId);
        return await (filter?.Invoke(q) ?? q).CountAsync();
    }

    // ------------------------------------------------------------ concurrency

    [Fact]
    public async Task Fifty_parallel_creates_for_one_slot_yield_exactly_one_booking()
    {
        var shop = await CreateShopAsync();
        var svc = ServiceFor(shop, Clock());
        var slot = Manila(5, 10);

        var results = await Task.WhenAll(Enumerable.Range(0, 50).Select(i =>
            Task.Run(() => svc.CreateAsync(Request(shop, slot, phone: $"0917000{i:0000}", staffId: shop.StaffIds[0])))));

        Assert.Equal(1, results.Count(r => r.IsSuccess));
        Assert.Equal(49, results.Count(r => r.Failure == FailureKind.SlotTaken));
        Assert.Equal(1, await CountBookingsAsync(shop));
    }

    [Fact]
    public async Task Any_available_with_two_staff_books_two_then_reports_slot_taken()
    {
        var shop = await CreateShopAsync(staffCount: 2);
        var svc = ServiceFor(shop, Clock());
        var slot = Manila(5, 10);

        var results = await Task.WhenAll(Enumerable.Range(0, 3).Select(i =>
            Task.Run(() => svc.CreateAsync(Request(shop, slot, phone: $"091700000{i}")))));

        var ok = results.Where(r => r.IsSuccess).ToList();
        Assert.Equal(2, ok.Count);
        Assert.Equal(2, ok.Select(r => r.Value!.Booking.StaffId).Distinct().Count());
        Assert.Equal(1, results.Count(r => r.Failure == FailureKind.SlotTaken));
        Assert.Equal(2, await CountBookingsAsync(shop));
    }

    [Fact]
    public async Task Any_available_prefers_the_staff_member_with_fewer_bookings_that_day()
    {
        var shop = await CreateShopAsync(staffCount: 2);
        var svc = ServiceFor(shop, Clock());

        var first = await svc.CreateAsync(Request(shop, Manila(5, 10), staffId: shop.StaffIds[0]));
        Assert.True(first.IsSuccess);

        var second = await svc.CreateAsync(Request(shop, Manila(5, 14)));
        Assert.True(second.IsSuccess);
        Assert.Equal(shop.StaffIds[1], second.Value!.Booking.StaffId);
    }

    // ------------------------------------------------------------ idempotency

    [Fact]
    public async Task Same_idempotency_key_returns_the_same_booking_and_creates_one_row()
    {
        var shop = await CreateShopAsync();
        var svc = ServiceFor(shop, Clock());

        var a = await svc.CreateAsync(Request(shop, Manila(5, 10), key: "key-1"));
        var b = await svc.CreateAsync(Request(shop, Manila(5, 10), key: "key-1"));

        Assert.True(a.IsSuccess && b.IsSuccess);
        Assert.False(a.Value!.AlreadyExisted);
        Assert.True(b.Value!.AlreadyExisted);
        Assert.Equal(a.Value.Booking.Id, b.Value.Booking.Id);
        Assert.Equal(1, await CountBookingsAsync(shop));
    }

    [Fact]
    public async Task Parallel_requests_with_one_key_create_one_booking()
    {
        var shop = await CreateShopAsync();
        var svc = ServiceFor(shop, Clock());

        var results = await Task.WhenAll(Enumerable.Range(0, 10).Select(_ =>
            Task.Run(() => svc.CreateAsync(Request(shop, Manila(5, 10), phone: "09175550000", key: "same-key")))));

        Assert.All(results, r => Assert.True(r.IsSuccess));
        Assert.Single(results.Select(r => r.Value!.Booking.Id).Distinct());
        Assert.Equal(1, await CountBookingsAsync(shop));
    }

    // ------------------------------------------------------------ server-side availability rules

    [Fact]
    public async Task Lead_time_is_enforced_on_the_server()
    {
        var shop = await CreateShopAsync();
        var svc = ServiceFor(shop, Clock(BaseNow.AddMinutes(70))); // Monday 09:10 Manila, lead time 60 min

        Assert.Equal(FailureKind.SlotTaken, (await svc.CreateAsync(Request(shop, Manila(4, 9, 30)))).Failure);
        Assert.Equal(FailureKind.SlotTaken, (await svc.CreateAsync(Request(shop, Manila(4, 10)))).Failure);
        Assert.True((await svc.CreateAsync(Request(shop, Manila(4, 10, 30)))).IsSuccess);
    }

    [Fact]
    public async Task Horizon_is_enforced_on_the_server()
    {
        var shop = await CreateShopAsync();
        var svc = ServiceFor(shop, Clock());

        Assert.Equal(FailureKind.SlotTaken, (await svc.CreateAsync(Request(shop, Manila(4, 10).AddDays(31)))).Failure);
        Assert.True((await svc.CreateAsync(Request(shop, Manila(4, 10).AddDays(20)))).IsSuccess);
    }

    [Fact]
    public async Task Blackout_is_enforced_on_the_server()
    {
        var shop = await CreateShopAsync();
        await using (var db = new TestDbFactory(shop.ConnectionString).CreateDbContext())
        {
            db.Blackouts.Add(new Blackout { BusinessId = shop.BusinessId, StartsAtUtc = Manila(6, 9), EndsAtUtc = Manila(6, 12), Reason = "Holiday" });
            await db.SaveChangesAsync();
        }
        var svc = ServiceFor(shop, Clock());

        Assert.Equal(FailureKind.SlotTaken, (await svc.CreateAsync(Request(shop, Manila(6, 10)))).Failure);
        Assert.Equal(FailureKind.SlotTaken, (await svc.CreateAsync(Request(shop, Manila(6, 11, 30)))).Failure);
        Assert.True((await svc.CreateAsync(Request(shop, Manila(6, 12)))).IsSuccess);
    }

    [Fact]
    public async Task Working_hours_and_slot_grid_are_enforced_on_the_server()
    {
        var shop = await CreateShopAsync();
        var svc = ServiceFor(shop, Clock());

        Assert.Equal(FailureKind.SlotTaken, (await svc.CreateAsync(Request(shop, Manila(5, 8, 30)))).Failure);  // before opening
        Assert.Equal(FailureKind.SlotTaken, (await svc.CreateAsync(Request(shop, Manila(5, 18)))).Failure);     // at closing
        Assert.Equal(FailureKind.SlotTaken, (await svc.CreateAsync(Request(shop, Manila(5, 17, 45)))).Failure);  // runs past closing / off grid
        Assert.Equal(FailureKind.SlotTaken, (await svc.CreateAsync(Request(shop, Manila(5, 10, 15)))).Failure);  // off the 30-minute grid
        Assert.True((await svc.CreateAsync(Request(shop, Manila(5, 17, 30)))).IsSuccess);                         // last slot fits exactly
    }

    [Fact]
    public async Task Buffer_blocks_the_following_slot_but_not_the_one_after()
    {
        var shop = await CreateShopAsync(bufferMinutes: 15);
        var svc = ServiceFor(shop, Clock());

        Assert.True((await svc.CreateAsync(Request(shop, Manila(5, 10)))).IsSuccess);
        Assert.Equal(FailureKind.SlotTaken, (await svc.CreateAsync(Request(shop, Manila(5, 10, 30)))).Failure);
        Assert.True((await svc.CreateAsync(Request(shop, Manila(5, 11)))).IsSuccess);
    }

    [Fact]
    public async Task A_slot_listed_earlier_but_since_taken_is_refused_not_double_booked()
    {
        var shop = await CreateShopAsync();
        var svc = ServiceFor(shop, Clock());
        var date = new DateOnly(2030, 3, 5);

        var listed = await svc.GetFreeStartsAsync(shop.BusinessId, shop.ServiceId, null, date);
        Assert.Contains(Manila(5, 10), listed.Value!);

        Assert.True((await svc.CreateAsync(Request(shop, Manila(5, 10)))).IsSuccess); // someone else books it
        var stale = await svc.CreateAsync(Request(shop, Manila(5, 10)));               // this page still shows it as free
        Assert.Equal(FailureKind.SlotTaken, stale.Failure);

        var after = await svc.GetFreeStartsAsync(shop.BusinessId, shop.ServiceId, null, date);
        Assert.DoesNotContain(Manila(5, 10), after.Value!);
    }

    // ------------------------------------------------------------ walk-ins (staff-created)

    [Fact]
    public async Task Staff_walk_in_skips_lead_time_but_not_hours_or_overlaps()
    {
        var shop = await CreateShopAsync();
        var clock = Clock(new DateTimeOffset(Manila(5, 10, 10), TimeSpan.Zero)); // 10:10, slot 10:00 in progress
        var svc = ServiceFor(shop, clock);
        var staff = BookingActor.Staff(Guid.NewGuid(), "Owner");

        Assert.Equal(FailureKind.SlotTaken, (await svc.CreateAsync(Request(shop, Manila(5, 10, 30)))).Failure);        // customer: inside lead time
        Assert.True((await svc.CreateAsync(Request(shop, Manila(5, 10, 30), actor: staff))).IsSuccess);               // walk-in: allowed
        Assert.Equal(FailureKind.SlotTaken, (await svc.CreateAsync(Request(shop, Manila(5, 10, 30), actor: staff))).Failure); // overlap still refused
        Assert.Equal(FailureKind.SlotTaken, (await svc.CreateAsync(Request(shop, Manila(5, 20), actor: staff))).Failure);     // outside hours
    }

    // ------------------------------------------------------------ cancellation, completion, audit

    [Fact]
    public async Task Customer_cancel_after_cutoff_is_refused_staff_cancel_succeeds_and_both_are_audited()
    {
        var shop = await CreateShopAsync(cutoffHours: 2);
        var clock = Clock();
        var svc = ServiceFor(shop, clock);
        var booking = (await svc.CreateAsync(Request(shop, Manila(5, 10)))).Value!.Booking;

        clock.Now = new DateTimeOffset(Manila(5, 8, 30), TimeSpan.Zero); // 1.5 h before the start
        var refused = await svc.CancelByTokenAsync(booking.ManageToken);
        Assert.Equal(FailureKind.PolicyViolation, refused.Failure);
        Assert.Equal(BookingStatus.Confirmed, (await svc.LoadSummaryAsync(shop.BusinessId, booking.Id))!.Status);

        var staff = await svc.TransitionAsync(shop.BusinessId, booking.Id, BookingStatus.Cancelled,
            BookingActor.Staff(Guid.NewGuid(), "Owner"), reason: "Barber is sick");
        Assert.True(staff.IsSuccess);
        Assert.Equal(BookingStatus.Cancelled, staff.Value!.Status);

        await using var db = new TestDbFactory(shop.ConnectionString).CreateDbContext();
        var audit = await db.AuditLogs.Where(a => a.BookingId == booking.Id).OrderBy(a => a.Id).ToListAsync();
        Assert.Equal(["CREATED", "CANCELLED"], audit.Select(a => a.Action));
        Assert.Equal("STAFF", audit[1].ActorType);
        Assert.Equal("Barber is sick", audit[1].Reason);
        Assert.Equal("CONFIRMED", audit[1].FromStatus);

        // cancelling freed the slot
        Assert.True((await svc.CreateAsync(Request(shop, Manila(5, 10)))).IsSuccess);
    }

    [Fact]
    public async Task Customer_cancel_before_cutoff_succeeds_and_unknown_token_is_not_found()
    {
        var shop = await CreateShopAsync(cutoffHours: 2);
        var clock = Clock();
        var svc = ServiceFor(shop, clock);
        var booking = (await svc.CreateAsync(Request(shop, Manila(5, 10)))).Value!.Booking;

        clock.Now = new DateTimeOffset(Manila(5, 6), TimeSpan.Zero); // 4 h before
        var ok = await svc.CancelByTokenAsync(booking.ManageToken);
        Assert.True(ok.IsSuccess);
        Assert.Equal(BookingStatus.Cancelled, ok.Value!.Status);

        Assert.Equal(FailureKind.NotFound, (await svc.CancelByTokenAsync("no-such-token")).Failure);
        Assert.Equal(FailureKind.PolicyViolation, (await svc.CancelByTokenAsync(booking.ManageToken)).Failure); // already cancelled
    }

    [Fact]
    public async Task Complete_and_no_show_follow_the_policy_and_write_audit_rows()
    {
        var shop = await CreateShopAsync();
        var clock = Clock();
        var svc = ServiceFor(shop, clock);
        var staff = BookingActor.Staff(Guid.NewGuid(), "Owner");
        var a = (await svc.CreateAsync(Request(shop, Manila(5, 10)))).Value!.Booking;
        var b = (await svc.CreateAsync(Request(shop, Manila(5, 11)))).Value!.Booking;

        Assert.Equal(FailureKind.PolicyViolation, (await svc.TransitionAsync(shop.BusinessId, a.Id, BookingStatus.Completed, staff)).Failure); // not started

        clock.Now = new DateTimeOffset(Manila(5, 10, 10), TimeSpan.Zero);
        Assert.True((await svc.TransitionAsync(shop.BusinessId, a.Id, BookingStatus.Completed, staff)).IsSuccess);

        clock.Now = new DateTimeOffset(Manila(5, 11, 10), TimeSpan.Zero); // 10 min after start: inside the 15-minute grace
        Assert.Equal(FailureKind.PolicyViolation, (await svc.TransitionAsync(shop.BusinessId, b.Id, BookingStatus.NoShow, staff)).Failure);
        clock.Now = new DateTimeOffset(Manila(5, 11, 20), TimeSpan.Zero);
        Assert.True((await svc.TransitionAsync(shop.BusinessId, b.Id, BookingStatus.NoShow, staff)).IsSuccess);

        await using var db = new TestDbFactory(shop.ConnectionString).CreateDbContext();
        Assert.Equal(["CREATED", "COMPLETED"], await db.AuditLogs.Where(x => x.BookingId == a.Id).OrderBy(x => x.Id).Select(x => x.Action).ToListAsync());
        Assert.Equal(["CREATED", "NO_SHOW"], await db.AuditLogs.Where(x => x.BookingId == b.Id).OrderBy(x => x.Id).Select(x => x.Action).ToListAsync());

        // customers cannot complete or mark no-show
        var c = (await svc.CreateAsync(Request(shop, Manila(6, 10)))).Value!.Booking;
        clock.Now = new DateTimeOffset(Manila(6, 11), TimeSpan.Zero);
        Assert.Equal(FailureKind.PolicyViolation,
            (await svc.TransitionAsync(shop.BusinessId, c.Id, BookingStatus.Completed, BookingActor.Customer("x"))).Failure);
    }

    [Fact]
    public async Task A_stale_version_is_rejected_and_a_current_one_is_accepted()
    {
        var shop = await CreateShopAsync();
        var svc = ServiceFor(shop, Clock());
        var booking = (await svc.CreateAsync(Request(shop, Manila(5, 10)))).Value!.Booking;
        var staff = BookingActor.Staff(Guid.NewGuid(), "Owner");

        var stale = await svc.TransitionAsync(shop.BusinessId, booking.Id, BookingStatus.Cancelled, staff, reason: "test", expectedVersion: booking.Version + 1);
        Assert.Equal(FailureKind.Stale, stale.Failure);
        Assert.Equal(BookingStatus.Confirmed, (await svc.LoadSummaryAsync(shop.BusinessId, booking.Id))!.Status);

        Assert.True((await svc.TransitionAsync(shop.BusinessId, booking.Id, BookingStatus.Cancelled, staff, reason: "test", expectedVersion: booking.Version)).IsSuccess);
    }

    [Fact]
    public async Task Two_simultaneous_staff_cancellations_produce_one_success_and_no_exception()
    {
        var shop = await CreateShopAsync();
        var svc = ServiceFor(shop, Clock());
        var booking = (await svc.CreateAsync(Request(shop, Manila(5, 10)))).Value!.Booking;
        var staff = BookingActor.Staff(Guid.NewGuid(), "Owner");

        var results = await Task.WhenAll(Enumerable.Range(0, 2).Select(_ =>
            Task.Run(() => svc.TransitionAsync(shop.BusinessId, booking.Id, BookingStatus.Cancelled, staff, reason: "test", expectedVersion: booking.Version))));

        Assert.Equal(1, results.Count(r => r.IsSuccess));
        Assert.Equal(1, results.Count(r => r.Failure is FailureKind.Stale or FailureKind.PolicyViolation));
        await using var db = new TestDbFactory(shop.ConnectionString).CreateDbContext();
        Assert.Equal(2, await db.AuditLogs.CountAsync(a => a.BookingId == booking.Id)); // CREATED + exactly one CANCELLED
    }

    // ------------------------------------------------------------ tenant isolation

    [Fact]
    public async Task Another_business_cannot_see_or_change_a_booking_or_use_foreign_ids()
    {
        var a = await CreateShopAsync();
        var b = await CreateShopAsync();
        var clock = Clock();
        var svcA = ServiceFor(a, clock);
        var booking = (await svcA.CreateAsync(Request(a, Manila(5, 10)))).Value!.Booking;
        var svcB = ServiceFor(b, clock);
        var staff = BookingActor.Staff(Guid.NewGuid(), "Other owner");

        Assert.Equal(FailureKind.NotFound, (await svcB.TransitionAsync(b.BusinessId, booking.Id, BookingStatus.Cancelled, staff)).Failure);
        Assert.Null(await svcB.LoadSummaryAsync(b.BusinessId, booking.Id));
        Assert.Equal(BookingStatus.Confirmed, (await svcA.LoadSummaryAsync(a.BusinessId, booking.Id))!.Status);

        // B's business id with A's service or staff id
        var foreignService = new CreateBookingRequest(b.BusinessId, a.ServiceId, null, Manila(5, 14), "X", "09171234567", null, "k-1", staff);
        Assert.Equal(FailureKind.NotFound, (await svcB.CreateAsync(foreignService)).Failure);
        var foreignStaff = new CreateBookingRequest(b.BusinessId, b.ServiceId, a.StaffIds[0], Manila(5, 14), "X", "09171234567", null, "k-2", staff);
        Assert.Equal(FailureKind.NotFound, (await svcB.CreateAsync(foreignStaff)).Failure);

        Assert.Equal(FailureKind.NotFound, (await svcB.GetFreeStartsAsync(b.BusinessId, a.ServiceId, null, new DateOnly(2030, 3, 5))).Failure);
        Assert.Equal(0, await CountBookingsAsync(b));
    }

    [Fact]
    public async Task The_same_idempotency_key_in_two_businesses_does_not_collide()
    {
        var a = await CreateShopAsync();
        var b = await CreateShopAsync();
        var clock = Clock();

        var ra = await ServiceFor(a, clock).CreateAsync(Request(a, Manila(5, 10), key: "shared-key"));
        var rb = await ServiceFor(b, clock).CreateAsync(Request(b, Manila(5, 10), key: "shared-key"));

        Assert.True(ra.IsSuccess && rb.IsSuccess);
        Assert.NotEqual(ra.Value!.Booking.Id, rb.Value!.Booking.Id);
        Assert.False(rb.Value.AlreadyExisted);
    }

    // ------------------------------------------------------------ input

    [Fact]
    public async Task Invalid_input_returns_field_errors_and_creates_nothing()
    {
        var shop = await CreateShopAsync();
        var svc = ServiceFor(shop, Clock());
        var bad = Request(shop, Manila(5, 10)) with { CustomerName = "  ", Phone = "12ab", Email = "not-an-email" };

        var result = await svc.CreateAsync(bad);

        Assert.Equal(FailureKind.Validation, result.Failure);
        Assert.Contains("name", result.FieldErrors.Keys);
        Assert.Contains("phone", result.FieldErrors.Keys);
        Assert.Contains("email", result.FieldErrors.Keys);
        Assert.Equal(0, await CountBookingsAsync(shop));
    }

    [Fact]
    public async Task A_repeat_customer_is_matched_by_phone_and_name_and_email_are_updated()
    {
        var shop = await CreateShopAsync();
        var svc = ServiceFor(shop, Clock());

        Assert.True((await svc.CreateAsync(Request(shop, Manila(5, 10), phone: "+63 917 123 4567") with { CustomerName = "Juan" })).IsSuccess);
        Assert.True((await svc.CreateAsync(Request(shop, Manila(5, 11), phone: "+639171234567") with { CustomerName = "Juan D.", Email = "juan@example.com" })).IsSuccess);

        await using var db = new TestDbFactory(shop.ConnectionString).CreateDbContext();
        var customer = await db.Customers.SingleAsync(c => c.BusinessId == shop.BusinessId);
        Assert.Equal("Juan D.", customer.Name);
        Assert.Equal("juan@example.com", customer.Email);
        Assert.Equal("09171234567", customer.Phone); // one canonical form: +63 917… is 0917…
    }
}
