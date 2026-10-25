using VeskaFlow.Core.Domain;
using VeskaFlow.Web.Services;
using Microsoft.EntityFrameworkCore;
using static VeskaFlow.Web.Tests.Seed;

namespace VeskaFlow.Web.Tests;

/// <summary>The optional medical record on the day of a service, the customer's history, and the service a follow-up is for.</summary>
public class VisitRecordTests
{
    private static string Id(string prefix) => prefix + Guid.NewGuid().ToString("N")[..10];

    private sealed record Setup(TestDbFactory Db, TestClock Clock, BookingService Bookings, VisitRecordService Records, FollowUpService FollowUps, RegisteredOwner Owner, Guid ServiceId, Guid StaffId);

    private static async Task<Setup> SetupAsync()
    {
        var db = new TestDbFactory(await TestDb.ConnectionStringAsync());
        var clock = new TestClock(BaseNow);
        var owner = (await new OnboardingService(db, clock).RegisterAsync(
            new RegisterRequest("Clinic", Id("vr"), "Dr A", $"{Id("o")}@example.com", "correct-horse-1", Industry: "clinic", TrialDays: 30))).Value!;
        await using var ctx = db.CreateDbContext();
        var service = await ctx.Services.Where(s => s.BusinessId == owner.BusinessId).Select(s => s.Id).SingleAsync();
        var staff = await ctx.StaffMembers.Where(s => s.BusinessId == owner.BusinessId).Select(s => s.Id).SingleAsync();
        return new Setup(db, clock, new BookingService(db, clock), new VisitRecordService(db), new FollowUpService(db, clock), owner, service, staff);
    }

    private static async Task<BookingSummary> BookAsync(Setup s, int day, int hour, string phone, bool complete)
    {
        var booking = (await s.Bookings.CreateAsync(new CreateBookingRequest(s.Owner.BusinessId, s.ServiceId, s.StaffId, Manila(day, hour), "Maria Santos",
            phone, null, Id("k"), BookingActor.Customer("Maria")))).Value!.Booking;
        if (complete)
        {
            s.Clock.Now = new DateTimeOffset(Manila(day, hour, 10), TimeSpan.Zero);
            Assert.True((await s.Bookings.TransitionAsync(s.Owner.BusinessId, booking.Id, BookingStatus.Completed, BookingActor.Staff(s.Owner.AdminUserId, "Dr A"))).IsSuccess);
        }
        return booking;
    }

    [Fact]
    public async Task Notes_can_be_saved_cleared_and_only_on_a_completed_booking()
    {
        var s = await SetupAsync();
        var scheduled = await BookAsync(s, 6, 10, "09175550051", complete: false);
        Assert.Equal(FailureKind.PolicyViolation, (await s.Records.SetNotesAsync(s.Owner.BusinessId, scheduled.Id, "x")).Failure);

        var done = await BookAsync(s, 5, 10, "09175550052", complete: true);
        Assert.True((await s.Records.SetNotesAsync(s.Owner.BusinessId, done.Id, "  BP 120/80  ")).IsSuccess);
        await using (var ctx = s.Db.CreateDbContext())
            Assert.Equal("BP 120/80", (await ctx.Bookings.SingleAsync(b => b.Id == done.Id)).VisitNotes);

        Assert.Contains("visitNotes", (await s.Records.SetNotesAsync(s.Owner.BusinessId, done.Id, new string('x', 4001))).FieldErrors.Keys);
        Assert.True((await s.Records.SetNotesAsync(s.Owner.BusinessId, done.Id, "   ")).IsSuccess); // blank clears it
        await using (var ctx = s.Db.CreateDbContext())
            Assert.Null((await ctx.Bookings.SingleAsync(b => b.Id == done.Id)).VisitNotes);
    }

    [Fact]
    public async Task A_customers_history_lists_completed_visits_newest_first_and_never_crosses_businesses()
    {
        var s = await SetupAsync();
        var other = await SetupAsync();
        var first = await BookAsync(s, 5, 9, "09175550053", complete: true);
        await s.Records.SetNotesAsync(s.Owner.BusinessId, first.Id, "First visit: cleaning");
        var second = await BookAsync(s, 5, 11, "09175550053", complete: true);
        await s.Records.SetNotesAsync(s.Owner.BusinessId, second.Id, "Second visit: filling");
        await BookAsync(s, 6, 10, "09175550053", complete: false); // not completed: not in the history

        await using var ctx = s.Db.CreateDbContext();
        var customerId = (await ctx.Bookings.SingleAsync(b => b.Id == first.Id)).CustomerId;

        var history = (await s.Records.GetHistoryAsync(s.Owner.BusinessId, customerId))!;
        Assert.Equal("Maria Santos", history.Name);
        Assert.Equal(new[] { "Second visit: filling", "First visit: cleaning" }, history.Visits.Select(v => v.Notes ?? "").ToArray());

        Assert.Null(await other.Records.GetHistoryAsync(other.Owner.BusinessId, customerId));
        Assert.Equal(FailureKind.NotFound, (await other.Records.SetNotesAsync(other.Owner.BusinessId, first.Id, "hacked")).Failure);
    }

    [Fact]
    public async Task A_follow_up_can_name_the_service_it_is_for_and_only_one_of_this_business()
    {
        var s = await SetupAsync();
        var catalog = new CatalogAdminService(s.Db, s.Clock);
        var cleaning = (await catalog.SaveServiceAsync(s.Owner.BusinessId, new ServiceInput(null, "Dental cleaning", 45, 0, 0, true, 5))).Value;
        var foreign = await SetupAsync();
        var foreignService = foreign.ServiceId;
        var done = await BookAsync(s, 5, 10, "09175550054", complete: true);

        Assert.True((await s.FollowUps.SetAsync(s.Owner.BusinessId, done.Id, new DateOnly(2030, 4, 5), null, cleaning)).IsSuccess);
        Assert.Equal("Dental cleaning", (await s.FollowUps.ListAsync(s.Owner.BusinessId)).Single().FollowUpServiceName);

        Assert.Equal(FailureKind.NotFound, (await s.FollowUps.SetAsync(s.Owner.BusinessId, done.Id, new DateOnly(2030, 4, 5), null, foreignService)).Failure);

        // The visit's own service means "same service": nothing extra is stored.
        Assert.True((await s.FollowUps.SetAsync(s.Owner.BusinessId, done.Id, new DateOnly(2030, 4, 5), null, s.ServiceId)).IsSuccess);
        Assert.Null((await s.FollowUps.ListAsync(s.Owner.BusinessId)).Single().FollowUpServiceName);
    }
}

