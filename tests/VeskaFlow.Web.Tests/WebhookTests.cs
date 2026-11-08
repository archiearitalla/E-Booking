using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using VeskaFlow.Core.Domain;
using VeskaFlow.Web;
using VeskaFlow.Web.Data;
using VeskaFlow.Web.Services;

namespace VeskaFlow.Web.Tests;

/// <summary>Outgoing webhooks (BUSINESS_RULES 6.y24): the address guard, the signature, which events are made, delivery and retries, and the Integrations page.</summary>
[Collection("http")]
public class WebhookTests
{
    private static readonly BookingActor Manager = BookingActor.Staff(Guid.NewGuid(), "Ana (manager)");

    private sealed class FakeTransport : IWebhookTransport
    {
        public List<(Uri Url, Dictionary<string, string> Headers, string Body)> Sent { get; } = [];
        public Func<WebhookResult> Answer { get; set; } = () => new WebhookResult(true, 200, null);
        public Task<WebhookResult> PostAsync(Uri url, IReadOnlyDictionary<string, string> headers, string body, CancellationToken ct)
        {
            Sent.Add((url, new Dictionary<string, string>(headers), body));
            return Task.FromResult(Answer());
        }
    }

    private sealed record Ctx(TestDbFactory Db, TestClock Clock, WebhookService Hooks, FakeTransport Transport, BookingService Booking, Shop Shop);

    private static async Task<Ctx> SetupAsync(Dictionary<string, string?>? config = null, bool enable = true)
    {
        var db = new TestDbFactory(await TestDb.ConnectionStringAsync());
        var clock = new TestClock(Seed.BaseNow);
        var transport = new FakeTransport();
        var cfg = new ConfigurationBuilder().AddInMemoryCollection(config ?? new()).Build();
        var hooks = new WebhookService(db, clock, transport, cfg, NullLogger<WebhookService>.Instance);
        var shop = await Seed.CreateShopAsync();
        if (enable) Assert.True((await hooks.SaveAsync(shop.BusinessId, "https://n8n.example.com/webhook/veskaflow", true)).IsSuccess);
        await using (var ctx = db.CreateDbContext())                                                                                               // the test database is shared
        {
            await ctx.PlatformSettings.Where(s => s.Key == WebhookService.CursorKey).ExecuteDeleteAsync();
            await ctx.WebhookEvents.ExecuteDeleteAsync();
        }
        await hooks.PublishNewAsync();                                                                                                             // the first run only sets the starting point
        return new Ctx(db, clock, hooks, transport, new BookingService(db, clock), shop);
    }

    private static async Task<Guid> BookAsync(Ctx c, int hour, string name = "Pia Cruz", string phone = "09175550101")
    {
        var made = await c.Booking.CreateAsync(Seed.Request(c.Shop, Seed.Manila(5, hour), actor: Manager) with { CustomerName = name, Phone = phone, Email = "pia@example.com" });
        Assert.True(made.IsSuccess, made.Message);
        return made.Value!.Booking.Id;
    }

    private static async Task<List<WebhookEvent>> EventsAsync(Ctx c)
    {
        await using var ctx = c.Db.CreateDbContext();
        return await ctx.WebhookEvents.AsNoTracking().Where(e => e.BusinessId == c.Shop.BusinessId).OrderBy(e => e.CreatedAtUtc).ThenBy(e => e.SourceAuditId).ToListAsync();
    }

    // ---------------------------------------------------------------- the guard and the signature

    [Theory]
    [InlineData("https://n8n.example.com/webhook/abc", true)]
    [InlineData("https://hooks.example.co.ph:5678/webhook/x?y=1", true)]
    [InlineData("http://n8n.example.com/webhook/abc", false)]
    [InlineData("https://user:pass@n8n.example.com/x", false)]
    [InlineData("https://localhost/x", false)]
    [InlineData("https://n8n.local/x", false)]
    [InlineData("https://n8n.internal/x", false)]
    [InlineData("https://intranet/x", false)]
    [InlineData("https://127.0.0.1/x", false)]
    [InlineData("https://10.0.0.5/x", false)]
    [InlineData("https://192.168.1.10/x", false)]
    [InlineData("https://169.254.169.254/latest/meta-data", false)]
    [InlineData("https://100.64.0.1/x", false)]
    [InlineData("https://[::1]/x", false)]
    [InlineData("https://8.8.8.8/x", true)]
    [InlineData("ftp://n8n.example.com/x", false)]
    [InlineData("not a url", false)]
    [InlineData("", false)]
    public void Only_public_https_addresses_are_allowed(string url, bool allowed) => Assert.Equal(allowed, WebhookGuard.Parse(url, out _) is not null);

