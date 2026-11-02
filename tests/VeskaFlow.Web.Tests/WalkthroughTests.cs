using System.Net;
using VeskaFlow.Web.Services;

namespace VeskaFlow.Web.Tests;

/// <summary>The platform owner's walkthroughs: well formed, kept apart from the tick lists, and seen by nobody else.</summary>
[Collection("http")]
public class WalkthroughTests
{
    [Fact]
    public void The_walkthroughs_are_well_formed_and_do_not_share_ids_with_the_tick_lists()
    {
        var all = WalkthroughGuide.Sections.SelectMany(s => s.Items).ToList();
        Assert.True(WalkthroughGuide.Sections.Count >= 6);
        Assert.Equal(all.Count, all.Select(i => i.Id).Distinct().Count());
        Assert.All(WalkthroughGuide.Sections, s => { Assert.False(s.Tickable); Assert.NotEmpty(s.Items); Assert.False(string.IsNullOrWhiteSpace(s.Title)); });
        Assert.All(all, i => { Assert.False(string.IsNullOrWhiteSpace(i.Title)); Assert.False(string.IsNullOrWhiteSpace(i.Detail)); });
        var others = PilotChecklist.Sections.SelectMany(s => s.Items).Concat(MetaReviewGuide.Sections.SelectMany(s => s.Items)).Select(i => i.Id);
        Assert.Empty(all.Select(i => i.Id).Intersect(others));
        Assert.False(PilotChecklist.IsKnown(all[0].Id));                                                                  // nothing to tick
        Assert.All(all.Where(i => i.Id.StartsWith("w-demo") || i.Id.StartsWith("w-setup")), i => Assert.Matches("Where:|Say:|Do:|Tell", i.Detail!)); // each step tells what to do or say
    }

    [Fact]
    public async Task Only_the_platform_owner_sees_the_walkthroughs_and_they_are_linked_from_the_platform_page()
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

        Assert.Equal(HttpStatusCode.NotFound, (await tenant.GetAsync("/Platform/Walkthroughs")).StatusCode);
        Assert.Equal(HttpStatusCode.Redirect, (await HttpApp.NewClient(factory).GetAsync("/Platform/Walkthroughs")).StatusCode);

        var page = WebUtility.HtmlDecode(await boss.GetStringAsync("/Platform/Walkthroughs"));
        Assert.Contains(Text.WalkthroughsTitle, page);
        Assert.Contains("A 10-minute demo for a new client", page);
        Assert.Contains("Set up a new shop with the owner", page);
        Assert.Contains("Where: /Register", page);
        Assert.Contains("Offering VeskaFlow to a shop owner", page);
        Assert.Contains("Do you take bookings by message or phone call?", page);                                            // the ready message
        Assert.Contains("data-copy=\"copy-w-offer-1\"", page);                                                               // with its Copy button
        Assert.Contains("data-copy=\"copy-w-offer-2\"", page);                                                               // and the one without a link
        var noLink = WalkthroughGuide.Sections.SelectMany(s => s.Items).Single(i => i.Id == "w-offer-2").Copy!;
        Assert.DoesNotContain("veskacode.com", noLink);
        Assert.DoesNotContain("http", noLink);
        Assert.Contains("a service, a staff member and a time", noLink);                                                            // not "barber": the message fits any kind of shop
        Assert.DoesNotContain("barber", noLink);
        Assert.Contains("phone number", noLink);                                                                            // asks for their number instead
        Assert.DoesNotContain("pilot-check", page);                                                                      // no tick buttons
        Assert.Contains("href=\"/Platform/Walkthroughs\"", WebUtility.HtmlDecode(await boss.GetStringAsync("/Platform")));
    }
}
