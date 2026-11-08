using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using VeskaFlow.Web.Data;
using VeskaFlow.Web.Services;

namespace VeskaFlow.Web.Tests;

/// <summary>
/// The assistant knows the people who work in the shop and the shop's own facts, so "is Archie free on Nov 3?" gets an answer from that person's real
/// free times, and questions about hours, deposits and cancelling are answered from what the shop set. Nothing is sent to a real model.
/// </summary>
[Collection("http")]
public class AiStaffAwareTests
{
    private sealed record Ctx(TestDbFactory Db, TestClock Clock, AiReceptionistService Chat, FakeAiModel Model);

    private static async Task<Ctx> SetupAsync(FakeAiModel model)
    {
        var db = new TestDbFactory(await TestDb.ConnectionStringAsync());
        var clock = new TestClock(Seed.BaseNow);
        var config = new ConfigurationBuilder().Build();
        var access = new AiAccess(db, clock, config, model);
        var chat = new AiReceptionistService(new ShopCatalogService(db, clock), new BookingService(db, clock), access, model, db, clock, NullLogger<AiReceptionistService>.Instance);
        return new Ctx(db, clock, chat, model);
    }

    private static async Task<(Shop Shop, string Slug)> ShopAsync(Ctx c, int staff = 2)
    {
        var shop = await Seed.CreateShopAsync(staffCount: staff);
        await using var ctx = c.Db.CreateDbContext();
        await ctx.Businesses.Where(b => b.Id == shop.BusinessId).ExecuteUpdateAsync(u =>
            u.SetProperty(b => b.AiAddonUntilUtc, c.Clock.GetUtcNow().UtcDateTime.AddDays(30)));
        return (shop, await ctx.Businesses.Where(b => b.Id == shop.BusinessId).Select(b => b.Slug).SingleAsync());
    }

    private static Task<ServiceResult<AiChatReply>> SendAsync(Ctx c, string slug, string text) => c.Chat.SendAsync(slug, null, text, "https://flow.example.com");

    /// <summary>One customer message may use two model calls (the tool, then the reply): decide by what the model was just given, not by a counter.</summary>
    private static AiStep Script(IReadOnlyList<AiMessage> messages, Func<string, AiStep> forText)
    {
        var last = messages[^1];
        if (last.Blocks.Any(b => b.Kind == AiBlockKind.ToolResult)) return FakeAiModel.Reply("done");
        return forText(last.Blocks.First(b => b.Kind == AiBlockKind.Text).Text);
    }

    private static List<string?> Times(AiBlock toolResult)
    {
        using var json = JsonDocument.Parse(toolResult.Text);
        return json.RootElement.GetProperty("times").EnumerateArray().Select(e => e.GetString()).ToList();
    }

    [Fact]
    public async Task The_prompt_lists_the_people_and_the_shop_facts_the_customer_may_ask_about()
    {
        var model = new FakeAiModel((_, _) => FakeAiModel.Reply("ok"));
        var c = await SetupAsync(model);
        var (shop, slug) = await ShopAsync(c);
        await using (var ctx = c.Db.CreateDbContext())
        {
            ctx.BusinessHours.Add(new BusinessHour { BusinessId = shop.BusinessId, DayOfWeek = 1, OpenTime = new TimeOnly(9, 0), CloseTime = new TimeOnly(18, 0) });
            await ctx.Businesses.Where(b => b.Id == shop.BusinessId).ExecuteUpdateAsync(u => u
                .SetProperty(b => b.PayInstructions, "Send the deposit to GCash 0917 000 0000, then show us the screenshot.")
                .SetProperty(b => b.CancelCutoffHours, 6).SetProperty(b => b.LeadTimeMinutes, 90));
            await ctx.SaveChangesAsync();
        }

        Assert.True((await SendAsync(c, slug, "available ba si Staff 1 bukas?")).IsSuccess);
        var system = model.Systems[0];
        Assert.Contains("People (id | name | does these services):", system);
        Assert.Contains($"id={shop.StaffIds[0]} | Staff 1 | Haircut", system);
        Assert.Contains($"id={shop.StaffIds[1]} | Staff 2 | Haircut", system);
        Assert.Contains("Shop information:", system);
        Assert.Contains("- Monday: 9:00 AM to 6:00 PM", system);
        Assert.Contains("at least 90 minutes before it starts", system);
        Assert.Contains("until 6 hour(s) before it starts", system);
        Assert.Contains("Send the deposit to GCash 0917 000 0000", system);
        Assert.Contains("Never say you cannot check a person", system);
        Assert.Contains("what VeskaFlow is", system, StringComparison.OrdinalIgnoreCase);                       // it can explain the system in a sentence
    }

