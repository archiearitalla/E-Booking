using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using VeskaFlow.Web;
using VeskaFlow.Web.Data;
using VeskaFlow.Web.Services;

namespace VeskaFlow.Web.Tests;

/// <summary>Per-shop AI limits (per day, per month, tokens), the usage and cost summary for the platform owner, and who may see it.</summary>
[Collection("http")]
public class AiLimitTests
{
    private static IConfiguration Config(Dictionary<string, string?>? extra = null) =>
        new ConfigurationBuilder().AddInMemoryCollection(extra ?? new()).Build();

    private sealed record Ctx(TestDbFactory Db, TestClock Clock, AiAccess Access, AiAdminService Admin, Shop Shop);

    private static async Task<Ctx> SetupAsync(Dictionary<string, string?>? config = null)
    {
        var db = new TestDbFactory(await TestDb.ConnectionStringAsync());
        var clock = new TestClock(Seed.BaseNow);
        var cfg = Config(config);
        return new Ctx(db, clock, new AiAccess(db, clock, cfg, null!), new AiAdminService(db, clock, cfg), await Seed.CreateShopAsync());
    }

    [Fact]
    public async Task A_shop_can_have_its_own_limit_per_day_and_per_month_and_a_refusal_counts_nothing()
    {
        var c = await SetupAsync(new() { ["AiReceptionist:MonthlyConversations"] = "5" });
        var id = c.Shop.BusinessId;

        Assert.True((await c.Admin.SetLimitsAsync(id, 2, null, null)).IsSuccess);                       // 2 a day, the default 5 a month
        Assert.True(await c.Access.TryStartConversationAsync(id));
        Assert.True(await c.Access.TryStartConversationAsync(id));
        Assert.False(await c.Access.TryStartConversationAsync(id));                                     // the day is used up...
        Assert.Equal(2, (await c.Access.UsageAsync(id)).Conversations);                                 // ...and the refusal did not use a month slot

        c.Clock.Now = c.Clock.Now.AddDays(1);                                                           // tomorrow
        Assert.True(await c.Access.TryStartConversationAsync(id));

        Assert.True((await c.Admin.SetLimitsAsync(id, null, 3, null)).IsSuccess);                       // 3 a month now, no daily limit
        Assert.False(await c.Access.TryStartConversationAsync(id));                                     // 3 are already used
        Assert.Equal(3, (await c.Access.UsageAsync(id)).Limit);

        Assert.True((await c.Admin.SetLimitsAsync(id, null, 0, null)).IsSuccess);                       // 0 stops it
        Assert.False(await c.Access.TryStartConversationAsync(id));
        Assert.True((await c.Admin.SetLimitsAsync(id, null, null, null)).IsSuccess);                    // back to the default (5)
        Assert.True(await c.Access.TryStartConversationAsync(id));

        var other = await Seed.CreateShopAsync();                                                       // another shop is untouched by these limits
        Assert.True(await c.Access.TryStartConversationAsync(other.BusinessId));
    }

    [Fact]
    public async Task A_token_limit_stops_new_conversations_once_the_month_has_used_that_many_tokens()
    {
        var c = await SetupAsync();
        var id = c.Shop.BusinessId;
        Assert.True((await c.Admin.SetLimitsAsync(id, null, null, 1000)).IsSuccess);
        Assert.True(await c.Access.TryStartConversationAsync(id));
        await c.Access.AddTokensAsync(id, 600, 399);
        Assert.True(await c.Access.TryStartConversationAsync(id));                                      // 999 of 1000
        await c.Access.AddTokensAsync(id, 1, 0);
        Assert.False(await c.Access.TryStartConversationAsync(id));                                     // 1000 reached
    }

