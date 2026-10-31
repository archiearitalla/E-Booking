using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using VeskaFlow.Web;
using VeskaFlow.Web.Data;
using VeskaFlow.Web.Services;

namespace VeskaFlow.Web.Tests;

/// <summary>Billing (BUSINESS_RULES 6.y22): prices, "I paid", confirming and rejecting, the receipt, and who sees what.</summary>
[Collection("http")]
public class PaymentTests
{
    private sealed record Ctx(TestDbFactory Db, TestClock Clock, PaymentService Payments, PricingInfo Pricing, Shop Shop);

    private static PricingInfo Pricing(Dictionary<string, string?>? extra = null) => new(new ConfigurationBuilder().AddInMemoryCollection(extra ?? new()).Build());

    private static async Task<Ctx> SetupAsync(DateTime? accessEnds = null, Dictionary<string, string?>? prices = null)
    {
        var db = new TestDbFactory(await TestDb.ConnectionStringAsync());
        var clock = new TestClock(Seed.BaseNow);
        var pricing = Pricing(prices);
        var shop = await Seed.CreateShopAsync();
        await using (var ctx = db.CreateDbContext())
            await ctx.Businesses.Where(b => b.Id == shop.BusinessId).ExecuteUpdateAsync(u => u.SetProperty(b => b.TrialEndsAtUtc, accessEnds ?? Seed.BaseNow.UtcDateTime.AddDays(10)));
        return new Ctx(db, clock, new PaymentService(db, clock, pricing), pricing, shop);
    }

    private static string Ref() => "GC" + Guid.NewGuid().ToString("N")[..10].ToUpperInvariant();

    private static async Task<Business> ShopAsync(Ctx c)
    {
        await using var ctx = c.Db.CreateDbContext();
        return await ctx.Businesses.AsNoTracking().SingleAsync(b => b.Id == c.Shop.BusinessId);
    }

    [Fact]
    public void The_prices_come_from_settings_with_a_yearly_saving_and_a_price_of_zero_is_not_for_sale()
    {
        var p = Pricing();
        Assert.Equal(new[] { "PRO-MONTH", "PRO-YEAR", "AI-MONTH", "EMAIL-MONTH" }, p.Options.Select(o => o.Key));
        Assert.Equal((499m, 4990m), (p.ProMonthly, p.ProYearly));
        Assert.Equal(998m, p.YearlySaving);
        Assert.Equal(365, p.Find("PRO", "YEAR")!.Days);
        Assert.Equal(30, p.Find("PRO", "MONTH")!.Days);
        Assert.Null(p.Find("AI", "YEAR"));

        var changed = Pricing(new() { ["Pricing:ProMonthly"] = "599", ["Pricing:EmailMonthly"] = "0" });
        Assert.Equal(599m, changed.Find("PRO", "MONTH")!.Price);
        Assert.DoesNotContain(changed.Options, o => o.Kind == "EMAIL");
    }

    [Fact]
    public async Task Saying_I_paid_checks_the_option_and_the_reference_uses_the_price_from_settings_and_a_reference_pays_once()
    {
        var c = await SetupAsync();
        var id = c.Shop.BusinessId;
        foreach (var bad in new[] { "", "ab", "!!bad", new string('1', 61) })
            Assert.Contains("reference", (await c.Payments.SubmitAsync(id, "PRO-MONTH", bad, null, "Owner")).FieldErrors.Keys);
        Assert.Contains("option", (await c.Payments.SubmitAsync(id, "AI-YEAR", Ref(), null, "Owner")).FieldErrors.Keys);
        Assert.Contains("note", (await c.Payments.SubmitAsync(id, "PRO-MONTH", Ref(), new string('x', 201), "Owner")).FieldErrors.Keys);
        Assert.Equal(FailureKind.NotFound, (await c.Payments.SubmitAsync(Guid.NewGuid(), "PRO-MONTH", Ref(), null, "Owner")).Failure);

        var reference = Ref();
        var made = await c.Payments.SubmitAsync(id, "PRO-YEAR", reference, " paid by Ana ", "Owner");
        Assert.True(made.IsSuccess);
        var row = (await c.Payments.ListForShopAsync(id)).Single();
        Assert.Equal(("PENDING", 4990m, 365, "paid by Ana"), (row.Status, row.Amount, row.Days, row.Note));                 // the price is the shop's, nothing the client typed

        Assert.Contains("reference", (await c.Payments.SubmitAsync(id, "PRO-MONTH", reference.ToLowerInvariant(), null, "Owner")).FieldErrors.Keys);   // the same reference again, any capitals
        var other = await Seed.CreateShopAsync();
        Assert.Contains("reference", (await c.Payments.SubmitAsync(other.BusinessId, "PRO-MONTH", reference, null, "Owner")).FieldErrors.Keys);        // or from another shop

        for (var i = 0; i < PaymentService.MaxPending - 1; i++) Assert.True((await c.Payments.SubmitAsync(id, "PRO-MONTH", Ref(), null, "Owner")).IsSuccess);
        Assert.Contains("limit", (await c.Payments.SubmitAsync(id, "PRO-MONTH", Ref(), null, "Owner")).FieldErrors.Keys);                              // too many waiting
    }

