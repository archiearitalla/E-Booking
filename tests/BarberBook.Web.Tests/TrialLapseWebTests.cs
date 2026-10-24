using System.Net;
using BarberBook.Web.Data;
using Microsoft.EntityFrameworkCore;
using static BarberBook.Web.Tests.HttpApp;

namespace BarberBook.Web.Tests;

/// <summary>When the trial ends the back office is locked (BUSINESS_RULES 18.2), over HTTP against real PostgreSQL.</summary>
[Collection("http")]
public class TrialLapseWebTests
{
    private static async Task<(HttpClient Client, Guid BusinessId, TestDbFactory Db)> RegisteredOwnerAsync(AppFactory factory)
    {
        var client = NewClient(factory);
        var slug = Unique("lap");
        var response = await PostFormAsync(client, "/Register", new()
        {
            ["Input.BusinessName"] = "Lapse Test Clinic", ["Input.Slug"] = slug, ["Input.OwnerName"] = "Dr Reyes",
            ["Input.Email"] = $"{Unique("o")}@example.com", ["Input.Password"] = "correct-horse-1", ["Input.Industry"] = "clinic",
        });
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);

        var db = new TestDbFactory(await TestDb.ConnectionStringAsync());
        await using var ctx = db.CreateDbContext();
        var id = await ctx.Businesses.Where(b => b.Slug == slug).Select(b => b.Id).SingleAsync();
        return (client, id, db);
    }

    private static async Task EndTrialAsync(TestDbFactory db, Guid businessId)
    {
        await using var ctx = db.CreateDbContext();
        await ctx.Businesses.Where(b => b.Id == businessId)
            .ExecuteUpdateAsync(s => s.SetProperty(b => b.TrialEndsAtUtc, DateTime.UtcNow.AddDays(-1)));
    }

    [Fact]
    public async Task While_the_trial_runs_the_back_office_works_and_the_trial_ended_page_sends_the_owner_back()
    {
        var (factory, _) = await StartAsync();
        using var _f = factory;
        var (client, _, _) = await RegisteredOwnerAsync(factory);

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/Admin")).StatusCode);
        var ended = await client.GetAsync("/Admin/TrialEnded");
        Assert.Equal(HttpStatusCode.Redirect, ended.StatusCode);
        Assert.Equal("/Admin", ended.Headers.Location!.OriginalString);
    }

    [Fact]
    public async Task After_the_trial_every_admin_page_and_post_goes_to_the_trial_ended_page_and_changes_nothing()
    {
        var (factory, _) = await StartAsync();
        using var _f = factory;
        var (client, businessId, db) = await RegisteredOwnerAsync(factory);
        await EndTrialAsync(db, businessId);

        foreach (var path in new[] { "/Admin", "/Admin/Services", "/Admin/Staff", "/Admin/Closures", "/Admin/Settings", "/Admin/Audit", "/Admin/About" })
        {
            var r = await client.GetAsync(path);
            Assert.Equal(HttpStatusCode.Redirect, r.StatusCode);
            Assert.Equal("/Admin/TrialEnded", r.Headers.Location!.OriginalString);
        }

        // The notice itself is reachable, says so, and offers a way to sign out.
        var page = await client.GetStringAsync("/Admin/TrialEnded");
        Assert.Contains(Text.TrialEndedTitle, WebUtility.HtmlDecode(page));

        // A post with a valid antiforgery token is refused the same way and the settings are untouched.
        var post = await PostFormAsync(client, "/Admin/Settings", new()
        {
            ["Input.Name"] = "Hijacked", ["Input.TimeZone"] = "Asia/Manila", ["Input.SlotMinutes"] = "30",
            ["Input.LeadTimeMinutes"] = "60", ["Input.HorizonDays"] = "30", ["Input.CancelCutoffHours"] = "2",
        }, tokenPage: "/Admin/TrialEnded");
        Assert.Equal(HttpStatusCode.Redirect, post.StatusCode);
        Assert.Equal("/Admin/TrialEnded", post.Headers.Location!.OriginalString);
        await using var ctx = db.CreateDbContext();
        Assert.Equal("Lapse Test Clinic", await ctx.Businesses.Where(b => b.Id == businessId).Select(b => b.Name).SingleAsync());
    }

    [Fact]
    public async Task A_lapsed_business_can_still_sign_out_and_its_public_page_says_it_is_closed()
    {
        var (factory, _) = await StartAsync();
        using var _f = factory;
        var (client, businessId, db) = await RegisteredOwnerAsync(factory);
        await EndTrialAsync(db, businessId);
        await using var ctx = db.CreateDbContext();
        var slug = await ctx.Businesses.Where(b => b.Id == businessId).Select(b => b.Slug).SingleAsync();

        var signOut = await PostFormAsync(client, "/Logout", new(), tokenPage: "/Admin/TrialEnded");
        Assert.Equal(HttpStatusCode.Redirect, signOut.StatusCode);
        Assert.Equal("/", signOut.Headers.Location!.OriginalString);

        var publicPage = WebUtility.HtmlDecode(await client.GetStringAsync($"/b/{slug}"));
        Assert.Contains(Text.TrialEndedPublic, publicPage);
    }
}
