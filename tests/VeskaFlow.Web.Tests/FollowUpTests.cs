using VeskaFlow.Core.Domain;
using VeskaFlow.Web.Services;
using Microsoft.EntityFrameworkCore;
using static VeskaFlow.Web.Tests.Seed;

namespace VeskaFlow.Web.Tests;

/// <summary>Follow-up visits: set on a completed booking, listed until done, with a ready-made message.</summary>
public class FollowUpTests
{
    private static string Id(string prefix) => prefix + Guid.NewGuid().ToString("N")[..10];

    private sealed record Setup(TestDbFactory Db, TestClock Clock, BookingService Bookings, FollowUpService FollowUps, CatalogAdminService Catalog, RegisteredOwner Owner, Guid ServiceId, Guid StaffId);

    private static async Task<Setup> SetupAsync()
    {
        var db = new TestDbFactory(await TestDb.ConnectionStringAsync());
        var clock = new TestClock(BaseNow);
        var owner = (await new OnboardingService(db, clock).RegisterAsync(
            new RegisterRequest("Clinic", Id("fu"), "Dr A", $"{Id("o")}@example.com", "correct-horse-1", Industry: "clinic", TrialDays: 30))).Value!;
        await using var ctx = db.CreateDbContext();
        var service = await ctx.Services.Where(s => s.BusinessId == owner.BusinessId).Select(s => s.Id).SingleAsync();
        var staff = await ctx.StaffMembers.Where(s => s.BusinessId == owner.BusinessId).Select(s => s.Id).SingleAsync();
        return new Setup(db, clock, new BookingService(db, clock), new FollowUpService(db, clock), new CatalogAdminService(db, clock), owner, service, staff);
    }

    private static async Task<Guid> CompletedBookingAsync(Setup s, int hour, string phone)
    {
        var booking = (await s.Bookings.CreateAsync(new CreateBookingRequest(s.Owner.BusinessId, s.ServiceId, s.StaffId, Manila(5, hour), "Maria Santos",
            phone, null, Id("k"), BookingActor.Customer("Maria")))).Value!.Booking;
        s.Clock.Now = new DateTimeOffset(Manila(5, hour, 10), TimeSpan.Zero);
        var done = await s.Bookings.TransitionAsync(s.Owner.BusinessId, booking.Id, BookingStatus.Completed, BookingActor.Staff(s.Owner.AdminUserId, "Dr A"));
        Assert.True(done.IsSuccess, done.Message);
        return booking.Id;
    }

    [Fact]
    public async Task A_follow_up_can_be_set_on_a_completed_booking_listed_and_marked_done()
    {
        var s = await SetupAsync();
        var id = await CompletedBookingAsync(s, 10, "09175550041");
        var visit = new DateOnly(2030, 3, 5);

        var set = await s.FollowUps.SetAsync(s.Owner.BusinessId, id, visit.AddDays(14), "Check the wound");
        Assert.True(set.IsSuccess, set.Message);

        var row = (await s.FollowUps.ListAsync(s.Owner.BusinessId)).Single();
        Assert.Equal(("Maria Santos", visit, visit.AddDays(14), "Check the wound"), (row.CustomerName, row.VisitDate, row.FollowUpOn, row.Note));
        Assert.False(row.Overdue);

        s.Clock.Now = new DateTimeOffset(Manila(25, 9), TimeSpan.Zero); // well after the follow-up date
        Assert.True((await s.FollowUps.ListAsync(s.Owner.BusinessId)).Single().Overdue);

        Assert.True((await s.FollowUps.MarkDoneAsync(s.Owner.BusinessId, id)).IsSuccess);
        Assert.Empty(await s.FollowUps.ListAsync(s.Owner.BusinessId));
    }

    [Fact]
    public async Task Follow_up_dates_are_validated_only_completed_bookings_qualify_and_it_can_be_cleared()
    {
        var s = await SetupAsync();
        var scheduled = (await s.Bookings.CreateAsync(new CreateBookingRequest(s.Owner.BusinessId, s.ServiceId, s.StaffId, Manila(6, 10), "Pedro",
            "09175550042", null, Id("k"), BookingActor.Customer("Pedro")))).Value!.Booking;
        Assert.Equal(FailureKind.PolicyViolation, (await s.FollowUps.SetAsync(s.Owner.BusinessId, scheduled.Id, new DateOnly(2030, 4, 1), null)).Failure);

        var id = await CompletedBookingAsync(s, 10, "09175550043");
        Assert.Contains("followUpOn", (await s.FollowUps.SetAsync(s.Owner.BusinessId, id, new DateOnly(2030, 3, 1), null)).FieldErrors.Keys);  // before the visit
        Assert.Contains("followUpOn", (await s.FollowUps.SetAsync(s.Owner.BusinessId, id, new DateOnly(2033, 1, 1), null)).FieldErrors.Keys);  // more than 2 years
        Assert.Contains("followUpNote", (await s.FollowUps.SetAsync(s.Owner.BusinessId, id, new DateOnly(2030, 4, 1), new string('x', 201))).FieldErrors.Keys);

        Assert.True((await s.FollowUps.SetAsync(s.Owner.BusinessId, id, new DateOnly(2030, 4, 1), null)).IsSuccess);
        Assert.Single(await s.FollowUps.ListAsync(s.Owner.BusinessId));
        Assert.True((await s.FollowUps.SetAsync(s.Owner.BusinessId, id, null, null)).IsSuccess); // clear
        Assert.Empty(await s.FollowUps.ListAsync(s.Owner.BusinessId));
    }

    [Fact]
    public async Task Another_business_cannot_set_list_or_finish_a_follow_up()
    {
        var a = await SetupAsync();
        var b = await SetupAsync();
        var id = await CompletedBookingAsync(a, 10, "09175550044");

        Assert.Equal(FailureKind.NotFound, (await b.FollowUps.SetAsync(b.Owner.BusinessId, id, new DateOnly(2030, 4, 1), null)).Failure);
        await a.FollowUps.SetAsync(a.Owner.BusinessId, id, new DateOnly(2030, 4, 1), null);
        Assert.Empty(await b.FollowUps.ListAsync(b.Owner.BusinessId));
        Assert.Equal(FailureKind.NotFound, (await b.FollowUps.MarkDoneAsync(b.Owner.BusinessId, id)).Failure);
        Assert.Single(await a.FollowUps.ListAsync(a.Owner.BusinessId));
    }

    [Fact]
    public async Task A_service_can_suggest_a_follow_up_in_days_and_the_day_view_offers_that_date()
    {
        var s = await SetupAsync();
        var bad = await s.Catalog.SaveServiceAsync(s.Owner.BusinessId, new ServiceInput(s.ServiceId, "Consultation", 30, 0, 0, true, 0, 0));
        Assert.Contains("followUpDays", bad.FieldErrors.Keys);
        Assert.True((await s.Catalog.SaveServiceAsync(s.Owner.BusinessId, new ServiceInput(s.ServiceId, "Consultation", 30, 0, 0, true, 0, 14))).IsSuccess);
        var id = await CompletedBookingAsync(s, 10, "09175550045");

        var days = new AdminDayService(s.Db, s.Clock);
        var booking = (await days.GetDayAsync(s.Owner.BusinessId, new DateOnly(2030, 3, 5)))!.Bookings.Single(b => b.Id == id);
        Assert.Equal(new DateOnly(2030, 3, 19), booking.SuggestedFollowUp);
        Assert.Null(booking.FollowUpOn);
    }
}
