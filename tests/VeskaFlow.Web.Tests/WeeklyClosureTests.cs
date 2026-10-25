using VeskaFlow.Core.Domain;
using VeskaFlow.Web.Data;
using VeskaFlow.Web.Services;
using Microsoft.EntityFrameworkCore;
using static VeskaFlow.Web.Tests.Seed;

namespace VeskaFlow.Web.Tests;

/// <summary>Closed days that repeat every week, and can be switched off and on (BUSINESS_RULES section 6).</summary>
public class WeeklyClosureTests
{
    private static string Id(string prefix) => prefix + Guid.NewGuid().ToString("N")[..10];
    private static readonly DateOnly Tuesday = new(2030, 3, 5);   // Seed.Manila(5, ...) is this Tuesday
    private static readonly DateOnly Wednesday = new(2030, 3, 6);

    private sealed record Setup(TestDbFactory Db, BookingService Bookings, ShopSettingsService Settings, RegisteredOwner Owner, Guid ServiceId, Guid StaffA, Guid StaffB);

    private static async Task<Setup> SetupAsync()
    {
        var db = new TestDbFactory(await TestDb.ConnectionStringAsync());
        var clock = new TestClock(BaseNow);
        var owner = (await new OnboardingService(db, clock).RegisterAsync(
            new RegisterRequest("Clinic", Id("wc"), "Dr A", $"{Id("o")}@example.com", "correct-horse-1", Industry: "clinic", TrialDays: 30))).Value!;
        await using var ctx = db.CreateDbContext();
        var service = await ctx.Services.Where(s => s.BusinessId == owner.BusinessId && !s.IsOther).Select(s => s.Id).SingleAsync();
        var a = await ctx.StaffMembers.Where(s => s.BusinessId == owner.BusinessId).Select(s => s.Id).SingleAsync();
        var b = (await new CatalogAdminService(db, clock).SaveStaffAsync(owner.BusinessId, new StaffInput(null, "Dr B", true, 1, [service]))).Value!.Id;
        return new Setup(db, new BookingService(db, clock), new ShopSettingsService(db, clock), owner, service, a, b);
    }

    private static async Task<int> FreeAsync(Setup s, Guid? staff, DateOnly day) =>
        (await s.Bookings.GetFreeStartsAsync(s.Owner.BusinessId, s.ServiceId, staff, day)).Value!.Count;

    [Fact]
    public async Task A_weekly_closure_closes_that_weekday_for_everyone_and_switching_it_off_opens_it_again()
    {
        var s = await SetupAsync();
        Assert.True(await FreeAsync(s, null, Tuesday) > 0);

        var added = await s.Settings.AddWeeklyClosureAsync(s.Owner.BusinessId, new WeeklyClosureInput(null, (int)DayOfWeek.Tuesday, "Staff meeting"));
        Assert.True(added.IsSuccess, added.Message);
        Assert.Equal(0, await FreeAsync(s, null, Tuesday));
        Assert.Equal(0, await FreeAsync(s, s.StaffA, Tuesday));
        Assert.True(await FreeAsync(s, null, Wednesday) > 0);  // other days are untouched

        await s.Settings.SetWeeklyClosureActiveAsync(s.Owner.BusinessId, added.Value!.Id, false);
        Assert.True(await FreeAsync(s, null, Tuesday) > 0);    // off: the rule stays but closes nothing

        await s.Settings.SetWeeklyClosureActiveAsync(s.Owner.BusinessId, added.Value.Id, true);
        Assert.Equal(0, await FreeAsync(s, null, Tuesday));
    }

    [Fact]
    public async Task A_weekly_closure_for_one_person_leaves_the_others_bookable_and_any_available_picks_them()
    {
        var s = await SetupAsync();
        Assert.True((await s.Settings.AddWeeklyClosureAsync(s.Owner.BusinessId, new WeeklyClosureInput(s.StaffA, (int)DayOfWeek.Tuesday, null))).IsSuccess);

        Assert.Equal(0, await FreeAsync(s, s.StaffA, Tuesday));
        Assert.True(await FreeAsync(s, s.StaffB, Tuesday) > 0);
        Assert.True(await FreeAsync(s, null, Tuesday) > 0);

        var booked = await s.Bookings.CreateAsync(new CreateBookingRequest(s.Owner.BusinessId, s.ServiceId, null, Manila(5, 10), "Maria",
            "09175550001", null, Id("k"), BookingActor.Customer("Maria")));
        Assert.True(booked.IsSuccess, booked.Message);
        Assert.Equal("Dr B", booked.Value!.Booking.StaffName);

        var direct = await s.Bookings.CreateAsync(new CreateBookingRequest(s.Owner.BusinessId, s.ServiceId, s.StaffA, Manila(5, 11), "Pedro",
            "09175550002", null, Id("k"), BookingActor.Customer("Pedro")));
        Assert.Equal(FailureKind.SlotTaken, direct.Failure);
    }

