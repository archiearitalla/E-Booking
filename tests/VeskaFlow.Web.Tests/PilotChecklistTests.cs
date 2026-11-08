using System.Net;
using Microsoft.EntityFrameworkCore;
using VeskaFlow.Web.Services;

namespace VeskaFlow.Web.Tests;

/// <summary>The platform owner's pilot checklist: stable ids, ticks that are saved, and nobody else sees it.</summary>
[Collection("http")]
public class PilotChecklistTests
{
    [Fact]
    public void Every_item_has_its_own_stable_id_and_words_and_the_questions_are_not_tickable()
    {
        var all = PilotChecklist.Sections.SelectMany(s => s.Items).ToList();
        Assert.Equal(all.Count, all.Select(i => i.Id).Distinct().Count());
        Assert.All(all, i => { Assert.False(string.IsNullOrWhiteSpace(i.Title)); Assert.DoesNotContain(" ", i.Id); });
        Assert.Equal(4, PilotChecklist.Sections.Count);
        Assert.All(PilotChecklist.Sections, s => Assert.NotEmpty(s.Items));
        Assert.False(PilotChecklist.Sections.Single(s => s.Id == "ask").Tickable);
        Assert.False(PilotChecklist.IsKnown("q-use")); // a question can not be ticked
        Assert.True(PilotChecklist.IsKnown("phone"));
        Assert.False(PilotChecklist.IsKnown(null));
        Assert.False(PilotChecklist.IsKnown("made-up"));
    }

    private static AccountControlService Accounts(TestDbFactory db, TestClock clock) =>
        new(db, clock, new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build(), new CapturingEmailSender());

    [Fact]
    public async Task A_tick_is_saved_with_its_time_can_be_taken_back_and_an_unknown_item_stores_nothing()
    {
        var db = new TestDbFactory(await TestDb.ConnectionStringAsync());
        var clock = new TestClock(Seed.BaseNow);
        var accounts = Accounts(db, clock);
        await using (var ctx = db.CreateDbContext()) await ctx.PlatformSettings.Where(s => s.Key.StartsWith("pilot:")).ExecuteDeleteAsync();

        Assert.Empty(await accounts.GetPilotChecksAsync());
        Assert.True(await accounts.SetPilotCheckAsync("phone", true));
        Assert.True(await accounts.SetPilotCheckAsync("email", true));
        var done = await accounts.GetPilotChecksAsync();
        Assert.Equal(new[] { "email", "phone" }, done.Keys.OrderBy(k => k));
        Assert.Equal(Seed.BaseNow.UtcDateTime, done["phone"]);

        Assert.True(await accounts.SetPilotCheckAsync("phone", false));
        Assert.Equal(new[] { "email" }, (await accounts.GetPilotChecksAsync()).Keys);

        Assert.False(await accounts.SetPilotCheckAsync("made-up", true));
        Assert.False(await accounts.SetPilotCheckAsync("q-use", true)); // not tickable
        Assert.False(await accounts.SetPilotCheckAsync(null, true));
        await using var check = db.CreateDbContext();
        Assert.False(await check.PlatformSettings.AnyAsync(s => s.Key == "pilot:made-up" || s.Key == "pilot:q-use"));
    }