    [Theory]
    [InlineData("8.8.8.8", true)]
    [InlineData("1.1.1.1", true)]
    [InlineData("2606:4700:4700::1111", true)]
    [InlineData("127.0.0.1", false)]
    [InlineData("10.1.2.3", false)]
    [InlineData("172.16.0.1", false)]
    [InlineData("172.32.0.1", true)]
    [InlineData("192.168.0.1", false)]
    [InlineData("169.254.1.1", false)]
    [InlineData("0.0.0.0", false)]
    [InlineData("224.0.0.1", false)]
    [InlineData("255.255.255.255", false)]
    [InlineData("::1", false)]
    [InlineData("fe80::1", false)]
    [InlineData("fd00::1", false)]
    [InlineData("::ffff:10.0.0.1", false)]
    public void Private_loopback_and_reserved_addresses_are_never_public(string ip, bool expected) => Assert.Equal(expected, WebhookGuard.IsPublicAddress(IPAddress.Parse(ip)));

    [Fact]
    public void The_signature_is_the_HMAC_of_timestamp_dot_body_and_matches_what_n8n_computes()
    {
        // The same value comes out of: printf '1700000000.{"a":1}' | openssl dgst -sha256 -hmac "whsec_test"
        Assert.Equal("sha256=38877139021993b830af32feea6e18a8da83eb2f6e49ee50bd9e4cf4ca4d3789", WebhookSigner.Sign("whsec_test", 1700000000, "{\"a\":1}"));
        Assert.NotEqual(WebhookSigner.Sign("whsec_test", 1700000000, "{\"a\":1}"), WebhookSigner.Sign("whsec_test", 1700000001, "{\"a\":1}"));
        Assert.NotEqual(WebhookSigner.Sign("whsec_test", 1700000000, "{\"a\":1}"), WebhookSigner.Sign("whsec_other", 1700000000, "{\"a\":1}"));
        Assert.NotEqual(WebhookSigner.NewSecret(), WebhookSigner.NewSecret());
        Assert.StartsWith("whsec_", WebhookSigner.NewSecret());
    }

    // ---------------------------------------------------------------- the shop's settings

    [Fact]
    public async Task The_address_is_checked_the_secret_is_made_once_and_another_shop_is_untouched()
    {
        var c = await SetupAsync(enable: false);
        var id = c.Shop.BusinessId;
        Assert.Contains("url", (await c.Hooks.SaveAsync(id, "http://n8n.example.com/x", true)).FieldErrors.Keys);
        Assert.Contains("url", (await c.Hooks.SaveAsync(id, "https://127.0.0.1/x", true)).FieldErrors.Keys);
        Assert.Contains("url", (await c.Hooks.SaveAsync(id, "", true)).FieldErrors.Keys);                                   // on with no address
        Assert.False((await c.Hooks.GetSettingsAsync(id))!.Enabled);

        Assert.True((await c.Hooks.SaveAsync(id, " https://n8n.example.com/webhook/a ", true)).IsSuccess);
        var first = (await c.Hooks.GetSettingsAsync(id))!;
        Assert.Equal((true, "https://n8n.example.com/webhook/a"), (first.Enabled, first.Url));
        Assert.StartsWith("whsec_", first.Secret);
        Assert.True((await c.Hooks.SaveAsync(id, "https://n8n.example.com/webhook/b", false)).IsSuccess);
        var second = (await c.Hooks.GetSettingsAsync(id))!;
        Assert.Equal((false, first.Secret), (second.Enabled, second.Secret));                                               // saving again keeps the secret
        var fresh = (await c.Hooks.RegenerateSecretAsync(id)).Value;
        Assert.NotEqual(first.Secret, fresh);
        Assert.Equal(FailureKind.NotFound, (await c.Hooks.SaveAsync(Guid.NewGuid(), "https://n8n.example.com/x", true)).Failure);
        var other = await Seed.CreateShopAsync();
        Assert.False((await c.Hooks.GetSettingsAsync(other.BusinessId))!.Enabled);
    }