    [Fact]
    public async Task A_weekly_closure_also_stops_walk_ins_that_day()
    {
        var s = await SetupAsync();
        Assert.True((await s.Settings.AddWeeklyClosureAsync(s.Owner.BusinessId, new WeeklyClosureInput(null, (int)DayOfWeek.Tuesday, null))).IsSuccess);

        var walkIn = await s.Bookings.CreateAsync(new CreateBookingRequest(s.Owner.BusinessId, s.ServiceId, s.StaffA, Manila(5, 10), "Walk-in",
            "", null, Id("k"), BookingActor.Staff(s.Owner.AdminUserId, "Dr A")));

        Assert.Equal(FailureKind.SlotTaken, walkIn.Failure);
    }

    [Fact]
    public async Task The_same_weekday_cannot_be_added_twice_and_bad_input_is_refused()
    {
        var s = await SetupAsync();
        Assert.True((await s.Settings.AddWeeklyClosureAsync(s.Owner.BusinessId, new WeeklyClosureInput(null, 0, null))).IsSuccess);

        var dup = await s.Settings.AddWeeklyClosureAsync(s.Owner.BusinessId, new WeeklyClosureInput(null, 0, "again"));
        Assert.Equal(FailureKind.Validation, dup.Failure);
        Assert.Contains("weekday", dup.FieldErrors.Keys);

        // The same weekday for one person is a different rule and is fine.
        Assert.True((await s.Settings.AddWeeklyClosureAsync(s.Owner.BusinessId, new WeeklyClosureInput(s.StaffA, 0, null))).IsSuccess);

        Assert.Contains("weekday", (await s.Settings.AddWeeklyClosureAsync(s.Owner.BusinessId, new WeeklyClosureInput(null, 7, null))).FieldErrors.Keys);
        Assert.Contains("reason", (await s.Settings.AddWeeklyClosureAsync(s.Owner.BusinessId, new WeeklyClosureInput(null, 3, new string('x', 121)))).FieldErrors.Keys);
    }

    [Fact]
    public async Task Existing_bookings_on_the_closed_day_are_counted_and_never_cancelled()
    {
        var s = await SetupAsync();
        var booking = await s.Bookings.CreateAsync(new CreateBookingRequest(s.Owner.BusinessId, s.ServiceId, s.StaffA, Manila(5, 10), "Maria",
            "09175550003", null, Id("k"), BookingActor.Customer("Maria")));
        Assert.True(booking.IsSuccess, booking.Message);

        var added = await s.Settings.AddWeeklyClosureAsync(s.Owner.BusinessId, new WeeklyClosureInput(null, (int)DayOfWeek.Tuesday, null));

        Assert.Equal(1, added.Value!.AffectedBookings);
        var row = (await s.Settings.ListWeeklyClosuresAsync(s.Owner.BusinessId)).Single();
        Assert.Equal(1, row.AffectedBookings);
        await using var ctx = s.Db.CreateDbContext();
        Assert.Equal(BookingStatus.Confirmed, (await ctx.Bookings.SingleAsync(b => b.Id == booking.Value!.Booking.Id)).Status);

        await s.Settings.SetWeeklyClosureActiveAsync(s.Owner.BusinessId, added.Value.Id, false);
        Assert.Equal(0, (await s.Settings.ListWeeklyClosuresAsync(s.Owner.BusinessId)).Single().AffectedBookings);
    }

    private static Task<ServiceResult<CreateOutcome>> BookAsync(Setup s, DateTime start, string phone) =>
        s.Bookings.CreateAsync(new CreateBookingRequest(s.Owner.BusinessId, s.ServiceId, s.StaffA, start, "Maria",
            phone, null, Id("k"), BookingActor.Customer("Maria")));

    [Fact]
    public async Task A_daily_time_window_blocks_only_those_hours_every_day_until_switched_off()
    {
        var s = await SetupAsync();
        var lunch = await s.Settings.AddWeeklyClosureAsync(s.Owner.BusinessId,
            new WeeklyClosureInput(null, null, "Lunch", new TimeOnly(12, 0), new TimeOnly(13, 0)));
        Assert.True(lunch.IsSuccess, lunch.Message);

        Assert.Equal(FailureKind.SlotTaken, (await BookAsync(s, Manila(5, 12), "09175550011")).Failure);
        Assert.Equal(FailureKind.SlotTaken, (await BookAsync(s, Manila(6, 12), "09175550012")).Failure); // next day too
        Assert.True((await BookAsync(s, Manila(5, 13), "09175550013")).IsSuccess);                         // after lunch
        Assert.True((await BookAsync(s, Manila(5, 11), "09175550014")).IsSuccess);                         // before lunch

        await s.Settings.SetWeeklyClosureActiveAsync(s.Owner.BusinessId, lunch.Value!.Id, false);
        Assert.True((await BookAsync(s, Manila(6, 12), "09175550015")).IsSuccess);
    }

