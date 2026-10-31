using System.Net;
using System.Text.RegularExpressions;
using VeskaFlow.Web;
using VeskaFlow.Web.Services;

namespace VeskaFlow.Web.Tests;

/// <summary>The in-app guide: every topic is complete, every "How does this work?" link has a topic, and the page shows what the person can use.</summary>
[Collection("http")]
public class HelpTests
{
    [Fact]
    public void Every_topic_is_complete_and_unique_and_points_at_a_real_admin_page()
    {
        Assert.Equal(HelpTopics.All.Count, HelpTopics.All.Select(t => t.Id).Distinct().Count());
        foreach (var t in HelpTopics.All)
        {
            Assert.Matches("^[a-z]+$", t.Id);
            Assert.False(string.IsNullOrWhiteSpace(t.Title));
            Assert.False(string.IsNullOrWhiteSpace(t.Summary));
            Assert.InRange(t.Steps.Length, 2, 7);
            Assert.All(t.Steps, s => Assert.False(string.IsNullOrWhiteSpace(s)));
            if (t.Link is not null) { Assert.StartsWith("/Admin", t.Link); Assert.False(string.IsNullOrWhiteSpace(t.LinkLabel)); }
        }
    }

    [Fact]
    public void Staff_see_only_the_topics_for_their_level_and_owners_see_all()
    {
        var staff = HelpTopics.For(AdminLevel.Staff).Select(t => t.Id).ToList();
        Assert.Contains("bookings", staff);
        Assert.Contains("walkin", staff);
        Assert.DoesNotContain("settings", staff);
        Assert.DoesNotContain("team", staff);
        Assert.DoesNotContain("reports", staff);
        Assert.Equal(HelpTopics.All.Count, HelpTopics.For(AdminLevel.Owner).Count);
        Assert.True(HelpTopics.For(AdminLevel.Manager).Count > staff.Count);
    }

    [Fact]
    public void Every_help_link_on_a_page_names_a_topic_that_exists()
    {
        var pages = Path.Combine(AppContext.BaseDirectory, "../../../../../src/VeskaFlow.Web/Pages/Admin");
        var found = Directory.EnumerateFiles(Path.GetFullPath(pages), "*.cshtml", SearchOption.AllDirectories)
            .SelectMany(f => Regex.Matches(File.ReadAllText(f), "_HelpLink\" model=\"@\\(\"([a-z]+)\"\\)\"").Select(m => m.Groups[1].Value)).ToList();
        Assert.True(found.Count >= 10);
        Assert.All(found, id => Assert.NotNull(HelpTopics.Find(id)));
    }

    [Fact]
    public async Task The_Help_page_lists_the_topics_searches_them_and_opens_the_one_asked_for_and_the_links_work()
    {
        var (factory, _) = await HttpApp.StartAsync();
        using var _f = factory;
        var owner = HttpApp.NewClient(factory);
        await HttpApp.PostFormAsync(owner, "/Register", new()
        {
            ["Input.BusinessName"] = "Help Shop", ["Input.OwnerName"] = "Owner", ["Input.Email"] = $"help{Guid.NewGuid():N}@example.com",
            ["Input.Password"] = "correct-horse-battery", ["Input.Accept"] = "true", ["Input.Industry"] = "barbershop",
        });

        var page = WebUtility.HtmlDecode(await owner.GetStringAsync("/Admin/Help"));
        Assert.Contains(Text.HelpTitle, page);
        foreach (var t in HelpTopics.All) Assert.Contains($"id=\"{t.Id}\"", page);
        Assert.Contains("href=\"/Admin/Help\"", page);                                              // in the menu

        var found = WebUtility.HtmlDecode(await owner.GetStringAsync("/Admin/Help?q=payment"));
        Assert.Contains("id=\"done\"", found);
        Assert.DoesNotContain("id=\"team\"", found);
        Assert.Contains(Text.HelpNone, WebUtility.HtmlDecode(await owner.GetStringAsync("/Admin/Help?q=zzzznothing")));

        var one = WebUtility.HtmlDecode(await owner.GetStringAsync("/Admin/Help?topic=promo"));
        Assert.Matches("id=\"promo\"[^>]*>\\s*<details open", one);

        var withLink = WebUtility.HtmlDecode(await owner.GetStringAsync("/Admin"));                 // the page itself links to its topic
        Assert.Contains("/Admin/Help?topic=bookings#bookings", withLink);

        foreach (var t in HelpTopics.All.Where(t => t.Link is not null && t.Id != "sale"))   // Record a sale exists only once it is switched on                           // "Open ..." buttons never lead to a missing page
        {
            var r = await owner.GetAsync(t.Link);
            Assert.NotEqual(HttpStatusCode.NotFound, r.StatusCode);
        }
    }
}