    // ---------------------------------------------------------------- making events

    [Fact]
    public async Task A_booking_becomes_one_event_with_the_booking_and_the_customer_and_nothing_private()
    {
        var c = await SetupAsync();
        var id = await BookAsync(c, 10);
        Assert.Equal(1, await c.Hooks.PublishNewAsync());
        Assert.Equal(0, await c.Hooks.PublishNewAsync());                                                                   // one audit row, one event

        var e = Assert.Single(await EventsAsync(c));
        Assert.Equal(("booking.created", "PENDING"), (e.Event, e.Status));
        using var doc = JsonDocument.Parse(e.Payload);
        var root = doc.RootElement;
        Assert.Equal("booking.created", root.GetProperty("event").GetString());
        Assert.Equal(e.Id, root.GetProperty("id").GetGuid());
        Assert.Equal(c.Shop.BusinessId, root.GetProperty("shop").GetProperty("id").GetGuid());
        var booking = root.GetProperty("data").GetProperty("booking");
        Assert.Equal(id, booking.GetProperty("id").GetGuid());
        Assert.Equal("CONFIRMED", booking.GetProperty("status").GetString());
        Assert.True(booking.GetProperty("price").GetDecimal() > 0);
        Assert.Equal("2030-03-05 10:00", booking.GetProperty("startsAtLocal").GetString());
        var customer = root.GetProperty("data").GetProperty("customer");
        Assert.Equal(("Pia Cruz", "09175550101", "pia@example.com"), (customer.GetProperty("name").GetString(), customer.GetProperty("phone").GetString(), customer.GetProperty("email").GetString()));
        var text = e.Payload.ToLowerInvariant();
        Assert.DoesNotContain("managetoken", text);                                                                         // never the customer's manage link
        Assert.DoesNotContain("visitnotes", text);                                                                          // never a medical record
        Assert.DoesNotContain("password", text);
    }

    [Fact]
    public async Task Status_changes_moves_and_sales_make_events_and_other_audit_rows_and_other_shops_make_none()
    {
        var c = await SetupAsync();
        var off = await SetupAsync(enable: false);                                                                          // a shop that never switched it on
        var id = await BookAsync(c, 10);
        await BookAsync(off, 10);
        Assert.True((await c.Booking.RescheduleAsync(c.Shop.BusinessId, id, Seed.Manila(5, 14), Manager)).IsSuccess);
        var earlier = Seed.Manila(4, 9);                                                                                    // so it can be completed (a visit is done only once it has started)
        var earlierEnd = earlier.AddMinutes(30);
        await using (var ctx = c.Db.CreateDbContext()) await ctx.Bookings.Where(b => b.Id == id).ExecuteUpdateAsync(u => u.SetProperty(b => b.StartsAtUtc, earlier).SetProperty(b => b.EndsAtUtc, earlierEnd).SetProperty(b => b.OccupiedUntilUtc, earlierEnd));
        c.Clock.Now = c.Clock.Now.AddHours(5);                                                                              // the visit has started
        var done = await c.Booking.TransitionAsync(c.Shop.BusinessId, id, BookingStatus.Completed, Manager);
        Assert.True(done.IsSuccess, done.Message);
        var second = await BookAsync(c, 11, "Rey Lim", "09175550102");
        Assert.True((await c.Booking.TransitionAsync(c.Shop.BusinessId, second, BookingStatus.Cancelled, Manager, "customer asked")).IsSuccess);
        var third = await BookAsync(c, 12, "Gil Reyes", "09175550103");
        await using (var ctx = c.Db.CreateDbContext()) ctx.AuditLogs.Add(new AuditLog { BusinessId = c.Shop.BusinessId, BookingId = third, ActorType = "STAFF", ActorLabel = "Ana", Action = "PAYMENT_SET", Reason = "x", AtUtc = DateTime.UtcNow });
        await using (var ctx = c.Db.CreateDbContext()) await ctx.SaveChangesAsync();

        await c.Hooks.PublishNewAsync();
        var events = (await EventsAsync(c)).Select(e => e.Event).ToList();
        Assert.Equal(new[] { "booking.created", "booking.rescheduled", "booking.completed", "booking.created", "booking.cancelled", "booking.created" }, events);   // PAYMENT_SET is not an event
        Assert.Contains("customer asked", (await EventsAsync(c)).Single(e => e.Event == "booking.cancelled").Payload);
        await using var check = off.Db.CreateDbContext();
        Assert.Empty(await check.WebhookEvents.Where(e => e.BusinessId == off.Shop.BusinessId).ToListAsync());
    }

