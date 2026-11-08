using Microsoft.EntityFrameworkCore;
using VeskaFlow.Web.Services;

namespace VeskaFlow.Web.Tests;

/// <summary>Five staff are included in the price; more are paid for and set by the platform owner (BUSINESS_RULES 7.2).</summary>
[Collection("http")]
public class StaffLimitTests
{
    private static string Id(string prefix) => prefix + Guid.NewGuid().ToString("N")[..10];

    [Fact]
    public void The_limits_are_five_included_plus_the_extra_for_a_trial_or_a_paid_plan_and_one_for_neither()
    {
        Assert.Equal(5, PlanLimits.MaxStaff("FREE", inTrial: true));
        Assert.Equal(5, PlanLimits.MaxStaff("PRO"));
        Assert.Equal(8, PlanLimits.MaxStaff("PRO", extraStaff: 3));
        Assert.Equal(8, PlanLimits.MaxStaff("FREE", inTrial: true, extraStaff: 3));
        Assert.Equal(1, PlanLimits.MaxStaff("FREE"));
        Assert.Equal(1, PlanLimits.MaxStaff("FREE", extraStaff: 10)); // extra staff only count for a client
        Assert.Equal(105, PlanLimits.MaxStaff("PRO", extraStaff: 5000)); // never past the most the platform owner can give
        Assert.Equal(5, PlanLimits.MaxStaff("PRO", extraStaff: -4));
        Assert.Equal(20, PlanLimits.MaxStaffRecords());
        Assert.Equal(23, PlanLimits.MaxStaffRecords(3));
    }

    private sealed record Ctx(CatalogAdminService Catalog, AccountControlService Accounts, Guid BusinessId, Guid ServiceId);

    private static async Task<Ctx> SetupAsync(bool trial = true)
    {
        var db = new TestDbFactory(await TestDb.ConnectionStringAsync());
        var clock = new TestClock(Seed.BaseNow);
        var owner = (await new OnboardingService(db, clock).RegisterAsync(
            new RegisterRequest("Staff Limits", Id("sl"), "Owner", $"{Id("o")}@example.com", "correct-horse-1", TrialDays: trial ? 30 : 0))).Value!;
        await using var ctx = db.CreateDbContext();
        var service = await ctx.Services.Where(s => s.BusinessId == owner.BusinessId && !s.IsOther).Select(s => s.Id).SingleAsync();
        return new Ctx(new CatalogAdminService(db, clock), new AccountControlService(db, clock, new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build(), new CapturingEmailSender()),
            owner.BusinessId, service);
    }

    private static Task<ServiceResult<SaveOutcome>> AddAsync(Ctx c, string name, bool active = true) =>
        c.Catalog.SaveStaffAsync(c.BusinessId, new StaffInput(null, name, active, 1, [c.ServiceId]));

    [Fact]
    public async Task A_shop_can_have_five_active_staff_and_the_sixth_is_refused_until_extra_staff_are_paid_for()
    {
        var c = await SetupAsync(); // starts with one staff member
        for (var i = 2; i <= 5; i++) Assert.True((await AddAsync(c, $"Staff {i}")).IsSuccess, $"staff {i}");
        var sixth = await AddAsync(c, "Staff 6");
        Assert.Contains("isActive", sixth.FieldErrors.Keys);
        Assert.Equal(string.Format(Text.StaffLimit, 5), sixth.FieldErrors["isActive"]);

        // The platform owner records two extra staff after the payment: room for two more, not three.
        Assert.True((await c.Accounts.SetExtraStaffAsync(c.BusinessId, 2)).IsSuccess);
        Assert.True((await AddAsync(c, "Staff 6")).IsSuccess);
        Assert.True((await AddAsync(c, "Staff 7")).IsSuccess);
        Assert.Equal(string.Format(Text.StaffLimit, 7), (await AddAsync(c, "Staff 8")).FieldErrors["isActive"]);

        // Lowering it does not cut anyone down; it only stops more being switched on.
        Assert.True((await c.Accounts.SetExtraStaffAsync(c.BusinessId, 0)).IsSuccess);
        Assert.Equal(7, (await c.Catalog.ListStaffAsync(c.BusinessId)).Count(s => s.IsActive));
        Assert.Contains("isActive", (await AddAsync(c, "Staff 8")).FieldErrors.Keys);
    }

    [Fact]
    public async Task A_deactivated_staff_member_makes_room_but_the_total_of_staff_records_is_capped_too()
    {
        var c = await SetupAsync();
        for (var i = 2; i <= 5; i++) Assert.True((await AddAsync(c, $"Active {i}")).IsSuccess);
        // Deactivated staff are kept for history and do not count as active, so they can be added up to the cap of 20 records in all.
        for (var i = 6; i <= 20; i++) Assert.True((await AddAsync(c, $"Gone {i}", active: false)).IsSuccess, $"record {i}");
        Assert.Equal(20, (await c.Catalog.ListStaffAsync(c.BusinessId)).Count);

        var twentyFirst = await AddAsync(c, "One too many", active: false);
        Assert.Contains("name", twentyFirst.FieldErrors.Keys);
        Assert.Equal(string.Format(Text.StaffRecordsLimit, 20), twentyFirst.FieldErrors["name"]);
        Assert.Equal(20, (await c.Catalog.ListStaffAsync(c.BusinessId)).Count);

        // Extra staff paid for raise the cap too.
        Assert.True((await c.Accounts.SetExtraStaffAsync(c.BusinessId, 2)).IsSuccess);
        Assert.True((await AddAsync(c, "Now fine", active: false)).IsSuccess);
    }

    [Fact]
    public async Task Extra_staff_can_only_be_set_between_zero_and_one_hundred_for_a_shop_that_exists()
    {
        var c = await SetupAsync();
        Assert.Equal(FailureKind.Validation, (await c.Accounts.SetExtraStaffAsync(c.BusinessId, -1)).Failure);
        Assert.Equal(FailureKind.Validation, (await c.Accounts.SetExtraStaffAsync(c.BusinessId, 101)).Failure);
        Assert.Equal(FailureKind.NotFound, (await c.Accounts.SetExtraStaffAsync(Guid.NewGuid(), 2)).Failure);
        Assert.True((await c.Accounts.SetExtraStaffAsync(c.BusinessId, 100)).IsSuccess);
        Assert.True((await c.Accounts.SetExtraStaffAsync(c.BusinessId, 0)).IsSuccess);
    }

    [Fact]
    public async Task A_shop_with_no_trial_and_no_paid_plan_keeps_one_staff_member()
    {
        var c = await SetupAsync(trial: false);
        Assert.Contains("isActive", (await AddAsync(c, "Second")).FieldErrors.Keys);
    }
}