    [Fact]
    public async Task Only_the_platform_owner_sees_the_page_and_a_tick_over_HTTP_is_remembered()
    {
        var bossEmail = $"boss{Guid.NewGuid():N}@example.com";
        var (factory, _) = await HttpApp.StartAsync(platformAdminEmails: bossEmail);
        using var _f = factory;
        Dictionary<string, string> Form(string email, string shop) => new()
        {
            ["Input.BusinessName"] = shop, ["Input.OwnerName"] = "Owner", ["Input.Email"] = email, ["Input.Password"] = "correct-horse-battery", ["Input.Accept"] = "true", ["Input.Industry"] = "barbershop",
        };
        var boss = HttpApp.NewClient(factory);
        await HttpApp.PostFormAsync(boss, "/Register", Form(bossEmail, "Boss Shop"));
        var tenant = HttpApp.NewClient(factory);
        await HttpApp.PostFormAsync(tenant, "/Register", Form($"t{Guid.NewGuid():N}@example.com", "Some Salon"));
        var db = new TestDbFactory(await TestDb.ConnectionStringAsync());
        await using (var ctx = db.CreateDbContext()) await ctx.PlatformSettings.Where(s => s.Key.StartsWith("pilot:")).ExecuteDeleteAsync();

        Assert.Equal(HttpStatusCode.NotFound, (await tenant.GetAsync("/Platform/Pilot")).StatusCode);                                     // a shop owner finds nothing
        var signedOut = await HttpApp.NewClient(factory).GetAsync("/Platform/Pilot");                                                    // someone signed out is sent to sign in
        Assert.Equal(HttpStatusCode.Redirect, signedOut.StatusCode);
        Assert.Contains("Login", signedOut.Headers.Location!.ToString());

        var page = WebUtility.HtmlDecode(await boss.GetStringAsync("/Platform/Pilot"));
        Assert.Contains(Text.PilotTitle, page);
        Assert.Contains("Test the main flows on a real phone", page);
        Assert.Contains("After two weeks: ask the owner", page);
        Assert.DoesNotContain("pages_messaging", page);                                                                                   // the App Review guide is its own page, not mixed in here
        Assert.Contains("href=\"/Platform/MetaReview\"", page);                                                                            // but linked from here and from the Platform page
        Assert.Contains("href=\"/Platform/MetaReview\"", WebUtility.HtmlDecode(await boss.GetStringAsync("/Platform")));
        Assert.Contains("0 / 8", page);                                                                                                  // before you charge: none done yet
        Assert.Contains("href=\"/Platform/Pilot\"", WebUtility.HtmlDecode(await boss.GetStringAsync("/Platform")));                      // linked from the Platform page

        var ticked = await HttpApp.PostFormAsync(boss, "/Platform/Pilot?handler=Toggle", new() { ["id"] = "phone", ["done"] = "true" }, tokenPage: "/Platform/Pilot");
        Assert.Equal(HttpStatusCode.Redirect, ticked.StatusCode);
        var after = WebUtility.HtmlDecode(await boss.GetStringAsync("/Platform/Pilot"));
        Assert.Contains("1 / 8", after);
        Assert.Contains("aria-pressed=\"true\"", after);

        Assert.Equal(HttpStatusCode.NotFound, (await HttpApp.PostFormAsync(boss, "/Platform/Pilot?handler=Toggle", new() { ["id"] = "made-up", ["done"] = "true" }, tokenPage: "/Platform/Pilot")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await HttpApp.PostFormAsync(tenant, "/Platform/Pilot?handler=Toggle", new() { ["id"] = "email", ["done"] = "true" }, tokenPage: "/Admin")).StatusCode);
        await using var check = db.CreateDbContext();
        Assert.False(await check.PlatformSettings.AnyAsync(s => s.Key == "pilot:email"));                                                // the other shop's tick did nothing
    }

    [Fact]
    public void The_Meta_review_guide_has_its_own_sections_with_the_permission_texts_and_where_to_click_and_its_items_can_be_ticked()
    {
        Assert.Equal(3, MetaReviewGuide.Sections.Count);
        var all = MetaReviewGuide.Sections.SelectMany(s => s.Items).ToList();
        Assert.Equal(all.Count, all.Select(i => i.Id).Distinct().Count());
        Assert.Empty(all.Select(i => i.Id).Intersect(PilotChecklist.Sections.SelectMany(s => s.Items).Select(i => i.Id)));                // no id shared with the pilot checklist
        Assert.All(new[] { "r-show", "r-meta", "r-read", "r-msg", "r-data" }, id => Assert.False(string.IsNullOrWhiteSpace(all.Single(i => i.Id == id).Copy)));
        Assert.All(all, i => Assert.Contains("Where", (i.Detail ?? "") + " " + MetaReviewGuide.Sections.First(s => s.Items.Contains(i)).Intro)); // each step says where to click
        Assert.True(PilotChecklist.IsKnown("r-msg"));
        Assert.False(PilotChecklist.IsKnown("r-made-up"));
    }

