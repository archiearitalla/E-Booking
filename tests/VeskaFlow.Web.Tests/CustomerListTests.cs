using VeskaFlow.Core.Domain;
using VeskaFlow.Web.Services;
using Microsoft.EntityFrameworkCore;
using static VeskaFlow.Web.Tests.Seed;

namespace VeskaFlow.Web.Tests;

/// <summary>The customer list: visits and spend, search, CSV, and isolation between shops.</summary>
[Collection("http")]
public class CustomerListTests
{
    private static string Id(string prefix) => prefix + Guid.NewGuid().ToString("N")[..10];

    private static async Task<(TestDbFactory Db, BookingService Bookings, RegisteredOwner Owner, Guid ServiceId, Guid StaffId)> ShopAsync()
    {
        var db = new TestDbFactory(await TestDb.ConnectionStringAsync());
        var clock = new TestClock(BaseNow);
        var owner = (await new OnboardingService(db, clock).RegisterAsync(
            new RegisterRequest("Cuts", Id("cl"), "Owner", $"{Id("o")}@example.com", "correct-horse-1", TrialDays: 30))).Value!;
        await using var ctx = db.CreateDbContext();
        var service = await ctx.Services.Where(s => s.BusinessId == owner.BusinessId && !s.IsOther).Select(s => s.Id).SingleAsync();
        var staff = await ctx.StaffMembers.Where(s => s.BusinessId == owner.BusinessId).Select(s => s.Id).SingleAsync();
        return (db, new BookingService(db, clock), owner, service, staff);
    }

    private static async Task<Guid> BookAsync(TestDbFactory db, BookingService bookings, RegisteredOwner owner, Guid service, Guid staff,
        int day, int hour, string name, string phone, BookingStatus final)
    {
        var booking = (await bookings.CreateAsync(new CreateBookingRequest(owner.BusinessId, service, staff, Manila(day, hour), name, phone, null,
            Id("k"), BookingActor.Customer(name)))).Value!.Booking;
        await using var ctx = db.CreateDbContext();
        var row = await ctx.Bookings.SingleAsync(b => b.Id == booking.Id);
        row.Status = final;
        row.PriceSnapshot = 150m;
        await ctx.SaveChangesAsync();
        return row.CustomerId;
    }

    [Fact]
    public async Task Customers_show_visits_no_shows_and_spend_and_can_be_searched()
    {
        var (db, bookings, owner, service, staff) = await ShopAsync();
        var maria = await BookAsync(db, bookings, owner, service, staff, 5, 9, "Maria Santos", "09175550401", BookingStatus.Completed);
        await BookAsync(db, bookings, owner, service, staff, 6, 9, "Maria Santos", "09175550401", BookingStatus.Completed);
        await BookAsync(db, bookings, owner, service, staff, 7, 9, "Maria Santos", "09175550401", BookingStatus.NoShow);
        await BookAsync(db, bookings, owner, service, staff, 5, 11, "Jose Reyes", "09175550402", BookingStatus.Confirmed);

        var list = new CustomerListService(db);
        var all = await list.ListAsync(owner.BusinessId);
        Assert.Equal(2, all.Count);
        var row = all.Single(r => r.Id == maria);
        Assert.Equal(2, row.Visits);
        Assert.Equal(1, row.NoShows);
        Assert.Equal(300m, row.Spent);
        Assert.Equal("Maria Santos", all[0].Name); // the one who has visited comes first

        Assert.Single(await list.ListAsync(owner.BusinessId, "jose"));
        Assert.Single(await list.ListAsync(owner.BusinessId, "0402"));
        Assert.Empty(await list.ListAsync(owner.BusinessId, "nobody"));

        var other = await ShopAsync();
        Assert.Empty(await list.ListAsync(other.Owner.BusinessId));
    }

    [Fact]
    public void Csv_has_a_header_quotes_commas_and_defuses_formulas()
    {
        var rows = new[]
        {
            new CustomerRow(Guid.NewGuid(), "=SUM(A1)", "09175550403", "a,b@example.com", 2, 0, 350m, new DateTime(2030, 3, 5, 1, 0, 0, DateTimeKind.Utc), DateTime.UtcNow),
        };
        var csv = CustomerListService.ToCsv(rows, "Asia/Manila");
        var lines = csv.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        Assert.StartsWith("Name,Phone,Email,", lines[0]);
        Assert.StartsWith("'=SUM(A1),09175550403,\"a,b@example.com\",2,0,350.00,", lines[1]);
    }
}
