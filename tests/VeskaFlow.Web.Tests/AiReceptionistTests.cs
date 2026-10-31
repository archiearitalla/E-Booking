using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using VeskaFlow.Core.Domain;
using VeskaFlow.Web.Data;
using VeskaFlow.Web.Services;

namespace VeskaFlow.Web.Tests;

/// <summary>A scripted model: the test decides what each step answers and reads what the service sent.</summary>
public sealed class FakeAiModel(Func<int, IReadOnlyList<AiMessage>, AiStep> script, bool configured = true) : IAiModel
{
    private int calls;
    public int Calls => Volatile.Read(ref calls);
    public List<string> Systems { get; } = [];
    public List<IReadOnlyList<AiMessage>> Conversations { get; } = [];
    public bool IsConfigured => configured;

    public Task<AiStep> NextAsync(string system, IReadOnlyList<AiMessage> messages, IReadOnlyList<AiTool> tools, CancellationToken ct = default)
    {
        var n = Interlocked.Increment(ref calls);
        lock (Systems) { Systems.Add(system); Conversations.Add(messages.ToList()); }
        return Task.FromResult(script(n, messages));
    }

    public static AiStep Reply(string text) => new([AiBlock.OfText(text)], "end_turn", 100, 20);
    public static AiBlock Use(string id, string tool, object args) =>
        new(AiBlockKind.ToolUse, Id: id, Name: tool, Json: JsonSerializer.Serialize(args));
    public static AiStep Uses(params AiBlock[] blocks) => new(blocks, "tool_use", 100, 20);

    /// <summary>The result text of the last tool result the service put in front of the model.</summary>
    public AiBlock LastToolResult() =>
        Conversations[^1][^1].Blocks.Last(b => b.Kind == AiBlockKind.ToolResult);
}

/// <summary>The AI receptionist: paid add-on, tools that use the real booking rules, limits and tenant isolation.</summary>
[Collection("http")]
public class AiReceptionistTests
{
    private sealed record Ctx(TestDbFactory Db, TestClock Clock, AiAccess Access, AiReceptionistService Chat, FakeAiModel Model);