    [Fact]
    public async Task Confirming_extends_the_access_from_the_later_of_today_and_its_end_sets_the_plan_gives_a_receipt_and_works_once()
    {
        var c = await SetupAsync(accessEnds: Seed.BaseNow.UtcDateTime.AddDays(10));
        var id = (await c.Payments.SubmitAsync(c.Shop.BusinessId, "PRO-MONTH", Ref(), null, "Owner")).Value;

        var confirmed = await c.Payments.ConfirmAsync(id, "Boss");
        Assert.True(confirmed.IsSuccess, confirmed.Message);
        Assert.Matches(@"^VF-2030-\d{6}$", confirmed.Value);
        var shop = await ShopAsync(c);
        Assert.Equal(Seed.BaseNow.UtcDateTime.AddDays(40), DateTime.SpecifyKind(shop.TrialEndsAtUtc!.Value, DateTimeKind.Utc));   // 10 days left + 30
        Assert.Equal("PRO", shop.Plan);

        Assert.Equal(FailureKind.PolicyViolation, (await c.Payments.ConfirmAsync(id, "Boss")).Failure);                               // decided once: not extended twice
        Assert.Equal(FailureKind.PolicyViolation, (await c.Payments.RejectAsync(id, "late", "Boss")).Failure);
        Assert.Equal(Seed.BaseNow.UtcDateTime.AddDays(40), DateTime.SpecifyKind((await ShopAsync(c)).TrialEndsAtUtc!.Value, DateTimeKind.Utc));
        Assert.Equal(FailureKind.NotFound, (await c.Payments.ConfirmAsync(Guid.NewGuid(), "Boss")).Failure);

        var second = (await c.Payments.SubmitAsync(c.Shop.BusinessId, "PRO-YEAR", Ref(), null, "Owner")).Value;
        var receipt2 = (await c.Payments.ConfirmAsync(second, "Boss")).Value;
        Assert.NotEqual(confirmed.Value, receipt2);                                                                                   // receipt numbers are unique and rising
        Assert.Equal(Seed.BaseNow.UtcDateTime.AddDays(40 + 365), DateTime.SpecifyKind((await ShopAsync(c)).TrialEndsAtUtc!.Value, DateTimeKind.Utc));
    }