    [Fact]
    public async Task A_closure_time_outside_the_opening_hours_is_refused()
    {
        var s = await SetupAsync(); // starter hours are Monday to Saturday 9 AM to 6 PM
        var night = await s.Settings.AddWeeklyClosureAsync(s.Owner.BusinessId,
            new WeeklyClosureInput(null, null, null, new TimeOnly(1, 0), new TimeOnly(7, 0)));
        Assert.Contains("startTime", night.FieldErrors.Keys);

        // Sunday has no opening hours at all, so a window on Sunday closes nothing either.
        Assert.Contains("startTime", (await s.Settings.AddWeeklyClosureAsync(s.Owner.BusinessId,
            new WeeklyClosureInput(null, (int)DayOfWeek.Sunday, null, new TimeOnly(10, 0), new TimeOnly(12, 0)))).FieldErrors.Keys);
        Assert.Contains("startTime", (await s.Settings.AddClosureAsync(s.Owner.BusinessId,
            new ClosureInput(null, Tuesday, Tuesday, null, new TimeOnly(1, 0), new TimeOnly(7, 0)))).FieldErrors.Keys);

        // A window that overlaps the opening hours, even partly, is fine.
        Assert.True((await s.Settings.AddWeeklyClosureAsync(s.Owner.BusinessId,
            new WeeklyClosureInput(null, null, null, new TimeOnly(7, 0), new TimeOnly(10, 0)))).IsSuccess);
    }

    [Fact]
    public async Task A_one_off_closure_can_be_a_time_window_and_times_are_validated()
    {
        var s = await SetupAsync();
        var added = await s.Settings.AddClosureAsync(s.Owner.BusinessId,
            new ClosureInput(null, Tuesday, Tuesday, "Training", new TimeOnly(14, 0), new TimeOnly(16, 0)));
        Assert.True(added.IsSuccess, added.Message);

        Assert.Equal(FailureKind.SlotTaken, (await BookAsync(s, Manila(5, 15), "09175550021")).Failure);
        Assert.True((await BookAsync(s, Manila(5, 10), "09175550022")).IsSuccess);
        var row = (await s.Settings.ListClosuresAsync(s.Owner.BusinessId)).Single();
        Assert.Equal(new TimeOnly(14, 0), row.StartTime);
        Assert.Equal(new TimeOnly(16, 0), row.EndTime);

        Assert.Contains("startTime", (await s.Settings.AddClosureAsync(s.Owner.BusinessId, new ClosureInput(null, Tuesday, Tuesday, null, new TimeOnly(9, 0), null))).FieldErrors.Keys);
        Assert.Contains("endTime", (await s.Settings.AddWeeklyClosureAsync(s.Owner.BusinessId, new WeeklyClosureInput(null, null, null, new TimeOnly(13, 0), new TimeOnly(12, 0)))).FieldErrors.Keys);
    }

    [Fact]
    public async Task Another_business_cannot_see_switch_or_delete_a_weekly_closure()
    {
        var a = await SetupAsync();
        var b = await SetupAsync();
        var rule = (await a.Settings.AddWeeklyClosureAsync(a.Owner.BusinessId, new WeeklyClosureInput(null, 2, null))).Value!;

        Assert.Empty(await b.Settings.ListWeeklyClosuresAsync(b.Owner.BusinessId));
        Assert.Equal(FailureKind.NotFound, (await b.Settings.SetWeeklyClosureActiveAsync(b.Owner.BusinessId, rule.Id, false)).Failure);
        Assert.Equal(FailureKind.NotFound, (await b.Settings.DeleteWeeklyClosureAsync(b.Owner.BusinessId, rule.Id)).Failure);
        Assert.Equal(FailureKind.NotFound, (await b.Settings.AddWeeklyClosureAsync(b.Owner.BusinessId, new WeeklyClosureInput(a.StaffA, 4, null))).Failure);

        Assert.True((await a.Settings.ListWeeklyClosuresAsync(a.Owner.BusinessId)).Single().IsActive); // still there and on
        Assert.True((await a.Settings.DeleteWeeklyClosureAsync(a.Owner.BusinessId, rule.Id)).IsSuccess);
    }
}
