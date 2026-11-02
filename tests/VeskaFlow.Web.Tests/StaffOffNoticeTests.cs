using System.Net;
using Microsoft.EntityFrameworkCore;
using VeskaFlow.Web.Data;

namespace VeskaFlow.Web.Tests;

/// <summary>
/// A customer who picks one person who is off (or fully booked) that day used to read only "No times are available". Now the page says that person has
/// no free time but others do, and offers everyone. When nobody has time it keeps the plain message.
/// </summary>
[Collection("http")]
public class StaffOffNoticeTests
{
    private static async Task<(string Page, string Without)> LoadAsync(HttpClient client, string slug, Guid serviceId, Guid? staffId, DateOnly day)
    {
        var withStaff = $"/b/{slug}?serviceId={serviceId}&date={day:yyyy-MM-dd}" + (staffId is { } s ? $"&staffId={s}" : "");
        return (WebUtility.HtmlDecode(await client.GetStringAsync(withStaff)), "");
    }

    [Fact]
    public async Task A_person_who_is_off_is_named_and_everyone_else_is_offered_and_when_nobody_is_free_the_plain_message_stays()
    {
        var (factory, client) = await HttpApp.StartAsync();
        using var _f = factory;
        var db = new TestDbFactory(await TestDb.ConnectionStringAsync());
        var shop = await Seed.CreateShopAsync(staffCount: 2);
        var day = Seed.NextOpenDay();
        var dayStartUtc = new DateTime(day.Year, day.Month, day.Day, 0, 0, 0, DateTimeKind.Utc).AddHours(-8); // Manila midnight
        string slug;
        await using (var ctx = db.CreateDbContext())
        {
            slug = await ctx.Businesses.Where(b => b.Id == shop.BusinessId).Select(b => b.Slug).SingleAsync();
            ctx.Blackouts.Add(new Blackout { BusinessId = shop.BusinessId, StaffId = shop.StaffIds[0], StartsAtUtc = dayStartUtc, EndsAtUtc = dayStartUtc.AddDays(1), Reason = "off" });
            await ctx.SaveChangesAsync();
        }

        // The first person is off: named, with a way to see everyone.
        var (off, _) = await LoadAsync(client, slug, shop.ServiceId, shop.StaffIds[0], day);
        Assert.Contains(Text.BookStaffNoTimes("Staff 1"), off);
        Assert.Contains(Text.BookShowAnyone, off);
        Assert.Contains(Text.WaitlistLinkHintStaff("Staff 1"), off);
        Assert.DoesNotContain(Text.BookNoTimes, off);
        Assert.DoesNotContain("class=\"slot\"", off);
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(off, "person-off"));                       // only Staff 1's chip is marked
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(off, System.Text.RegularExpressions.Regex.Escape(Text.BookStaffNoTimeTag)));

        // The second person is free: times are listed and there is no such notice.
        var (free, _) = await LoadAsync(client, slug, shop.ServiceId, shop.StaffIds[1], day);
        Assert.Contains("class=\"slot\"", free);
        Assert.DoesNotContain(Text.BookStaffNoTimes("Staff 2"), free);
        Assert.Contains("person-off", free);                                                                           // Staff 1 is still marked while Staff 2 is looked at

        // Choosing "anyone available" lists the free person's times.
        var (anyone, _) = await LoadAsync(client, slug, shop.ServiceId, null, day);
        Assert.Contains("class=\"slot\"", anyone);

        // When the second person is off too, nobody has time: the plain message, no offer of "anyone".
        await using (var ctx = db.CreateDbContext())
        {
            ctx.Blackouts.Add(new Blackout { BusinessId = shop.BusinessId, StaffId = shop.StaffIds[1], StartsAtUtc = dayStartUtc, EndsAtUtc = dayStartUtc.AddDays(1), Reason = "off" });
            await ctx.SaveChangesAsync();
        }
        var (nobody, _) = await LoadAsync(client, slug, shop.ServiceId, shop.StaffIds[0], day);
        Assert.Contains(Text.BookNoTimes, nobody);
        Assert.DoesNotContain(Text.BookShowAnyone, nobody);
        Assert.Contains(Text.WaitlistLinkHint, nobody);
        Assert.Equal(2, System.Text.RegularExpressions.Regex.Matches(nobody, "person-off").Count);                    // both are marked now

        // Before a service is chosen nobody is marked.
        var blank = WebUtility.HtmlDecode(await client.GetStringAsync($"/b/{slug}"));
        Assert.DoesNotContain("person-off", blank);
    }

    [Fact]
    public void The_no_shows_label_uses_a_non_breaking_hyphen_so_it_does_not_split_on_a_phone()
    {
        Assert.Contains('‑', Text.KpiNoShows);
        Assert.DoesNotContain('-', Text.KpiNoShows);
    }
}