    [Fact]
    public async Task Limits_are_range_checked_and_an_unknown_shop_is_not_found()
    {
        var c = await SetupAsync();
        Assert.Equal(FailureKind.Validation, (await c.Admin.SetLimitsAsync(c.Shop.BusinessId, -1, null, null)).Failure);
        Assert.Equal(FailureKind.Validation, (await c.Admin.SetLimitsAsync(c.Shop.BusinessId, null, AiAdminService.MaxMonthly + 1, null)).Failure);
        Assert.Equal(FailureKind.Validation, (await c.Admin.SetLimitsAsync(c.Shop.BusinessId, null, null, AiAdminService.MaxTokens + 1)).Failure);
        Assert.Equal(FailureKind.NotFound, (await c.Admin.SetLimitsAsync(Guid.NewGuid(), 1, 1, 1)).Failure);
        var dbCheck = await Assert.ThrowsAnyAsync<Exception>(async () =>
        {
            await using var ctx = c.Db.CreateDbContext();
            await ctx.Database.ExecuteSqlInterpolatedAsync($"UPDATE business SET ai_daily_limit = -5 WHERE id = {c.Shop.BusinessId}");
        });
        Assert.Contains("business_ai_limits", dbCheck.ToString());
    }

    [Fact]
    public async Task The_summary_lists_each_shop_with_its_numbers_cost_and_limits_and_can_be_searched()
    {
        var c = await SetupAsync(new() { ["AiReceptionist:InputUsdPerMillionTokens"] = "1", ["AiReceptionist:OutputUsdPerMillionTokens"] = "5", ["AiReceptionist:UsdToPhp"] = "60" });
        var quiet = await Seed.CreateShopAsync();
        await using (var ctx = c.Db.CreateDbContext())
            await ctx.Businesses.Where(b => b.Id == c.Shop.BusinessId).ExecuteUpdateAsync(u => u.SetProperty(b => b.AiAddonUntilUtc, Seed.BaseNow.UtcDateTime.AddDays(30)));
        Assert.True(await c.Access.TryStartConversationAsync(c.Shop.BusinessId));
        await c.Access.AddTokensAsync(c.Shop.BusinessId, 2_000_000, 400_000);                           // 2.00 + 2.00 = 4.00 USD = 240 PHP

        var report = await c.Admin.ReportAsync(c.Admin.CurrentMonth, null);
        var row = report.Shops.Single(s => s.BusinessId == c.Shop.BusinessId);
        Assert.Equal((1, 1, 2_400_000L, 4m, 240m, true), (row.Conversations, row.ConversationsToday, row.Tokens, row.CostUsd, row.CostPhp, row.AddonActive));
        Assert.DoesNotContain(report.Shops, s => s.BusinessId == quiet.BusinessId);                      // no add-on and no use: not listed
        Assert.Equal(report.Shops.Sum(s => s.CostPhp), report.TotalCostPhp);

        Assert.Single((await c.Admin.ReportAsync(c.Admin.CurrentMonth, row.Slug.ToUpperInvariant())).Shops);
        Assert.Empty((await c.Admin.ReportAsync(c.Admin.CurrentMonth, "no-such-shop-zzz")).Shops);
        Assert.DoesNotContain((await c.Admin.ReportAsync(c.Admin.CurrentMonth.AddMonths(-1), row.Slug)).Shops, s => s.Conversations > 0); // last month: nothing used
    }