    [Fact]
    public async Task Switching_it_on_never_replays_history_and_a_walk_in_has_no_phone()
    {
        var c = await SetupAsync(enable: false);
        await BookAsync(c, 9);                                                                                              // made before it was switched on
        await c.Hooks.PublishNewAsync();
        Assert.True((await c.Hooks.SaveAsync(c.Shop.BusinessId, "https://n8n.example.com/x", true)).IsSuccess);
        await c.Hooks.PublishNewAsync();
        Assert.Empty(await EventsAsync(c));                                                                                 // the earlier booking was passed over while it was off

        await BookAsync(c, 10, "Walk In", "");
        await c.Hooks.PublishNewAsync();
        using var doc = JsonDocument.Parse(Assert.Single(await EventsAsync(c)).Payload);
        Assert.False(doc.RootElement.GetProperty("data").GetProperty("customer").TryGetProperty("phone", out _));         // a walk-in placeholder is never sent as a number
    }

    [Fact]
    public async Task A_sale_recorded_by_hand_is_an_event()
    {
        var c = await SetupAsync();
        await using (var ctx = c.Db.CreateDbContext()) await ctx.Businesses.Where(b => b.Id == c.Shop.BusinessId).ExecuteUpdateAsync(u => u.SetProperty(b => b.AllowManualSales, true));
        var sales = new ManualSaleService(c.Db, c.Clock);
        var made = await sales.RecordAsync(c.Shop.BusinessId, new ManualSaleInput(c.Shop.ServiceId, null, new DateOnly(2030, 3, 4), null, 300m, "Kuya Ben", null, null, Guid.NewGuid().ToString("N")), Manager);
        Assert.True(made.IsSuccess, made.Message);
        await c.Hooks.PublishNewAsync();
        var e = Assert.Single(await EventsAsync(c));
        Assert.Equal("sale.recorded", e.Event);
        Assert.Contains("\"paidAmount\":300", e.Payload);
    }

    // ---------------------------------------------------------------- delivery

    [Fact]
    public async Task A_delivery_is_signed_with_the_shops_secret_and_marked_delivered()
    {
        var c = await SetupAsync();
        await BookAsync(c, 10);
        await c.Hooks.PublishNewAsync();
        Assert.Equal(1, await c.Hooks.DeliverDueAsync());

        var sent = Assert.Single(c.Transport.Sent);
        Assert.Equal("https://n8n.example.com/webhook/veskaflow", sent.Url.ToString());
        var e = Assert.Single(await EventsAsync(c));
        Assert.Equal("booking.created", sent.Headers["X-VeskaFlow-Event"]);
        Assert.Equal(e.Id.ToString(), sent.Headers["X-VeskaFlow-Delivery"]);
        var stamp = long.Parse(sent.Headers["X-VeskaFlow-Timestamp"]);
        var secret = (await c.Hooks.GetSettingsAsync(c.Shop.BusinessId))!.Secret!;
        Assert.Equal(WebhookSigner.Sign(secret, stamp, sent.Body), sent.Headers["X-VeskaFlow-Signature"]);                  // what n8n recomputes
        Assert.Equal(e.Payload, sent.Body);
        Assert.Equal(("DELIVERED", 1), (e.Status, e.Attempts));
        Assert.NotNull(e.DeliveredAtUtc);
        Assert.Equal(0, await c.Hooks.DeliverDueAsync());                                                                   // nothing is sent twice
    }

