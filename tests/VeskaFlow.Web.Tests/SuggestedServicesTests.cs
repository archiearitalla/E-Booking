using VeskaFlow.Web.Data;
using VeskaFlow.Web.Services;
using Microsoft.EntityFrameworkCore;
using static VeskaFlow.Web.Tests.Seed;

namespace VeskaFlow.Web.Tests;

/// <summary>"Add suggested services" fills in the usual services for the industry and skips ones already there.</summary>
public class SuggestedServicesTests
{
    private static string Id(string prefix) => prefix + Guid.NewGuid().ToString("N")[..10];

    [Fact]
    public async Task Adds_only_the_missing_services_and_a_second_run_adds_nothing()
    {
        var db = new TestDbFactory(await TestDb.ConnectionStringAsync());
        var clock = new TestClock(BaseNow);
        var owner = (await new OnboardingService(db, clock).RegisterAsync(
            new RegisterRequest("Clinic", Id("ss"), "Dr A", $"{Id("o")}@example.com", "correct-horse-1", Industry: "clinic"))).Value!;
        var catalog = new CatalogAdminService(db, clock);
        var suggested = Industries.Suggested("clinic");

        // The starter "Consultation" is already there, so it is skipped (even if typed in another case).
        var first = await catalog.AddSuggestedServicesAsync(owner.BusinessId);
        Assert.Equal(suggested.Count - 1, first);
        Assert.Equal(0, await catalog.AddSuggestedServicesAsync(owner.BusinessId));

        var names = (await catalog.ListServicesAsync(owner.BusinessId)).Select(s => s.Name).ToList();
        Assert.Equal(suggested.Count + 1, names.Count); // the suggestions, plus the "Other" service every shop has
        Assert.Equal(names.Count, names.Select(n => n.ToLowerInvariant()).Distinct().Count());

        // New services are linked to the existing staff so they can be booked.
        await using var ctx = db.CreateDbContext();
        var staffId = await ctx.StaffMembers.Where(s => s.BusinessId == owner.BusinessId).Select(s => s.Id).SingleAsync();
        Assert.Equal(suggested.Count + 1, await ctx.StaffServices.CountAsync(x => x.StaffId == staffId));
    }

    [Fact]
    public async Task Does_not_touch_another_business()
    {
        var db = new TestDbFactory(await TestDb.ConnectionStringAsync());
        var clock = new TestClock(BaseNow);
        var onboarding = new OnboardingService(db, clock);
        var a = (await onboarding.RegisterAsync(new RegisterRequest("A", Id("sa"), "A", $"{Id("o")}@example.com", "correct-horse-1", Industry: "salon"))).Value!;
        var b = (await onboarding.RegisterAsync(new RegisterRequest("B", Id("sb"), "B", $"{Id("o")}@example.com", "correct-horse-1", Industry: "salon"))).Value!;
        var catalog = new CatalogAdminService(db, clock);

        await catalog.AddSuggestedServicesAsync(a.BusinessId);

        Assert.Equal(2, (await catalog.ListServicesAsync(b.BusinessId)).Count); // only its starter service and "Other"
    }
}