    [Fact]
    public async Task Asking_about_one_person_shows_only_that_persons_free_times_and_a_bad_person_is_refused()
    {
        Shop? shop = null;
        var model = new FakeAiModel((_, m) => Script(m, text => text switch
        {
            "question 0" => FakeAiModel.Uses(FakeAiModel.Use("a", "check_slots", new { service_id = shop!.ServiceId, date = "2030-03-05", staff_id = shop.StaffIds[0] })),
            "question 1" => FakeAiModel.Uses(FakeAiModel.Use("b", "check_slots", new { service_id = shop!.ServiceId, date = "2030-03-05", staff_id = shop.StaffIds[1] })),
            "question 2" => FakeAiModel.Uses(FakeAiModel.Use("c", "check_slots", new { service_id = shop!.ServiceId, date = "2030-03-05" })),
            "question 3" => FakeAiModel.Uses(FakeAiModel.Use("d", "check_slots", new { service_id = shop!.ServiceId, date = "2030-03-05", staff_id = "not-a-guid" })),
            "question 4" => FakeAiModel.Uses(FakeAiModel.Use("e", "check_slots", new { service_id = shop!.ServiceId, date = "2030-03-05", staff_id = Guid.NewGuid() })),
            _ => FakeAiModel.Reply("done"),
        }));
        var c = await SetupAsync(model);
        var (s, slug) = await ShopAsync(c);
        shop = s;
        // Staff 1 is booked at 09:00 on that day; Staff 2 is free.
        var bookings = new BookingService(c.Db, c.Clock);
        Assert.True((await bookings.CreateAsync(Seed.Request(s, Seed.Manila(5, 9), staffId: s.StaffIds[0]))).IsSuccess);

        // Each call is one model step, so each customer message advances one scripted tool call.
        var results = new List<AiBlock>();
        for (var i = 0; i < 5; i++)
        {
            Assert.True((await SendAsync(c, slug, $"question {i}")).IsSuccess);
            results.Add(model.LastToolResult());
        }

        var one = Times(results[0]);
        Assert.DoesNotContain("09:00", one);                    // Staff 1 is taken at 9
        Assert.Contains("09:30", one);
        var two = Times(results[1]);
        Assert.Contains("09:00", two);                          // Staff 2 is free at 9
        Assert.Contains("09:00", Times(results[2]));            // anyone: someone is free at 9
        Assert.True(results[3].IsError);
        Assert.Contains("not one of the listed people", results[3].Text);
        Assert.True(results[4].IsError);                        // a person of another shop or a made-up id
    }

    [Fact]
    public async Task A_person_who_does_not_do_the_service_is_refused_and_a_booking_with_a_person_books_that_person()
    {
        Shop? shop = null;
        Guid outsider = Guid.Empty;
        var model = new FakeAiModel((_, m) => Script(m, text => text switch
        {
            "can Does Nothing cut my hair?" => FakeAiModel.Uses(FakeAiModel.Use("a", "check_slots", new { service_id = shop!.ServiceId, date = "2030-03-05", staff_id = outsider })),
            "book me with Staff 2 at 10" => FakeAiModel.Uses(FakeAiModel.Use("b", "create_booking", new
            {
                service_id = shop!.ServiceId, date = "2030-03-05", time = "10:00", customer_name = "Mark", phone = "09171234567", staff_id = shop.StaffIds[1],
            })),
            _ => FakeAiModel.Reply("done"),
        }));
        var c = await SetupAsync(model);
        var (s, slug) = await ShopAsync(c);
        shop = s;
        await using (var ctx = c.Db.CreateDbContext())
        {
            var other = new Staff { BusinessId = s.BusinessId, Name = "Does Nothing", SortOrder = 9 }; // no StaffService row: does not do the haircut
            ctx.Add(other);
            await ctx.SaveChangesAsync();
            outsider = other.Id;
        }

        Assert.True((await SendAsync(c, slug, "can Does Nothing cut my hair?")).IsSuccess);
        Assert.True(model.LastToolResult().IsError);
        Assert.Contains("does not do that service", model.LastToolResult().Text);

        Assert.True((await SendAsync(c, slug, "book me with Staff 2 at 10")).IsSuccess);
        var made = model.LastToolResult();
        Assert.False(made.IsError);
        await using var check = c.Db.CreateDbContext();
        var row = await check.Bookings.AsNoTracking().SingleAsync(b => b.BusinessId == s.BusinessId);
        Assert.Equal(s.StaffIds[1], row.StaffId);               // the person the customer asked for, not whoever is first
    }
}