    [Fact]
    public async Task A_failed_delivery_is_retried_later_with_longer_waits_and_given_up_after_six_tries()
    {
        var c = await SetupAsync();
        await BookAsync(c, 10);
        await c.Hooks.PublishNewAsync();
        c.Transport.Answer = () => new WebhookResult(false, 500, "HTTP 500");

        var waits = new List<double>();
        for (var i = 0; i < WebhookService.MaxAttempts; i++)
        {
            Assert.Equal(0, await c.Hooks.DeliverDueAsync());
            var e = Assert.Single(await EventsAsync(c));
            Assert.Equal(i + 1, e.Attempts);
            Assert.Equal((500, "HTTP 500"), (e.LastStatusCode, e.LastError));
            if (e.Status == "FAILED") break;
            waits.Add((e.NextAttemptAtUtc - c.Clock.Now.UtcDateTime).TotalMinutes);
            Assert.Equal(0, await c.Hooks.DeliverDueAsync());                                                               // not due yet: no extra try
            c.Clock.Now = c.Clock.Now.AddHours(7);                                                                          // past any wait
        }
        Assert.Equal(new[] { 1d, 5d, 30d, 120d, 360d }, waits);
        Assert.Equal("FAILED", (await EventsAsync(c)).Single().Status);
        Assert.Equal(WebhookService.MaxAttempts, c.Transport.Sent.Count);
        c.Transport.Answer = () => new WebhookResult(true, 200, null);
        c.Clock.Now = c.Clock.Now.AddDays(1);
        Assert.Equal(0, await c.Hooks.DeliverDueAsync());                                                                   // a failed event stays failed
    }

    [Fact]
    public async Task A_shop_that_switched_it_off_sends_nothing_and_a_deleted_shops_events_are_dropped_and_a_test_works()
    {
        var c = await SetupAsync();
        await BookAsync(c, 10);
        await c.Hooks.PublishNewAsync();
        Assert.True((await c.Hooks.SaveAsync(c.Shop.BusinessId, "https://n8n.example.com/webhook/veskaflow", false)).IsSuccess);
        Assert.Equal(0, await c.Hooks.DeliverDueAsync());
        Assert.Empty(c.Transport.Sent);
        Assert.Equal("FAILED", (await EventsAsync(c)).Single().Status);

        var test = await c.Hooks.SendTestAsync(c.Shop.BusinessId);
        Assert.True(test.Ok);
        Assert.Equal("test.ping", Assert.Single(c.Transport.Sent).Headers["X-VeskaFlow-Event"]);
        Assert.Contains("This is a test from VeskaFlow.", c.Transport.Sent[0].Body);
        c.Transport.Answer = () => new WebhookResult(false, 404, "HTTP 404");
        Assert.Equal((false, 404), ((await c.Hooks.SendTestAsync(c.Shop.BusinessId)).Ok, (await c.Hooks.SendTestAsync(c.Shop.BusinessId)).StatusCode));

        var orphan = Guid.NewGuid();
        await using (var ctx = c.Db.CreateDbContext())
        {
            ctx.WebhookEvents.Add(new WebhookEvent { BusinessId = orphan, Event = "booking.created", Payload = "{}", CreatedAtUtc = DateTime.UtcNow, NextAttemptAtUtc = DateTime.UtcNow.AddYears(-30) });
            await ctx.SaveChangesAsync();
        }
        c.Clock.Now = DateTimeOffset.UtcNow.AddYears(1);
        await c.Hooks.DeliverDueAsync();
        await using var check = c.Db.CreateDbContext();
        Assert.Empty(await check.WebhookEvents.Where(e => e.BusinessId == orphan).ToListAsync());
    }