    [Fact]
    public async Task A_lapsed_shop_is_extended_from_today_the_add_ons_extend_their_own_dates_and_a_shop_with_no_end_date_only_gets_the_plan()
    {
        var lapsed = await SetupAsync(accessEnds: Seed.BaseNow.UtcDateTime.AddDays(-30));
        await lapsed.Payments.ConfirmAsync((await lapsed.Payments.SubmitAsync(lapsed.Shop.BusinessId, "PRO-MONTH", Ref(), null, "Owner")).Value, "Boss");
        Assert.Equal(Seed.BaseNow.UtcDateTime.AddDays(30), DateTime.SpecifyKind((await ShopAsync(lapsed)).TrialEndsAtUtc!.Value, DateTimeKind.Utc));

        var c = await SetupAsync();
        await c.Payments.ConfirmAsync((await c.Payments.SubmitAsync(c.Shop.BusinessId, "AI-MONTH", Ref(), null, "Owner")).Value, "Boss");
        await c.Payments.ConfirmAsync((await c.Payments.SubmitAsync(c.Shop.BusinessId, "EMAIL-MONTH", Ref(), null, "Owner")).Value, "Boss");
        var shop = await ShopAsync(c);
        Assert.Equal(Seed.BaseNow.UtcDateTime.AddDays(30), DateTime.SpecifyKind(shop.AiAddonUntilUtc!.Value, DateTimeKind.Utc));
        Assert.Equal(Seed.BaseNow.UtcDateTime.AddDays(30), DateTime.SpecifyKind(shop.EmailAddonUntilUtc!.Value, DateTimeKind.Utc));
        Assert.Equal(Seed.BaseNow.UtcDateTime.AddDays(10), DateTime.SpecifyKind(shop.TrialEndsAtUtc!.Value, DateTimeKind.Utc));       // an add-on never moves the access date

        var open = await SetupAsync();
        await using (var ctx = open.Db.CreateDbContext()) await ctx.Businesses.Where(b => b.Id == open.Shop.BusinessId).ExecuteUpdateAsync(u => u.SetProperty(b => b.TrialEndsAtUtc, (DateTime?)null));
        await open.Payments.ConfirmAsync((await open.Payments.SubmitAsync(open.Shop.BusinessId, "PRO-MONTH", Ref(), null, "Owner")).Value, "Boss");
        var unlimited = await ShopAsync(open);
        Assert.Null(unlimited.TrialEndsAtUtc);
        Assert.Equal("PRO", unlimited.Plan);
    }

    [Fact]
    public async Task Rejecting_keeps_the_reason_changes_nothing_and_frees_the_reference_and_a_payment_can_be_recorded_by_hand()
    {
        var c = await SetupAsync();
        var reference = Ref();
        var id = (await c.Payments.SubmitAsync(c.Shop.BusinessId, "PRO-MONTH", reference, null, "Owner")).Value;
        Assert.True((await c.Payments.RejectAsync(id, "Amount does not match", "Boss")).IsSuccess);
        var row = (await c.Payments.ListForShopAsync(c.Shop.BusinessId)).Single();
        Assert.Equal(("REJECTED", "Amount does not match"), (row.Status, row.RejectReason));
        Assert.Equal(FailureKind.PolicyViolation, (await c.Payments.ConfirmAsync(id, "Boss")).Failure);
        Assert.Equal(Seed.BaseNow.UtcDateTime.AddDays(10), DateTime.SpecifyKind((await ShopAsync(c)).TrialEndsAtUtc!.Value, DateTimeKind.Utc));
        Assert.True((await c.Payments.SubmitAsync(c.Shop.BusinessId, "PRO-MONTH", reference, null, "Owner")).IsSuccess);               // a rejected reference can be used again
        Assert.Equal(FailureKind.NotFound, (await c.Payments.RejectAsync(Guid.NewGuid(), null, "Boss")).Failure);

        var hand = await c.Payments.RecordByHandAsync(c.Shop.BusinessId, "PRO-MONTH", 300m, "cash", "Boss");
        Assert.True(hand.IsSuccess, hand.Message);
        var recorded = (await c.Payments.ListAsync("CONFIRMED", 100)).Single(p => p.BusinessId == c.Shop.BusinessId);
        Assert.Equal((300m, true, "cash"), (recorded.Amount, recorded.ByHand, recorded.Reference));                                    // a discount
        Assert.Equal(Seed.BaseNow.UtcDateTime.AddDays(40), DateTime.SpecifyKind((await ShopAsync(c)).TrialEndsAtUtc!.Value, DateTimeKind.Utc));
        Assert.True((await c.Payments.RecordByHandAsync(c.Shop.BusinessId, "AI-MONTH", null, null, "Boss")).IsSuccess);
        Assert.Contains(await c.Payments.ListAsync("CONFIRMED", 100), p => p.BusinessId == c.Shop.BusinessId && p.Kind == "AI" && p.Amount == 1500m && p.Reference == "By hand");   // no amount: the full price
        Assert.Contains("amount", (await c.Payments.RecordByHandAsync(c.Shop.BusinessId, "PRO-MONTH", -1m, null, "Boss")).FieldErrors.Keys);
        Assert.Contains("option", (await c.Payments.RecordByHandAsync(c.Shop.BusinessId, "nope", null, null, "Boss")).FieldErrors.Keys);
        Assert.Equal(FailureKind.NotFound, (await c.Payments.RecordByHandAsync(Guid.NewGuid(), "PRO-MONTH", null, null, "Boss")).Failure);
    }