    private static async Task<Ctx> SetupAsync(FakeAiModel model, int monthlyLimit = 300)
    {
        var db = new TestDbFactory(await TestDb.ConnectionStringAsync());
        var clock = new TestClock(Seed.BaseNow);
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            { ["AiReceptionist:MonthlyConversations"] = monthlyLimit.ToString() }).Build();
        var access = new AiAccess(db, clock, config, model);
        var chat = new AiReceptionistService(new ShopCatalogService(db, clock), new BookingService(db, clock), access, model, db, clock,
            NullLogger<AiReceptionistService>.Instance);
        return new Ctx(db, clock, access, chat, model);
    }

    private static async Task<(Shop Shop, string Slug)> ShopWithAddonAsync(Ctx c, bool addon = true)
    {
        var shop = await Seed.CreateShopAsync();
        await using var ctx = c.Db.CreateDbContext();
        await ctx.Businesses.Where(b => b.Id == shop.BusinessId).ExecuteUpdateAsync(u =>
            u.SetProperty(b => b.AiAddonUntilUtc, addon ? c.Clock.GetUtcNow().UtcDateTime.AddDays(30) : (DateTime?)null));
        return (shop, await ctx.Businesses.Where(b => b.Id == shop.BusinessId).Select(b => b.Slug).SingleAsync());
    }

    private static Task<ServiceResult<AiChatReply>> SendAsync(Ctx c, string slug, string text, Guid? chatId = null) =>
        c.Chat.SendAsync(slug, chatId, text, "https://flow.example.com");

    private static object Booking(Shop shop, string phone = "09171234567", string time = "10:00") => new
    {
        service_id = shop.ServiceId, date = "2030-03-05", time, customer_name = "Mark", phone,
    };

    // ---------------------------------------------------------------- the add-on

    [Fact]
    public async Task Without_the_add_on_an_expired_add_on_or_no_model_key_the_chat_is_not_found_and_nothing_is_spent()
    {
        var model = new FakeAiModel((_, _) => FakeAiModel.Reply("hi"));
        var c = await SetupAsync(model);
        var (_, none) = await ShopWithAddonAsync(c, addon: false);
        var (expired, expiredSlug) = await ShopWithAddonAsync(c);
        c.Clock.Now = c.Clock.Now.AddDays(31); // the add-on of the second shop ended yesterday
        var unconfigured = await SetupAsync(new FakeAiModel((_, _) => FakeAiModel.Reply("hi"), configured: false));
        var (_, unconfiguredSlug) = await ShopWithAddonAsync(unconfigured);

        Assert.Equal(FailureKind.NotFound, (await SendAsync(c, none, "hello")).Failure);
        Assert.Equal(FailureKind.NotFound, (await SendAsync(c, expiredSlug, "hello")).Failure);
        Assert.Equal(FailureKind.NotFound, (await SendAsync(unconfigured, unconfiguredSlug, "hello")).Failure);
        Assert.Equal(FailureKind.NotFound, (await SendAsync(c, "no-such-shop", "hello")).Failure);
        Assert.Equal(0, model.Calls);
        Assert.Equal(0, unconfigured.Model.Calls);
        await using var db = c.Db.CreateDbContext();
        Assert.False(await db.AiUsages.AnyAsync(u => u.BusinessId == expired.BusinessId));
        Assert.False(await db.AiChats.AnyAsync(x => x.BusinessId == expired.BusinessId));
    }

    [Fact]
    public async Task The_platform_owner_extends_the_add_on_from_the_later_of_today_and_its_end()
    {
        var c = await SetupAsync(new FakeAiModel((_, _) => FakeAiModel.Reply("hi")));
        var shop = await Seed.CreateShopAsync();
        var accounts = new AccountControlService(c.Db, c.Clock, new ConfigurationBuilder().Build(), new NoEmailSender());
        var now = c.Clock.GetUtcNow().UtcDateTime;

        Assert.False(await c.Access.IsActiveAsync(shop.BusinessId));
        Assert.Equal(now.AddDays(30), (await accounts.ExtendAiAddonAsync(shop.BusinessId, 30)).Value, TimeSpan.FromSeconds(1));
        Assert.True(await c.Access.IsActiveAsync(shop.BusinessId));
        Assert.Equal(now.AddDays(37), (await accounts.ExtendAiAddonAsync(shop.BusinessId, 7)).Value, TimeSpan.FromSeconds(1));
        c.Clock.Now = c.Clock.Now.AddDays(100);
        Assert.False(await c.Access.IsActiveAsync(shop.BusinessId)); // lapsed
        Assert.Equal(c.Clock.GetUtcNow().UtcDateTime.AddDays(7), (await accounts.ExtendAiAddonAsync(shop.BusinessId, 7)).Value, TimeSpan.FromSeconds(1));
        Assert.Equal(FailureKind.Validation, (await accounts.ExtendAiAddonAsync(shop.BusinessId, 5)).Failure);
        Assert.Equal(FailureKind.NotFound, (await accounts.ExtendAiAddonAsync(Guid.NewGuid(), 30)).Failure);
    }

    // ---------------------------------------------------------------- tools

    [Fact]
    public async Task A_conversation_lists_the_real_free_times_answers_and_is_counted()
    {
        Shop? shop = null;
        var model = new FakeAiModel((n, _) => n == 1
            ? FakeAiModel.Uses(FakeAiModel.Use("t1", "check_slots", new { service_id = shop!.ServiceId, date = "2030-03-05" }))
            : FakeAiModel.Reply("Free at 9:00 AM, 10:00 AM and more. Which one?"));
        var c = await SetupAsync(model);
        var (s, slug) = await ShopWithAddonAsync(c);
        shop = s;

        var first = await SendAsync(c, slug, "may slot ba bukas, gupit");
        Assert.True(first.IsSuccess);
        Assert.Equal("Free at 9:00 AM, 10:00 AM and more. Which one?", first.Value!.Text);

        var result = model.LastToolResult();
        Assert.False(result.IsError);
        using var json = JsonDocument.Parse(result.Text);
        var times = json.RootElement.GetProperty("times").EnumerateArray().Select(e => e.GetString()).ToList();
        Assert.Contains("09:00", times);
        Assert.Contains("17:30", times);              // the last 30-minute slot before 18:00
        Assert.DoesNotContain("08:30", times);        // outside the opening hours
        Assert.DoesNotContain("18:00", times);

        Assert.Contains("Haircut", model.Systems[0]);
        Assert.Contains(s.ServiceId.ToString(), model.Systems[0]);
        Assert.Contains("Monday, March 4, 2030", model.Systems[0]);

        await using var db = c.Db.CreateDbContext();
        var usage = await db.AiUsages.SingleAsync(u => u.BusinessId == s.BusinessId);
        Assert.Equal(1, usage.Conversations);
        Assert.Equal(200, usage.InputTokens);   // two model calls of 100
        Assert.Equal(40, usage.OutputTokens);
        var chat = await db.AiChats.SingleAsync(x => x.BusinessId == s.BusinessId);
        Assert.Equal(1, chat.Turns);
        Assert.Equal(first.Value.ChatId, chat.Id);
        Assert.DoesNotContain("check_slots", chat.Transcript);   // only what was said is kept, not tool calls
        Assert.Contains("may slot ba bukas", chat.Transcript);

        // The second message continues the same conversation: it is not counted again and sees the earlier words.
        var second = await SendAsync(c, slug, "10", first.Value.ChatId);
        Assert.True(second.IsSuccess);
        Assert.Equal(1, (await db.AiUsages.AsNoTracking().SingleAsync(u => u.BusinessId == s.BusinessId)).Conversations);
        var seen = model.Conversations[^1];
        Assert.Equal(["user", "assistant", "user"], seen.Select(m => m.Role));
        Assert.Equal("10", seen[^1].Blocks[0].Text);
    }

    [Fact]
    public async Task Booking_through_the_chat_makes_one_real_booking_even_when_the_call_is_repeated()
    {
        Shop? shop = null;
        var model = new FakeAiModel((n, _) => n == 1
            ? FakeAiModel.Uses(FakeAiModel.Use("a", "create_booking", Booking(shop!)), FakeAiModel.Use("b", "create_booking", Booking(shop!)))
            : FakeAiModel.Reply("Booked!"));
        var c = await SetupAsync(model);
        var (s, slug) = await ShopWithAddonAsync(c);
        shop = s;

        var reply = await SendAsync(c, slug, "Mark 09171234567, 10am please");

        await using var db = c.Db.CreateDbContext();
        var booking = await db.Bookings.SingleAsync(b => b.BusinessId == s.BusinessId);
        // The customer's own link is added by the server, once, word for word, and never sent to the AI service.
        var link = $"https://flow.example.com/booking/{booking.ManageToken}";
        Assert.Equal($"Booked!\n\n{Text.AiChatManageLink} {link}", reply.Value!.Text);
        Assert.DoesNotContain(booking.ManageToken, string.Join(" ", model.Systems));
        Assert.DoesNotContain(booking.ManageToken, JsonSerializer.Serialize(model.Conversations.Select(m => m.SelectMany(x => x.Blocks).Select(b => b.Text + b.Json))));
        await using var again = c.Db.CreateDbContext();
        Assert.Contains(link, (await again.AiChats.SingleAsync(x => x.BusinessId == s.BusinessId)).Transcript.Replace("\\u002F", "/")); // the kept chat has it, as the customer saw it
        Assert.Equal(Seed.Manila(5, 10), DateTime.SpecifyKind(booking.StartsAtUtc, DateTimeKind.Utc));
        Assert.Equal(BookingStatus.Confirmed, booking.Status);

        var results = model.Conversations[^1][^1].Blocks.Where(b => b.Kind == AiBlockKind.ToolResult).ToList();
        Assert.Equal(2, results.Count);                       // each tool use got its result, in one message
        Assert.All(results, r => Assert.False(r.IsError));
        Assert.DoesNotContain("/booking/", results[0].Text);
        Assert.Contains("Tuesday, March 5 at 10:00 AM", results[0].Text);

        var audit = await db.AuditLogs.SingleAsync(a => a.BookingId == booking.Id);
        Assert.Equal("Mark (chat assistant)", audit.ActorLabel);
        Assert.Equal("CUSTOMER", audit.ActorType);
    }

    [Fact]
    public async Task A_time_that_was_just_taken_comes_back_to_the_model_as_slot_taken_and_nothing_extra_is_booked()
    {
        Shop? shop = null;
        var model = new FakeAiModel((n, _) => n == 1
            ? FakeAiModel.Uses(FakeAiModel.Use("a", "create_booking", Booking(shop!, "09179999999")))
            : FakeAiModel.Reply("Sorry, that time was just taken."));
        var c = await SetupAsync(model);
        var (s, slug) = await ShopWithAddonAsync(c);
        shop = s;
        var other = await new BookingService(c.Db, c.Clock).CreateAsync(Seed.Request(s, Seed.Manila(5, 10)));
        Assert.True(other.IsSuccess);

        await SendAsync(c, slug, "10am");

        var result = model.LastToolResult();
        Assert.True(result.IsError);
        Assert.StartsWith("slot_taken", result.Text);
        await using var db = c.Db.CreateDbContext();
        Assert.Equal(1, await db.Bookings.CountAsync(b => b.BusinessId == s.BusinessId));
    }

    [Fact]
    public async Task Wrong_arguments_are_explained_to_the_model_and_book_nothing()
    {
        Shop? shop = null;
        AiStep Script(int n, IReadOnlyList<AiMessage> _) => n switch
        {
            1 => FakeAiModel.Uses(
                FakeAiModel.Use("a", "check_slots", new { service_id = "not-a-guid", date = "2030-03-05" }),
                FakeAiModel.Use("b", "check_slots", new { service_id = shop!.ServiceId, date = "05/03/2030" }),
                FakeAiModel.Use("c", "check_slots", new { service_id = shop!.ServiceId, date = "2030-01-01" }),      // in the past
                FakeAiModel.Use("d", "check_slots", new { service_id = shop!.ServiceId, date = "2031-12-01" }),      // beyond the horizon
                FakeAiModel.Use("e", "create_booking", new { service_id = shop!.ServiceId, date = "2030-03-05", time = "10am", customer_name = "Mark", phone = "0917" }),
                FakeAiModel.Use("f", "create_booking", new { service_id = shop!.ServiceId, date = "2030-03-05", time = "10:00", customer_name = "Mark", phone = "abc" }),
                FakeAiModel.Use("g", "no_such_tool", new { })),
            _ => FakeAiModel.Reply("ok"),
        };
        var model = new FakeAiModel(Script);
        var c = await SetupAsync(model);
        var (s, slug) = await ShopWithAddonAsync(c);
        shop = s;

        await SendAsync(c, slug, "test");

        var results = model.Conversations[^1][^1].Blocks.Where(b => b.Kind == AiBlockKind.ToolResult).ToList();
        Assert.Equal(7, results.Count);
        Assert.All(results, r => Assert.True(r.IsError, r.Text));
        await using var db = c.Db.CreateDbContext();
        Assert.False(await db.Bookings.AnyAsync(b => b.BusinessId == s.BusinessId));
    }

    // ---------------------------------------------------------------- tenants

    [Fact]
    public async Task Another_shops_conversation_or_service_cannot_be_reached()
    {
        Shop? shopA = null, shopB = null;
        var model = new FakeAiModel((n, _) => n switch
        {
            // In shop B's chat the model names a service id that belongs to shop A.
            2 => FakeAiModel.Uses(FakeAiModel.Use("x", "create_booking", new
                { service_id = shopA!.ServiceId, date = "2030-03-05", time = "10:00", customer_name = "Mark", phone = "09171234567" })),
            _ => FakeAiModel.Reply("hi"),
        });
        var c = await SetupAsync(model);
        var (a, slugA) = await ShopWithAddonAsync(c);
        shopA = a;
        var (b, slugB) = await ShopWithAddonAsync(c);
        shopB = b;

        var chatA = (await SendAsync(c, slugA, "hello")).Value!.ChatId; // call 1
        Assert.Equal(FailureKind.NotFound, (await SendAsync(c, slugB, "hello", chatA)).Failure); // A's chat id on B's page
        Assert.Equal(FailureKind.NotFound, (await SendAsync(c, slugA, "hello", Guid.NewGuid())).Failure);
        Assert.Equal(1, model.Calls);

        await SendAsync(c, slugB, "book me");   // call 2 asks for A's service
        Assert.True(model.LastToolResult().IsError);
        Assert.Contains("not one of the listed services", model.LastToolResult().Text);
        await using var db = c.Db.CreateDbContext();
        Assert.False(await db.Bookings.AnyAsync(x => x.BusinessId == a.BusinessId || x.BusinessId == shopB.BusinessId));
        Assert.DoesNotContain(a.ServiceId.ToString(), model.Systems[^1]);   // B's prompt never lists A's services
    }

    // ---------------------------------------------------------------- limits and failures

    [Fact]
    public async Task The_monthly_limit_stops_new_conversations_but_not_running_ones()
    {
        var c = await SetupAsync(new FakeAiModel((_, _) => FakeAiModel.Reply("hi")), monthlyLimit: 2);
        var (shop, slug) = await ShopWithAddonAsync(c);

        var one = (await SendAsync(c, slug, "a")).Value!.ChatId;
        Assert.True((await SendAsync(c, slug, "b")).IsSuccess);
        var third = await SendAsync(c, slug, "c");
        Assert.Equal(FailureKind.PolicyViolation, third.Failure);
        Assert.Equal(Text.AiChatUnavailable, third.Message);

        Assert.True((await SendAsync(c, slug, "again", one)).IsSuccess);   // a running conversation goes on
        var usage = await c.Access.UsageAsync(shop.BusinessId);
        Assert.Equal(2, usage.Conversations);
        Assert.Equal(2, usage.Limit);

        c.Clock.Now = new DateTimeOffset(2030, 4, 2, 0, 0, 0, TimeSpan.Zero);   // a new month, a new allowance
        await using (var db = c.Db.CreateDbContext())
            await db.Businesses.Where(b => b.Id == shop.BusinessId).ExecuteUpdateAsync(u => u.SetProperty(b => b.AiAddonUntilUtc, c.Clock.GetUtcNow().UtcDateTime.AddDays(5)));
        Assert.True((await SendAsync(c, slug, "d")).IsSuccess);
    }

    [Fact]
    public async Task Parallel_starts_cannot_pass_the_monthly_limit()
    {
        var c = await SetupAsync(new FakeAiModel((_, _) => FakeAiModel.Reply("hi")), monthlyLimit: 3);
        var (shop, slug) = await ShopWithAddonAsync(c);

        var results = await Task.WhenAll(Enumerable.Range(0, 12).Select(i => SendAsync(c, slug, $"hello {i}")));

        Assert.Equal(3, results.Count(r => r.IsSuccess));
        Assert.Equal(9, results.Count(r => r.Failure == FailureKind.PolicyViolation));
        Assert.Equal(3, (await c.Access.UsageAsync(shop.BusinessId)).Conversations);
        Assert.Equal(3, c.Model.Calls);
    }

    [Fact]
    public async Task Messages_must_be_short_and_a_conversation_has_a_turn_limit()
    {
        var c = await SetupAsync(new FakeAiModel((_, _) => FakeAiModel.Reply("hi")));
        var (_, slug) = await ShopWithAddonAsync(c);

        Assert.Equal(FailureKind.Validation, (await SendAsync(c, slug, "   ")).Failure);
        Assert.Equal(FailureKind.Validation, (await SendAsync(c, slug, new string('x', AiReceptionistService.MaxMessageChars + 1))).Failure);
        Assert.Equal(0, c.Model.Calls);                                   // refused before the model was asked
        Assert.True((await SendAsync(c, slug, new string('x', AiReceptionistService.MaxMessageChars))).IsSuccess);

        var chat = (await SendAsync(c, slug, "hi")).Value!.ChatId;
        for (var i = 2; i <= AiReceptionistService.MaxTurns; i++)
            Assert.True((await SendAsync(c, slug, "more", chat)).IsSuccess);
        var full = await SendAsync(c, slug, "one too many", chat);
        Assert.Equal(FailureKind.PolicyViolation, full.Failure);
        Assert.Equal(Text.AiChatFull, full.Message);
    }

    [Fact]
    public async Task A_failing_model_gives_a_friendly_answer_and_leaves_the_conversation_clean()
    {
        var c = await SetupAsync(new FakeAiModel((_, _) => throw new HttpRequestException("model service down")));
        var (shop, slug) = await ShopWithAddonAsync(c);

        var reply = await SendAsync(c, slug, "hello");

        Assert.True(reply.IsSuccess);
        Assert.Equal(Text.AiChatUnavailable, reply.Value!.Text);
        await using var db = c.Db.CreateDbContext();
        var chat = await db.AiChats.SingleAsync(x => x.BusinessId == shop.BusinessId);
        Assert.Equal(0, chat.Turns);
        Assert.Equal("[]", chat.Transcript);
    }

    [Fact]
    public async Task A_refusal_and_a_model_that_never_stops_calling_tools_both_end_politely()
    {
        var refusing = await SetupAsync(new FakeAiModel((_, _) => new AiStep([], "refusal", 10, 0)));
        var (_, refusingSlug) = await ShopWithAddonAsync(refusing);
        Assert.Equal(Text.AiChatCannotHelp, (await SendAsync(refusing, refusingSlug, "hello")).Value!.Text);

        Shop? shop = null;
        var looping = await SetupAsync(new FakeAiModel((n, _) =>
            FakeAiModel.Uses(FakeAiModel.Use("t" + n, "check_slots", new { service_id = shop!.ServiceId, date = "2030-03-05" }))));
        var (s, slug) = await ShopWithAddonAsync(looping);
        shop = s;
        Assert.Equal(Text.AiChatCannotHelp, (await SendAsync(looping, slug, "hello")).Value!.Text);
        Assert.Equal(AiReceptionistService.MaxSteps, looping.Model.Calls);
    }

    [Fact]
    public async Task Old_conversations_of_a_shop_are_removed_when_a_new_one_starts()
    {
        var c = await SetupAsync(new FakeAiModel((_, _) => FakeAiModel.Reply("hi")));
        var (shop, slug) = await ShopWithAddonAsync(c);
        var (otherShop, _) = await ShopWithAddonAsync(c);
        var now = c.Clock.GetUtcNow().UtcDateTime;
        Guid oldMine = Guid.NewGuid(), recentMine = Guid.NewGuid(), oldOther = Guid.NewGuid();
        await using (var db = c.Db.CreateDbContext())
        {
            db.AiChats.AddRange(
                new AiChat { Id = oldMine, BusinessId = shop.BusinessId, CreatedAtUtc = now.AddDays(-31), UpdatedAtUtc = now.AddDays(-31) },
                new AiChat { Id = recentMine, BusinessId = shop.BusinessId, CreatedAtUtc = now.AddDays(-5), UpdatedAtUtc = now.AddDays(-5) },
                new AiChat { Id = oldOther, BusinessId = otherShop.BusinessId, CreatedAtUtc = now.AddDays(-40), UpdatedAtUtc = now.AddDays(-40) });
            await db.SaveChangesAsync();
        }

        await SendAsync(c, slug, "hello");

        await using var check = c.Db.CreateDbContext();
        Assert.False(await check.AiChats.AnyAsync(x => x.Id == oldMine));
        Assert.True(await check.AiChats.AnyAsync(x => x.Id == recentMine));
        Assert.True(await check.AiChats.AnyAsync(x => x.Id == oldOther));   // another shop's conversations are not this shop's to remove
    }

    // ---------------------------------------------------------------- privacy: a customer's chats

    private static string Chat(string customerSays) =>
        "[{\"R\":\"u\",\"T\":\"" + customerSays + "\"},{\"R\":\"a\",\"T\":\"Noted.\"}]";

    [Fact]
    public async Task A_customers_chats_are_found_by_the_number_typed_in_them_and_exported_and_deleted_with_the_customer()
    {
        var c = await SetupAsync(new FakeAiModel((_, _) => FakeAiModel.Reply("hi")));
        var shop = await Seed.CreateShopAsync();
        var otherShop = await Seed.CreateShopAsync();
        var bookings = new BookingService(c.Db, c.Clock);
        Assert.True((await bookings.CreateAsync(Seed.Request(shop, Seed.Manila(5, 10), phone: "09171234567"))).IsSuccess);
        Assert.True((await bookings.CreateAsync(Seed.Request(otherShop, Seed.Manila(5, 10), phone: "09171234567"))).IsSuccess);
        var now = c.Clock.GetUtcNow().UtcDateTime;
        Guid international = Guid.NewGuid(), dashed = Guid.NewGuid(), stranger = Guid.NewGuid(), otherShopsChat = Guid.NewGuid();
        await using (var db = c.Db.CreateDbContext())
        {
            db.AiChats.AddRange(
                new AiChat { Id = international, BusinessId = shop.BusinessId, CreatedAtUtc = now, UpdatedAtUtc = now, Transcript = Chat("Mark, +63 917 123 4567") },
                new AiChat { Id = dashed, BusinessId = shop.BusinessId, CreatedAtUtc = now.AddMinutes(1), UpdatedAtUtc = now, Transcript = Chat("number is 0917-123-4567") },
                new AiChat { Id = stranger, BusinessId = shop.BusinessId, CreatedAtUtc = now, UpdatedAtUtc = now, Transcript = Chat("09189998888") },
                new AiChat { Id = otherShopsChat, BusinessId = otherShop.BusinessId, CreatedAtUtc = now, UpdatedAtUtc = now, Transcript = Chat("09171234567") });
            await db.SaveChangesAsync();
        }
        var data = new CustomerDataService(c.Db);
        Guid customerId;
        await using (var db = c.Db.CreateDbContext())
            customerId = await db.Customers.Where(x => x.BusinessId == shop.BusinessId).Select(x => x.Id).SingleAsync();

        using var export = JsonDocument.Parse((await data.ExportJsonAsync(shop.BusinessId, customerId))!);
        var chats = export.RootElement.GetProperty("assistantChats").EnumerateArray().ToList();
        Assert.Equal(2, chats.Count);                                                   // both formats found, in the order started
        Assert.Contains("+63 917 123 4567", chats[0].GetProperty("messages")[0].GetProperty("T").GetString());
        Assert.DoesNotContain("09189998888", export.RootElement.GetRawText());          // someone else's chat is not in it

        Assert.True((await data.DeleteAsync(shop.BusinessId, customerId, "Juan Dela Cruz")).IsSuccess);
        await using var check = c.Db.CreateDbContext();
        Assert.False(await check.AiChats.AnyAsync(x => x.Id == international || x.Id == dashed));
        Assert.True(await check.AiChats.AnyAsync(x => x.Id == stranger));               // a different number stays
        Assert.True(await check.AiChats.AnyAsync(x => x.Id == otherShopsChat));         // another shop's chats are not this shop's to erase
    }

    [Fact]
    public async Task A_walk_in_without_a_number_matches_no_chat_and_a_wrong_name_deletes_nothing()
    {
        var c = await SetupAsync(new FakeAiModel((_, _) => FakeAiModel.Reply("hi")));
        var shop = await Seed.CreateShopAsync();
        var now = c.Clock.GetUtcNow().UtcDateTime;
        var customer = new Customer { BusinessId = shop.BusinessId, Name = "Walk In", Phone = "walkin-1234567890123" };
        var chat = new AiChat { BusinessId = shop.BusinessId, CreatedAtUtc = now, UpdatedAtUtc = now, Transcript = Chat("1234567890123") };
        await using (var db = c.Db.CreateDbContext())
        {
            db.AddRange(customer, chat);
            await db.SaveChangesAsync();
        }
        var data = new CustomerDataService(c.Db);

        using var export = JsonDocument.Parse((await data.ExportJsonAsync(shop.BusinessId, customer.Id))!);
        Assert.Empty(export.RootElement.GetProperty("assistantChats").EnumerateArray());
        Assert.Equal(FailureKind.Validation, (await data.DeleteAsync(shop.BusinessId, customer.Id, "wrong name")).Failure);
        Assert.True((await data.DeleteAsync(shop.BusinessId, customer.Id, "Walk In")).IsSuccess);
        await using var check = c.Db.CreateDbContext();
        Assert.True(await check.AiChats.AnyAsync(x => x.Id == chat.Id));                // nothing safe to match, so nothing is guessed
    }

    // ---------------------------------------------------------------- the web layer

    private static async Task<(AppFactory Root, HttpClient Client, FakeAiModel Model, Shop Shop, string Slug)> WebAsync(bool addon, int chatPerMinute = 1000)
    {
        var (root, _) = await HttpApp.StartAsync(chatPerMinute: chatPerMinute);
        var model = new FakeAiModel((_, _) => FakeAiModel.Reply("Hello from the assistant"));
        var client = root.WithWebHostBuilder(b => b.ConfigureServices(s => s.AddSingleton<IAiModel>(model)))
            .CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = true });
        var shop = await Seed.CreateShopAsync();
        var db = new TestDbFactory(await TestDb.ConnectionStringAsync());
        await using var ctx = db.CreateDbContext();
        await ctx.Businesses.Where(b => b.Id == shop.BusinessId).ExecuteUpdateAsync(u =>
            u.SetProperty(b => b.AiAddonUntilUtc, addon ? DateTime.UtcNow.AddDays(30) : (DateTime?)null));
        var slug = await ctx.Businesses.Where(b => b.Id == shop.BusinessId).Select(b => b.Slug).SingleAsync();
        return (root, client, model, shop, slug);
    }

    private static async Task<HttpResponseMessage> PostChatAsync(HttpClient client, string slug, string message, Guid? chatId = null, string? token = null)
    {
        token ??= await HttpApp.TokenFromAsync(client, $"/b/{slug}"); // the chat form's own token (the page must show the chat)
        return await client.PostAsync($"/b/{slug}/chat", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["message"] = message, ["chatId"] = chatId?.ToString() ?? "", ["__RequestVerificationToken"] = token,
        }));
    }

    [Fact]
    public async Task The_booking_page_offers_the_chat_only_while_the_add_on_is_on()
    {
        var (rootOn, on, _, _, slugOn) = await WebAsync(addon: true);
        using var _a = rootOn;
        var (rootOff, off, _, _, slugOff) = await WebAsync(addon: false);
        using var _b = rootOff;

        var withChat = await on.GetStringAsync($"/b/{slugOn}");
        Assert.Contains("id=\"chat\"", withChat);
        Assert.Contains("/js/chat.js", withChat);
        Assert.Contains(Text.AiChatNotice, WebUtility.HtmlDecode(withChat));            // customers are told it is automated, with a link
        Assert.Contains("href=\"/Privacy\"", withChat);
        var without = await off.GetStringAsync($"/b/{slugOff}");
        Assert.DoesNotContain("id=\"chat\"", without);
        Assert.DoesNotContain("chat.js", without);
    }

    [Fact]
    public async Task A_chat_message_over_http_answers_in_json_continues_the_conversation_and_hides_other_shops()
    {
        var (root, client, model, _, slug) = await WebAsync(addon: true);
        using var _r = root;
        var token = await HttpApp.TokenFromAsync(client, $"/b/{slug}");

        var first = await PostChatAsync(client, slug, "hello", null, token);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal("no-store", first.Headers.CacheControl?.ToString());
        using var doc = JsonDocument.Parse(await first.Content.ReadAsStringAsync());
        Assert.Equal("Hello from the assistant", doc.RootElement.GetProperty("reply").GetString());
        var chatId = doc.RootElement.GetProperty("chatId").GetGuid();

        Assert.Equal(HttpStatusCode.OK, (await PostChatAsync(client, slug, "and then?", chatId, token)).StatusCode);
        Assert.Equal(3, model.Conversations[^1].Count);
        Assert.Equal(HttpStatusCode.NotFound, (await PostChatAsync(client, slug, "hi", Guid.NewGuid(), token)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await PostChatAsync(client, slug, "  ", null, token)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/b/{slug}/chat")).StatusCode);
    }

    [Fact]
    public async Task The_chat_endpoint_needs_the_page_token_and_gives_a_shop_without_the_add_on_the_plain_not_found()
    {
        var (root, client, model, _, slug) = await WebAsync(addon: true);
        using var _r = root;
        var noToken = await client.PostAsync($"/b/{slug}/chat", new FormUrlEncodedContent(new Dictionary<string, string> { ["message"] = "hi" }));
        Assert.Equal(HttpStatusCode.BadRequest, noToken.StatusCode);
        Assert.Equal(0, model.Calls);

        var (rootOff, off, offModel, _, slugOff) = await WebAsync(addon: false);
        using var _o = rootOff;
        var offToken = await HttpApp.TokenFromAsync(off, "/Register");
        Assert.Equal(HttpStatusCode.NotFound, (await PostChatAsync(off, slugOff, "hi", null, offToken)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await PostChatAsync(off, "no-such-shop", "hi", null, offToken)).StatusCode);
        Assert.Equal(0, offModel.Calls);
    }

    private sealed class OnlyGoodCaptcha : ICaptcha
    {
        public bool IsEnabled => true;
        public string SiteKey => "site-key-123";
        public Task<bool> VerifyAsync(string? token, string? clientIp, CancellationToken ct = default) => Task.FromResult(token == "good");
    }

    [Fact]
    public async Task A_new_conversation_needs_the_person_check_when_Turnstile_is_on_and_the_rest_of_it_does_not()
    {
        Environment.SetEnvironmentVariable("Turnstile__SiteKey", "site-key-123");
        Environment.SetEnvironmentVariable("Turnstile__SecretKey", "secret-key");
        try
        {
            var (root, _, _, _, slug) = await WebAsync(addon: true);
            using var _r = root;
            var model = new FakeAiModel((_, _) => FakeAiModel.Reply("Hello from the assistant"));
            var client = root.WithWebHostBuilder(b => b.ConfigureServices(s =>
                {
                    s.AddSingleton<IAiModel>(model);
                    s.AddSingleton<ICaptcha>(new OnlyGoodCaptcha());
                }))
                .CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = true });

            var page = await client.GetStringAsync($"/b/{slug}");
            Assert.Contains("id=\"chat-captcha\"", page);       // the check is inside the chat form
            Assert.Contains("data-sitekey=\"site-key-123\"", page);
            var token = await HttpApp.TokenFromAsync(client, $"/b/{slug}");

            Task<HttpResponseMessage> Post(string message, Guid? chatId, string? answer) =>
                client.PostAsync($"/b/{slug}/chat", new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["message"] = message, ["chatId"] = chatId?.ToString() ?? "", ["__RequestVerificationToken"] = token,
                    ["cf-turnstile-response"] = answer ?? "",
                }));

            // No answer, or a wrong one: refused, nothing counted, the model is never called.
            var none = await Post("hello", null, null);
            Assert.Equal(HttpStatusCode.BadRequest, none.StatusCode);
            Assert.Contains("could not check that you are a person", WebUtility.HtmlDecode(await none.Content.ReadAsStringAsync()));
            Assert.Equal(HttpStatusCode.BadRequest, (await Post("hello", null, "bad")).StatusCode);
            Assert.Equal(0, model.Calls);

            // The first message with a good answer starts the conversation; the next ones in it need no new answer.
            var first = await Post("hello", null, "good");
            Assert.Equal(HttpStatusCode.OK, first.StatusCode);
            using var doc = JsonDocument.Parse(await first.Content.ReadAsStringAsync());
            var chatId = doc.RootElement.GetProperty("chatId").GetGuid();
            Assert.Equal(HttpStatusCode.OK, (await Post("and then?", chatId, null)).StatusCode);
            // A made-up chat id is still not found, with or without an answer.
            Assert.Equal(HttpStatusCode.NotFound, (await Post("hi", Guid.NewGuid(), null)).StatusCode);
        }
        finally
        {
            Environment.SetEnvironmentVariable("Turnstile__SiteKey", null);
            Environment.SetEnvironmentVariable("Turnstile__SecretKey", null);
        }
    }

    [Fact]
    public async Task Chat_messages_are_limited_per_client_and_the_limit_is_a_429()
    {
        var (root, client, model, _, slug) = await WebAsync(addon: true, chatPerMinute: 2);
        using var _r = root;
        var token = await HttpApp.TokenFromAsync(client, $"/b/{slug}");

        Assert.Equal(HttpStatusCode.OK, (await PostChatAsync(client, slug, "1", null, token)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await PostChatAsync(client, slug, "2", null, token)).StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await PostChatAsync(client, slug, "3", null, token)).StatusCode);
        Assert.Equal(2, model.Calls);
    }
}