    [Fact]
    public async Task Platform_events_go_to_the_platform_address_only_when_it_is_set_up_and_a_payment_makes_one()
    {
        var off = await SetupAsync();
        await off.Hooks.EnqueuePlatformAsync("shop.signed_up", new { shop = new { name = "X" } });
        await using (var ctx = off.Db.CreateDbContext()) Assert.Empty(await ctx.WebhookEvents.Where(e => e.BusinessId == null && e.Event == "shop.signed_up" && e.CreatedAtUtc >= Seed.BaseNow.UtcDateTime).ToListAsync());

        var c = await SetupAsync(new() { ["Webhooks:PlatformUrl"] = "https://n8n.example.com/webhook/veskaflow-platform", ["Webhooks:PlatformSecret"] = "a-long-platform-secret-123" });
        Assert.True(c.Hooks.PlatformConfigured);
        var payments = new PaymentService(c.Db, c.Clock, new PricingInfo(new ConfigurationBuilder().Build()), c.Hooks);
        var paid = await payments.SubmitAsync(c.Shop.BusinessId, "PRO-MONTH", "GC" + Guid.NewGuid().ToString("N")[..10], null, "Owner");
        Assert.True(paid.IsSuccess);
        await payments.ConfirmAsync(paid.Value, "Boss");

        await c.Hooks.DeliverDueAsync();
        var mine = c.Transport.Sent.Where(s => s.Url.AbsolutePath.EndsWith("veskaflow-platform") && s.Body.Contains(c.Shop.BusinessId.ToString())).ToList();
        Assert.Equal(new[] { "payment.submitted", "payment.confirmed" }, mine.Select(s => s.Headers["X-VeskaFlow-Event"]));
        Assert.All(mine, s => Assert.Equal(WebhookSigner.Sign("a-long-platform-secret-123", long.Parse(s.Headers["X-VeskaFlow-Timestamp"]), s.Body), s.Headers["X-VeskaFlow-Signature"]));
        Assert.Contains("\"receiptNo\":\"VF-", mine[1].Body);
    }

    // ---------------------------------------------------------------- the page

    [Fact]
    public async Task The_Integrations_page_saves_a_good_address_refuses_a_private_one_shows_the_secret_and_the_latest_deliveries()
    {
        var (factory, _) = await HttpApp.StartAsync();
        using var _f = factory;
        var owner = HttpApp.NewClient(factory);
        await HttpApp.PostFormAsync(owner, "/Register", new()
        {
            ["Input.BusinessName"] = "Hook Shop", ["Input.OwnerName"] = "Owner", ["Input.Email"] = $"hook{Guid.NewGuid():N}@example.com",
            ["Input.Password"] = "correct-horse-battery", ["Input.Accept"] = "true", ["Input.Industry"] = "barbershop",
        });

        var page = Decode(await owner.GetStringAsync("/Admin/Integrations"));
        Assert.Contains(Text.IntegrationsTitle, page);
        Assert.DoesNotContain("whsec_", page);                                                                              // no secret until an address is saved
        Assert.Contains("href=\"/Admin/Integrations\"", page);                                                              // in the menu

        var bad = await HttpApp.PostFormAsync(owner, "/Admin/Integrations?handler=Save", new() { ["Input.Url"] = "https://192.168.1.5/x", ["Input.Enabled"] = "true" }, tokenPage: "/Admin/Integrations");
        Assert.Contains(Text.WebhookUrlPrivate, Decode(await bad.Content.ReadAsStringAsync()));
        var http = await HttpApp.PostFormAsync(owner, "/Admin/Integrations?handler=Save", new() { ["Input.Url"] = "http://n8n.example.com/x", ["Input.Enabled"] = "true" }, tokenPage: "/Admin/Integrations");
        Assert.Contains(Text.WebhookUrlHttps, Decode(await http.Content.ReadAsStringAsync()));

        var ok = await HttpApp.PostFormAsync(owner, "/Admin/Integrations?handler=Save", new() { ["Input.Url"] = "https://n8n.example.com/webhook/veskaflow", ["Input.Enabled"] = "true" }, tokenPage: "/Admin/Integrations");
        Assert.Equal(HttpStatusCode.Redirect, ok.StatusCode);
        var saved = Decode(await owner.GetStringAsync("/Admin/Integrations"));
        Assert.Contains("https://n8n.example.com/webhook/veskaflow", saved);
        Assert.Contains("whsec_", saved);
        Assert.Contains(Text.IntegrationsNoneYet, saved);
        Assert.Contains("booking.created", saved);
    }

    private static string Decode(string html) => WebUtility.HtmlDecode(html);
}