    [Fact]
    public async Task Only_the_platform_owner_sees_the_AI_summary_and_can_set_a_limit()
    {
        var bossEmail = $"boss{Guid.NewGuid():N}@example.com";
        var (factory, _) = await HttpApp.StartAsync(platformAdminEmails: bossEmail);
        using var _f = factory;
        var boss = HttpApp.NewClient(factory);
        await HttpApp.PostFormAsync(boss, "/Register", Form(bossEmail, "Boss Shop"));
        var tenant = HttpApp.NewClient(factory);
        await HttpApp.PostFormAsync(tenant, "/Register", Form($"shop{Guid.NewGuid():N}@example.com", "Tenant Shop"));

        var page = WebUtility.HtmlDecode(await boss.GetStringAsync("/Platform/Ai"));
        Assert.Contains(Text.AiAdminTitle, page);
        Assert.Contains("/Platform/Ai", WebUtility.HtmlDecode(await boss.GetStringAsync("/Platform")));
        Assert.Equal(HttpStatusCode.NotFound, (await tenant.GetAsync("/Platform/Ai")).StatusCode);

        var shopId = await ShopIdAsync(factory, "Tenant Shop");
        var denied = await HttpApp.PostFormAsync(tenant, "/Platform/Ai?handler=Limits", new() { ["id"] = shopId.ToString(), ["monthly"] = "1" }, tokenPage: "/Admin");
        Assert.Equal(HttpStatusCode.NotFound, denied.StatusCode);

        await using (var ctx = new TestDbFactory(await TestDb.ConnectionStringAsync()).CreateDbContext())
            await ctx.Businesses.Where(b => b.Id == shopId).ExecuteUpdateAsync(u => u.SetProperty(b => b.AiAddonUntilUtc, DateTime.UtcNow.AddDays(30)));
        var saved = await HttpApp.PostFormAsync(boss, "/Platform/Ai?handler=Limits", new() { ["id"] = shopId.ToString(), ["daily"] = "10", ["monthly"] = "100", ["tokens"] = "1,000,000" }, tokenPage: "/Platform/Ai");
        Assert.Equal(HttpStatusCode.Redirect, saved.StatusCode);
        await using var check = new TestDbFactory(await TestDb.ConnectionStringAsync()).CreateDbContext();
        var b = await check.Businesses.SingleAsync(x => x.Id == shopId);
        Assert.Equal((10, 100, 1_000_000L), (b.AiDailyLimit, b.AiMonthlyLimit, b.AiTokenLimit));

        var bad = await HttpApp.PostFormAsync(boss, "/Platform/Ai?handler=Limits", new() { ["id"] = shopId.ToString(), ["daily"] = "many" }, tokenPage: "/Platform/Ai");
        Assert.Equal(HttpStatusCode.Redirect, bad.StatusCode);
        Assert.Contains(Text.AiLimitInvalid, WebUtility.HtmlDecode(await boss.GetStringAsync("/Platform/Ai")));
        Assert.Equal(10, (await check.Businesses.AsNoTracking().SingleAsync(x => x.Id == shopId)).AiDailyLimit);   // nothing changed

        Assert.Contains("Tenant Shop", WebUtility.HtmlDecode(await boss.GetStringAsync("/Platform/Ai?q=tenant")));
    }

    [Fact]
    public async Task The_platform_shop_list_is_one_line_per_shop_with_a_search_and_suggestions()
    {
        var bossEmail = $"boss{Guid.NewGuid():N}@example.com";
        var (factory, _) = await HttpApp.StartAsync(platformAdminEmails: bossEmail);
        using var _f = factory;
        var boss = HttpApp.NewClient(factory);
        await HttpApp.PostFormAsync(boss, "/Register", Form(bossEmail, "Boss Shop"));
        var tenantEmail = $"shop{Guid.NewGuid():N}@example.com";
        await HttpApp.PostFormAsync(HttpApp.NewClient(factory), "/Register", Form(tenantEmail, "Zebra Salon Unique"));

        var page = WebUtility.HtmlDecode(await boss.GetStringAsync("/Platform"));
        Assert.Contains("class=\"shop-sum\"", page);                                      // a summary line per shop
        Assert.Contains("<datalist id=\"shop-suggestions\">", page);
        Assert.Contains("value=\"Zebra Salon Unique\"", page);                            // suggested while typing

        var found = WebUtility.HtmlDecode(await boss.GetStringAsync("/Platform?q=zebra"));
        Assert.Contains("1 shop", found);
        Assert.Contains(tenantEmail, found);
        Assert.Contains("value=\"Boss Shop\"", found);                                    // suggestions still list every shop
        Assert.DoesNotContain("class=\"shop-name\">Boss Shop", found);                    // but the list is narrowed
        Assert.Contains(Text.PlatformEmpty, WebUtility.HtmlDecode(await boss.GetStringAsync("/Platform?q=no-such-shop-zzz")));
    }

    private static async Task<Guid> ShopIdAsync(AppFactory factory, string name)
    {
        await using var ctx = new TestDbFactory(await TestDb.ConnectionStringAsync()).CreateDbContext();
        return await ctx.Businesses.Where(b => b.Name == name).OrderByDescending(b => b.CreatedAtUtc).Select(b => b.Id).FirstAsync();
    }

    private static Dictionary<string, string> Form(string email, string shop) => new()
    {
        ["Input.BusinessName"] = shop, ["Input.OwnerName"] = "Owner", ["Input.Email"] = email, ["Input.Password"] = "correct-horse-battery", ["Input.Accept"] = "true", ["Input.Industry"] = "barbershop",
    };
}