    [Fact]
    public async Task The_totals_count_only_confirmed_payments_of_the_month_the_receipt_is_the_shops_own_and_the_database_checks_hold()
    {
        var c = await SetupAsync();
        var a = (await c.Payments.SubmitAsync(c.Shop.BusinessId, "PRO-MONTH", Ref(), null, "Owner")).Value;
        await c.Payments.SubmitAsync(c.Shop.BusinessId, "AI-MONTH", Ref(), null, "Owner");                                             // stays pending
        await c.Payments.ConfirmAsync(a, "Boss");
        var totals = await c.Payments.TotalsAsync(new DateOnly(2030, 3, 1));                                                          // the test database is shared: look at what this shop added
        var mine = (await c.Payments.ListAsync("CONFIRMED", 500)).Where(p => p.BusinessId == c.Shop.BusinessId).ToList();
        Assert.Equal((499m, 1), (mine.Sum(p => p.Amount), mine.Count));
        Assert.True(totals.Total >= 499m && totals.Count >= 1 && totals.ByKind["PRO"] >= 499m);
        Assert.Equal(0m, (await c.Payments.TotalsAsync(new DateOnly(2030, 2, 1))).Total);

        Assert.NotNull(await c.Payments.GetReceiptAsync(c.Shop.BusinessId, a));
        var other = await Seed.CreateShopAsync();
        Assert.Null(await c.Payments.GetReceiptAsync(other.BusinessId, a));                                                           // another shop's id
        var pending = (await c.Payments.ListForShopAsync(c.Shop.BusinessId)).Single(p => p.Status == "PENDING").Id;
        Assert.Null(await c.Payments.GetReceiptAsync(c.Shop.BusinessId, pending));                                                    // not confirmed: no receipt

        await using var ctx = c.Db.CreateDbContext();
        var bad = await Assert.ThrowsAnyAsync<Exception>(() => ctx.Database.ExecuteSqlInterpolatedAsync($"UPDATE payment SET status = 'CONFIRMED' WHERE id = {pending}"));
        Assert.Contains("payment_receipt", bad.ToString());                                                                           // confirmed always has a receipt number
        var amount = await Assert.ThrowsAnyAsync<Exception>(() => ctx.Database.ExecuteSqlInterpolatedAsync($"UPDATE payment SET amount = -1 WHERE id = {pending}"));
        Assert.Contains("payment_amount", amount.ToString());
    }

    [Fact]
    public async Task The_owner_pays_the_platform_owner_confirms_and_each_sees_only_their_own_over_HTTP()
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
        await HttpApp.PostFormAsync(tenant, "/Register", Form($"t{Guid.NewGuid():N}@example.com", "Paying Salon"));
        var stranger = HttpApp.NewClient(factory);
        await HttpApp.PostFormAsync(stranger, "/Register", Form($"s{Guid.NewGuid():N}@example.com", "Other Salon"));
        var db = new TestDbFactory(await TestDb.ConnectionStringAsync());
        Guid tenantId;
        await using (var ctx = db.CreateDbContext()) tenantId = await ctx.Businesses.Where(b => b.Name == "Paying Salon").OrderByDescending(b => b.CreatedAtUtc).Select(b => b.Id).FirstAsync();

        var page = WebUtility.HtmlDecode(await tenant.GetStringAsync("/Admin/Billing"));
        Assert.Contains(Text.BillingPageTitle, page);
        Assert.Contains("₱499", page);
        Assert.Contains("₱4,990", page);
        Assert.Contains(Text.BillingYearSaving("₱998"), page);

