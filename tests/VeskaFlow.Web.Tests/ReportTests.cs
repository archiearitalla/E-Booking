using VeskaFlow.Core.Domain;
using VeskaFlow.Web.Services;
using Microsoft.EntityFrameworkCore;
using static VeskaFlow.Web.Tests.Seed;

namespace VeskaFlow.Web.Tests;

/// <summary>Reports: counts by outcome, revenue from completed bookings, period ranges, isolation between shops.</summary>
[Collection("http")]
public class ReportTests
{
    private static string Id(string prefix) => prefix + Guid.NewGuid().ToString("N")[..10];

    [Fact]
    public void Periods_cover_the_expected_dates()
    {
        var today = new DateOnly(2030, 3, 15);
        Assert.Equal((new DateOnly(2030, 3, 1), today), ReportService.Range(ReportService.Period.ThisMonth, today));
        Assert.Equal((new DateOnly(2030, 2, 1), new DateOnly(2030, 2, 28)), ReportService.Range(ReportService.Period.LastMonth, today));
        Assert.Equal((new DateOnly(2030, 2, 14), today), ReportService.Range(ReportService.Period.Last30Days, today));
        Assert.Equal((new DateOnly(2030, 1, 1), today), ReportService.Range(ReportService.Period.ThisYear, today));
    }

    [Fact]
    public async Task The_report_counts_outcomes_and_adds_up_completed_revenue_for_this_shop_only()
    {
        var db = new TestDbFactory(await TestDb.ConnectionStringAsync());
        var clock = new TestClock(BaseNow);
        var bookings = new BookingService(db, clock);
        var owner = (await new OnboardingService(db, clock).RegisterAsync(
            new RegisterRequest("Cuts", Id("rp"), "Owner", $"{Id("o")}@example.com", "correct-horse-1", TrialDays: 30))).Value!;
        await using var ctx = db.CreateDbContext();
        var service = await ctx.Services.Where(s => s.BusinessId == owner.BusinessId && !s.IsOther).Select(s => s.Id).SingleAsync();
        var staff = await ctx.StaffMembers.Where(s => s.BusinessId == owner.BusinessId).Select(s => s.Id).SingleAsync();

        async Task Book(int day, int hour, BookingStatus status, decimal price)
        {
            var b = (await bookings.CreateAsync(new CreateBookingRequest(owner.BusinessId, service, staff, Manila(day, hour), "Maria", "0917" + Random.Shared.Next(1000000, 9999999), null,
                Id("k"), BookingActor.Customer("Maria")))).Value!.Booking;
            await using var c = db.CreateDbContext();
            var row = await c.Bookings.SingleAsync(x => x.Id == b.Id);
            row.Status = status;
            row.PriceSnapshot = price;
            await c.SaveChangesAsync();
        }

        await Book(5, 9, BookingStatus.Completed, 200m);
        await Book(5, 11, BookingStatus.Completed, 300m);
        await Book(6, 9, BookingStatus.NoShow, 200m);
        await Book(6, 11, BookingStatus.Cancelled, 200m);
        await Book(7, 9, BookingStatus.Confirmed, 200m);

        var reports = new ReportService(db, clock);
        clock.Now = new DateTimeOffset(Manila(20, 12), TimeSpan.Zero);
        var report = (await reports.BuildAsync(owner.BusinessId, ReportService.Period.ThisMonth))!;
        Assert.Equal(5, report.Total);
        Assert.Equal(2, report.Completed);
        Assert.Equal(1, report.NoShows);
        Assert.Equal(1, report.Cancelled);
        Assert.Equal(1, report.Upcoming);
        Assert.Equal(500m, report.Revenue);
        var line = Assert.Single(report.ByService);
        Assert.Equal(500m, line.Revenue);
        Assert.Equal(2, Assert.Single(report.ByStaff).Completed);

        // Last month is empty, and another shop sees nothing of this one.
        Assert.Equal(0, (await reports.BuildAsync(owner.BusinessId, ReportService.Period.LastMonth))!.Total);
        var other = (await new OnboardingService(db, clock).RegisterAsync(
            new RegisterRequest("Other", Id("rq"), "Owner", $"{Id("o")}@example.com", "correct-horse-1", TrialDays: 30))).Value!;
        Assert.Equal(0, (await reports.BuildAsync(other.BusinessId, ReportService.Period.ThisMonth))!.Total);
    }
}
