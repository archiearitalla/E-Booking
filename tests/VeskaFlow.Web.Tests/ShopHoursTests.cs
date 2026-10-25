using VeskaFlow.Web.Services;
using Microsoft.EntityFrameworkCore;
using static VeskaFlow.Web.Tests.Seed;

namespace VeskaFlow.Web.Tests;

/// <summary>Opening hours set once in Settings are copied to every staff member and to new staff.</summary>
public class ShopHoursTests
{
    private static string Id(string prefix) => prefix + Guid.NewGuid().ToString("N")[..10];

    private static async Task<(TestDbFactory Db, CatalogAdminService Catalog, RegisteredOwner Owner)> SetupAsync()
    {
        var db = new TestDbFactory(await TestDb.ConnectionStringAsync());
        var clock = new TestClock(BaseNow);
        var owner = (await new OnboardingService(db, clock).RegisterAsync(
            new RegisterRequest("Clinic", Id("sh"), "Dr A", $"{Id("o")}@example.com", "correct-horse-1", Industry: "clinic", TrialDays: 30))).Value!;
        return (db, new CatalogAdminService(db, clock), owner);
    }

    private static readonly HourWindow[] WithLunch =
    [
        new(1, new TimeOnly(8, 0), new TimeOnly(12, 0)), new(1, new TimeOnly(13, 0), new TimeOnly(17, 0)),
        new(2, new TimeOnly(8, 0), new TimeOnly(17, 0)),
    ];

    [Fact]
    public async Task Saving_shop_hours_replaces_the_hours_of_every_staff_member_and_is_shown_again()
    {
        var (db, catalog, owner) = await SetupAsync();
        var second = (await catalog.SaveStaffAsync(owner.BusinessId, new StaffInput(null, "Dr B", true, 1, []))).Value!.Id;

        var saved = await catalog.SaveShopHoursAsync(owner.BusinessId, WithLunch);
        Assert.True(saved.IsSuccess, saved.Message);

        await using var ctx = db.CreateDbContext();
        foreach (var staffId in await ctx.StaffMembers.Where(s => s.BusinessId == owner.BusinessId).Select(s => s.Id).ToListAsync())
            Assert.Equal(3, await ctx.WorkingHours.CountAsync(h => h.StaffId == staffId));
        Assert.Equal(3, (await catalog.GetShopHoursAsync(owner.BusinessId)).Count);

        // Individual hours can still differ afterwards, and the shop hours stay as they were.
        await catalog.SaveHoursAsync(owner.BusinessId, second, [new HourWindow(3, new TimeOnly(10, 0), new TimeOnly(14, 0))]);
        Assert.Equal(3, (await catalog.GetShopHoursAsync(owner.BusinessId)).Count);
    }

    [Fact]
    public async Task A_new_staff_member_starts_with_the_shop_hours()
    {
        var (db, catalog, owner) = await SetupAsync();
        await catalog.SaveShopHoursAsync(owner.BusinessId, WithLunch);

        var added = (await catalog.SaveStaffAsync(owner.BusinessId, new StaffInput(null, "Dr C", true, 2, []))).Value!.Id;

        await using var ctx = db.CreateDbContext();
        Assert.Equal(3, await ctx.WorkingHours.CountAsync(h => h.StaffId == added));
        Assert.Equal(0, await ctx.WorkingHours.CountAsync(h => h.StaffId == added && h.DayOfWeek == 6)); // Saturday is not in the shop hours
    }

    [Fact]
    public async Task Bad_shop_hours_are_refused_and_another_shop_is_never_touched()
    {
        var (db, catalog, owner) = await SetupAsync();
        var (_, _, other) = await SetupAsync();

        var overlap = await catalog.SaveShopHoursAsync(owner.BusinessId,
            [new HourWindow(1, new TimeOnly(8, 0), new TimeOnly(12, 0)), new HourWindow(1, new TimeOnly(11, 0), new TimeOnly(17, 0))]);
        Assert.Equal(FailureKind.Validation, overlap.Failure);
        Assert.Equal(6, (await catalog.GetShopHoursAsync(owner.BusinessId)).Count); // unchanged starter hours

        await catalog.SaveShopHoursAsync(owner.BusinessId, WithLunch);
        Assert.Equal(6, (await catalog.GetShopHoursAsync(other.BusinessId)).Count);
        await using var ctx = db.CreateDbContext();
        var otherStaff = await ctx.StaffMembers.Where(s => s.BusinessId == other.BusinessId).Select(s => s.Id).SingleAsync();
        Assert.Equal(6, await ctx.WorkingHours.CountAsync(h => h.StaffId == otherStaff));
    }
}