        var reference = Ref();
        var bad = await HttpApp.PostFormAsync(tenant, "/Admin/Billing", new() { ["Input.Option"] = "PRO-MONTH", ["Input.Reference"] = "x" }, tokenPage: "/Admin/Billing");
        Assert.Contains(Text.PayReferenceInvalid, WebUtility.HtmlDecode(await bad.Content.ReadAsStringAsync()));
        var sent = await HttpApp.PostFormAsync(tenant, "/Admin/Billing", new() { ["Input.Option"] = "PRO-MONTH", ["Input.Reference"] = reference }, tokenPage: "/Admin/Billing");
        Assert.Equal(HttpStatusCode.Redirect, sent.StatusCode);
        Assert.Contains(Text.BillingStatusPending, WebUtility.HtmlDecode(await tenant.GetStringAsync("/Admin/Billing")));

        Assert.Equal(HttpStatusCode.NotFound, (await tenant.GetAsync("/Platform/Payments")).StatusCode);                              // a shop owner finds nothing there
        var waiting = WebUtility.HtmlDecode(await boss.GetStringAsync("/Admin"));
        Assert.Contains(Text.PaymentsAlert(1), waiting);                                                                              // the platform owner is told
        var list = WebUtility.HtmlDecode(await boss.GetStringAsync("/Platform/Payments"));
        Assert.Contains(reference, list);
        Guid paymentId;
        await using (var ctx = db.CreateDbContext()) paymentId = await ctx.Payments.Where(p => p.BusinessId == tenantId).Select(p => p.Id).SingleAsync();

        var denied = await HttpApp.PostFormAsync(tenant, "/Platform/Payments?handler=Confirm", new() { ["id"] = paymentId.ToString() }, tokenPage: "/Admin/Billing");
        Assert.Equal(HttpStatusCode.NotFound, denied.StatusCode);
        var ok = await HttpApp.PostFormAsync(boss, "/Platform/Payments?handler=Confirm", new() { ["id"] = paymentId.ToString() }, tokenPage: "/Platform/Payments");
        Assert.Equal(HttpStatusCode.Redirect, ok.StatusCode);

        await using (var ctx = db.CreateDbContext())
        {
            var shop = await ctx.Businesses.AsNoTracking().SingleAsync(b => b.Id == tenantId);
            Assert.Equal("PRO", shop.Plan);
            Assert.True(shop.TrialEndsAtUtc > DateTime.UtcNow.AddDays(55));                                                             // 30 trial days + 30 paid
        }
        var after = WebUtility.HtmlDecode(await tenant.GetStringAsync("/Admin/Billing"));
        Assert.Contains($"/Admin/Billing/Receipt/{paymentId}", after);
        var receipt = WebUtility.HtmlDecode(await tenant.GetStringAsync($"/Admin/Billing/Receipt/{paymentId}"));
        Assert.Contains("VF-", receipt);
        Assert.Contains(Text.ReceiptNotOfficial, receipt);
        Assert.Equal(HttpStatusCode.NotFound, (await stranger.GetAsync($"/Admin/Billing/Receipt/{paymentId}")).StatusCode);          // another shop's receipt

        await using (var ctx = db.CreateDbContext())                                                                                  // the access ends: the owner can still open Billing
            await ctx.Businesses.Where(b => b.Id == tenantId).ExecuteUpdateAsync(u => u.SetProperty(b => b.TrialEndsAtUtc, DateTime.UtcNow.AddDays(-2)));
        Assert.Equal(HttpStatusCode.OK, (await tenant.GetAsync("/Admin/Billing")).StatusCode);
        Assert.Contains("Your access ended on", WebUtility.HtmlDecode(await tenant.GetStringAsync("/Admin/Billing")));
        var locked = await tenant.GetAsync("/Admin/Customers");
        Assert.Equal(HttpStatusCode.Redirect, locked.StatusCode);                                                                     // everything else is still locked
        Assert.Contains("TrialEnded", locked.Headers.Location!.ToString());
    }
}
