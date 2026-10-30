using Microsoft.EntityFrameworkCore;
using VeskaFlow.Web.Services;

namespace VeskaFlow.Web.Tests;

/// <summary>Every kind of business that can sign up: the list, the starter service, the suggested services, the staff label.</summary>
[Collection("http")]
public class IndustryTests
{
    private static string Id(string prefix) => prefix + Guid.NewGuid().ToString("N")[..10];

    [Fact]
    public void Every_industry_has_a_label_a_starter_service_a_staff_label_and_sensible_suggestions()
    {
        var keys = Industries.All.Select(i => i.Key).ToList();
        Assert.Equal(keys.Count, keys.Distinct().Count());
        Assert.True(keys.Count >= 11);
        Assert.Contains(Industries.Default, keys);

        foreach (var (key, label, starter) in Industries.All)
        {
            Assert.True(Industries.IsValid(key));
            Assert.False(string.IsNullOrWhiteSpace(label));
            Assert.False(string.IsNullOrWhiteSpace(starter));
            Assert.False(string.IsNullOrWhiteSpace(Industries.StaffLabel(key)));

            var suggested = Industries.Suggested(key);
            Assert.InRange(suggested.Count, 6, 12);
            var names = suggested.Select(s => s.Name.ToLowerInvariant()).ToList();
            Assert.Equal(names.Count, names.Distinct().Count()); // no duplicate in one list
            Assert.All(suggested, s =>
            {
                Assert.InRange(s.Name.Length, 2, 60);
                Assert.InRange(s.DurationMinutes, 5, 480);
                Assert.True(s.FollowUpDays is null or > 0 and <= 730);
            });
        }
        Assert.False(Industries.IsValid("nope"));
        Assert.False(Industries.IsValid(null));
        Assert.Equal("Therapist", Industries.StaffLabel("spa"));
        Assert.Equal("Barber", Industries.StaffLabel("unknown")); // an unknown key falls back to the default
    }

    [Fact]
    public async Task A_shop_of_every_kind_can_sign_up_and_add_its_suggested_services()
    {
        var db = new TestDbFactory(await TestDb.ConnectionStringAsync());
        var clock = new TestClock(Seed.BaseNow);
        var onboarding = new OnboardingService(db, clock);
        var catalog = new CatalogAdminService(db, clock);

        foreach (var (key, _, starter) in Industries.All)
        {
            var owner = (await onboarding.RegisterAsync(
                new RegisterRequest("Shop " + key, Id("in"), "Owner", $"{Id("o")}@example.com", "correct-horse-1", Industry: key))).Value!;
            await using var ctx = db.CreateDbContext();
            Assert.Equal(key, await ctx.Businesses.Where(b => b.Id == owner.BusinessId).Select(b => b.Industry).SingleAsync());
            Assert.Contains(starter, await ctx.Services.Where(s => s.BusinessId == owner.BusinessId).Select(s => s.Name).ToListAsync());

            var added = await catalog.AddSuggestedServicesAsync(owner.BusinessId);
            var names = (await catalog.ListServicesAsync(owner.BusinessId)).Select(s => s.Name.ToLowerInvariant()).ToList();
            Assert.Equal(names.Count, names.Distinct().Count());
            Assert.True(added >= Industries.Suggested(key).Count - 1);
        }

        var bad = await onboarding.RegisterAsync(
            new RegisterRequest("Bad", Id("in"), "Owner", $"{Id("o")}@example.com", "correct-horse-1", Industry: "mining"));
        Assert.Contains("industry", bad.FieldErrors.Keys);
    }

    [Fact]
    public async Task The_sign_up_page_lists_them_all_and_the_booking_page_uses_the_staff_label()
    {
        var (factory, client) = await HttpApp.StartAsync();
        using var _f = factory;
        var register = System.Net.WebUtility.HtmlDecode(await client.GetStringAsync("/Register"));
        foreach (var (_, label, _) in Industries.All) Assert.Contains(label, register);

        var db = new TestDbFactory(await TestDb.ConnectionStringAsync());
        var slug = Id("sp");
        var owner = (await new OnboardingService(db, TimeProvider.System).RegisterAsync(
            new RegisterRequest("Calm Spa", slug, "Owner", $"{Id("o")}@example.com", "correct-horse-1", Industry: "spa", TrialDays: 30))).Value!;
        await new CatalogAdminService(db, TimeProvider.System).AddSuggestedServicesAsync(owner.BusinessId);
        var page = System.Net.WebUtility.HtmlDecode(await HttpApp.NewClient(factory).GetStringAsync($"/b/{slug}"));
        Assert.Contains("Therapist", page);
        Assert.DoesNotContain("<legend>Barber</legend>", page);
    }
}