    [Fact]
    public async Task The_Meta_review_page_is_for_the_platform_owner_only_and_a_tick_there_is_remembered()
    {
        var bossEmail = $"boss{Guid.NewGuid():N}@example.com";
        var (factory, _) = await HttpApp.StartAsync(platformAdminEmails: bossEmail);
        using var _f = factory;
        Dictionary<string, string> Form(string email, string shop) => new()
        {
            ["Input.BusinessName"] = shop, ["Input.OwnerName"] = "Owner", ["Input.Email"] = email, ["Input.Password"] = "correct-horse-battery", ["Input.Accept"] = "true", ["Input.Industry"] = "barbershop",
        };
        var boss = HttpApp.NewClient(factory);
        await HttpApp.PostFormAsync(boss, "/Register", Form(bossEmail, "Boss Shop"));
        var tenant = HttpApp.NewClient(factory);
        await HttpApp.PostFormAsync(tenant, "/Register", Form($"t{Guid.NewGuid():N}@example.com", "Some Salon"));
        var db = new TestDbFactory(await TestDb.ConnectionStringAsync());
        await using (var ctx = db.CreateDbContext()) await ctx.PlatformSettings.Where(s => s.Key.StartsWith("pilot:")).ExecuteDeleteAsync();

        Assert.Equal(HttpStatusCode.NotFound, (await tenant.GetAsync("/Platform/MetaReview")).StatusCode);
        Assert.Equal(HttpStatusCode.Redirect, (await HttpApp.NewClient(factory).GetAsync("/Platform/MetaReview")).StatusCode);

        var page = WebUtility.HtmlDecode(await boss.GetStringAsync("/Platform/MetaReview"));
        Assert.Contains("Meta App Review guide", page);
        Assert.Contains("We use pages_messaging only to reply to a person who wrote to the Page first", page);        // the text to paste
        Assert.Contains("business.facebook.com/settings", page);                                                       // where to click
        Assert.Contains("Publish", page);
        Assert.Contains("0 / 5", page);                                                                                // before you submit: none done yet

        var ticked = await HttpApp.PostFormAsync(boss, "/Platform/MetaReview?handler=Toggle", new() { ["id"] = "r-verify", ["done"] = "true" }, tokenPage: "/Platform/MetaReview");
        Assert.Equal(HttpStatusCode.Redirect, ticked.StatusCode);
        Assert.Contains("1 / 5", WebUtility.HtmlDecode(await boss.GetStringAsync("/Platform/MetaReview")));
        Assert.DoesNotContain("1 / 8", WebUtility.HtmlDecode(await boss.GetStringAsync("/Platform/Pilot")));          // the pilot page does not count it

        Assert.Equal(HttpStatusCode.NotFound, (await HttpApp.PostFormAsync(boss, "/Platform/MetaReview?handler=Toggle", new() { ["id"] = "phone", ["done"] = "true" }, tokenPage: "/Platform/MetaReview")).StatusCode); // a pilot item is not for this page
        Assert.Equal(HttpStatusCode.NotFound, (await HttpApp.PostFormAsync(tenant, "/Platform/MetaReview?handler=Toggle", new() { ["id"] = "r-live", ["done"] = "true" }, tokenPage: "/Admin")).StatusCode);
        await using var check = db.CreateDbContext();
        Assert.False(await check.PlatformSettings.AnyAsync(s => s.Key == "pilot:r-live" || s.Key == "pilot:phone"));
    }
}
