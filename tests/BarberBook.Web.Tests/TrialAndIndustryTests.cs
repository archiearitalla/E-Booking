using BarberBook.Web.Data;
using BarberBook.Web.Services;
using Microsoft.EntityFrameworkCore;
using static BarberBook.Web.Tests.Seed;

namespace BarberBook.Web.Tests;

/// <summary>Industry choice at sign-up and the trial window (BUSINESS_RULES section 18), against real PostgreSQL.</summary>
public class TrialAndIndustryTests
{
    private static string Unique(string prefix) => prefix + Guid.NewGuid().ToString("N")[..10];

    private static RegisterRequest Request(string? industry = null, int trialDays = 0) =>
        new("Smile Clinic", Unique("tri"), "Dr Reyes", $"{Unique("o")}@example.com", "correct-horse-1",
            Industry: industry, TrialDays: trialDays);

    [Fact]
    public async Task A_clinic_gets_its_starter_service_and_a_trial_end_date()
    {
        var db = new TestDbFactory(await TestDb.ConnectionStringAsync());
        var clock = new TestClock(BaseNow);

        var result = await new OnboardingService(db, clock).RegisterAsync(Request("clinic", trialDays: 30));

        Assert.True(result.IsSuccess);
        Assert.Equal(BaseNow.UtcDateTime.AddDays(30), result.Value!.TrialEndsAtUtc);
        await using var ctx = db.CreateDbContext();
        var business = await ctx.Businesses.SingleAsync(b => b.Id == result.Value.BusinessId);
        Assert.Equal("clinic", business.Industry);
        Assert.Equal(BaseNow.UtcDateTime.AddDays(30), business.TrialEndsAtUtc);
        Assert.Equal(Text.DefaultServiceClinic,
            await ctx.Services.Where(s => s.BusinessId == business.Id).Select(s => s.Name).SingleAsync());
    }

    [Fact]
    public async Task Without_an_industry_or_trial_days_the_shop_is_a_barbershop_with_no_trial_limit()
    {
        var db = new TestDbFactory(await TestDb.ConnectionStringAsync());

        var result = await new OnboardingService(db).RegisterAsync(Request());

        Assert.True(result.IsSuccess);
        Assert.Null(result.Value!.TrialEndsAtUtc);
        await using var ctx = db.CreateDbContext();
        var business = await ctx.Businesses.SingleAsync(b => b.Id == result.Value.BusinessId);
        Assert.Equal("barbershop", business.Industry);
        Assert.Null(business.TrialEndsAtUtc);
    }

    [Fact]
    public async Task An_unknown_industry_is_refused_and_nothing_is_created()
    {
        var db = new TestDbFactory(await TestDb.ConnectionStringAsync());
        var request = Request("spaceship");

        var result = await new OnboardingService(db).RegisterAsync(request);

        Assert.Equal(FailureKind.Validation, result.Failure);
        Assert.Contains("industry", result.FieldErrors.Keys);
        await using var ctx = db.CreateDbContext();
        Assert.False(await ctx.Businesses.AnyAsync(b => b.Slug == request.Slug));
    }

    [Fact]
    public async Task After_the_trial_customers_cannot_book_but_staff_still_can_and_the_page_reports_it()
    {
        var db = new TestDbFactory(await TestDb.ConnectionStringAsync());
        var clock = new TestClock(BaseNow);
        var owner = (await new OnboardingService(db, clock).RegisterAsync(Request("clinic", trialDays: 30))).Value!;
        var bookings = new BookingService(db, clock);
        var catalog = new ShopCatalogService(db, clock);

        await using var ctx = db.CreateDbContext();
        var serviceId = await ctx.Services.Where(s => s.BusinessId == owner.BusinessId).Select(s => s.Id).SingleAsync();
        var staffId = await ctx.StaffMembers.Where(s => s.BusinessId == owner.BusinessId).Select(s => s.Id).SingleAsync();
        CreateBookingRequest Req(string phone, BookingActor actor, int hour) => new(
            owner.BusinessId, serviceId, staffId, Manila(5, hour), "Maria", phone, null, Unique("k"), actor);

        // During the trial a customer can book and the page accepts bookings.
        Assert.True((await bookings.CreateAsync(Req("09170000001", BookingActor.Customer("Maria"), 10))).IsSuccess);
        Assert.True((await catalog.GetBySlugAsync(owner.Slug))!.AcceptingBookings);

        // The trial ends.
        await ctx.Businesses.Where(b => b.Id == owner.BusinessId)
            .ExecuteUpdateAsync(s => s.SetProperty(b => b.TrialEndsAtUtc, BaseNow.UtcDateTime.AddDays(-1)));

        var customer = await bookings.CreateAsync(Req("09170000002", BookingActor.Customer("Maria"), 11));
        Assert.Equal(FailureKind.PolicyViolation, customer.Failure);
        Assert.False((await catalog.GetBySlugAsync(owner.Slug))!.AcceptingBookings);

        var staff = await bookings.CreateAsync(Req("09170000003", BookingActor.Staff(owner.AdminUserId, "Owner"), 12));
        Assert.True(staff.IsSuccess, staff.Message);
    }

    [Fact]
    public async Task A_shop_with_no_trial_date_always_accepts_bookings()
    {
        var db = new TestDbFactory(await TestDb.ConnectionStringAsync());
        var clock = new TestClock(BaseNow);
        var owner = (await new OnboardingService(db, clock).RegisterAsync(Request())).Value!;

        Assert.True((await new ShopCatalogService(db, clock).GetBySlugAsync(owner.Slug))!.AcceptingBookings);
    }
}
