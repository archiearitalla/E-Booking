using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using VeskaFlow.Web.Services;

namespace VeskaFlow.Web.Tests;

/// <summary>Reminder emails 7, 3 and 1 days before access ends, and how a client is told to renew.</summary>
[Collection("http")]
public class ExpiryNoticeTests
{
    private static string Id(string prefix) => prefix + Guid.NewGuid().ToString("N")[..10];

    private sealed class RefusingEmailSender : IEmailSender
    {
        public bool IsConfigured => true;
        public int Attempts { get; private set; }
        public Task<bool> SendAsync(string to, string subject, string body, string? html = null, CancellationToken ct = default)
        {
            Attempts++;
            return Task.FromResult(false);
        }
    }

    private static BillingInfo Billing() => new(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
    {
        ["Billing:Price"] = "PHP 499 per month", ["Billing:PayTo"] = "GCash 09XX XXX XXXX (Test Name)", ["Trial:ContactUrl"] = "https://m.me/test.shop",
    }).Build());

    private sealed record Shop(TestDbFactory Db, TestClock Clock, RegisteredOwner Owner, string Email);

    private static async Task<Shop> ShopAsync(bool requireEmail = false)
    {
        var db = new TestDbFactory(await TestDb.ConnectionStringAsync());
        var clock = new TestClock(Seed.BaseNow);
        var email = $"{Id("o")}@example.com";
        var owner = (await new OnboardingService(db, clock).RegisterAsync(
            new RegisterRequest("Cuts", Id("ex"), "Owner", email, "correct-horse-1", TrialDays: 30, RequireEmailConfirmation: requireEmail))).Value!;
        return new Shop(db, clock, owner, email);
    }

    /// <summary>The emails this shop's owner got (the test database is shared with other shops).</summary>
    private static List<(string To, string Subject, string Body, string? Html)> Mine(CapturingEmailSender sender, Shop s) =>
        sender.Sent.Where(m => m.To == s.Email).ToList();

    private static ExpiryNoticeService Service(Shop s, IEmailSender sender) =>
        new(s.Db, s.Clock, sender, Billing(), new ConfigurationBuilder().Build(), NullLogger<ExpiryNoticeService>.Instance);

    [Fact]
    public void The_step_is_seven_three_or_one_days_and_nothing_when_early_or_over()
    {
        Assert.Null(ExpiryNoticeService.StageFor(30));
        Assert.Null(ExpiryNoticeService.StageFor(7.01));
        Assert.Equal(7, ExpiryNoticeService.StageFor(7));
        Assert.Equal(7, ExpiryNoticeService.StageFor(4.2));
        Assert.Equal(3, ExpiryNoticeService.StageFor(3));
        Assert.Equal(3, ExpiryNoticeService.StageFor(2.1));
        Assert.Equal(1, ExpiryNoticeService.StageFor(1));
        Assert.Equal(1, ExpiryNoticeService.StageFor(0.2));
        Assert.Null(ExpiryNoticeService.StageFor(0));
        Assert.Null(ExpiryNoticeService.StageFor(-2));
    }

    [Fact]
    public async Task Each_step_is_sent_once_with_the_price_and_where_to_pay_and_an_extension_starts_over()
    {
        var s = await ShopAsync(); // access ends 30 days after BaseNow
        var sender = new CapturingEmailSender();
        var service = Service(s, sender);
        var end = s.Clock.GetUtcNow().UtcDateTime.AddDays(30);

        await service.SendDueAsync(); // 30 days left: too early
        Assert.Empty(Mine(sender, s));

        s.Clock.Now = new DateTimeOffset(end.AddDays(-6.5), TimeSpan.Zero);
        await service.SendDueAsync();
        await service.SendDueAsync(); // not twice
        Assert.Single(Mine(sender, s));
        var first = Assert.Single(Mine(sender, s));
        Assert.Equal(s.Email, first.To);
        Assert.Contains("7 days", first.Subject);
        Assert.Contains("PHP 499 per month", first.Body);
        Assert.Contains("GCash 09XX XXX XXXX (Test Name)", first.Body);
        Assert.Contains("screenshot", first.Body); // how to send the proof
        Assert.Contains("https://m.me/test.shop", first.Body); // where to send it

        s.Clock.Now = new DateTimeOffset(end.AddDays(-2.5), TimeSpan.Zero);
        await service.SendDueAsync();
        s.Clock.Now = new DateTimeOffset(end.AddDays(-0.5), TimeSpan.Zero);
        await service.SendDueAsync();
        Assert.Contains("tomorrow", Mine(sender, s)[^1].Subject);
        Assert.Equal(3, Mine(sender, s).Count);

        s.Clock.Now = new DateTimeOffset(end.AddHours(1), TimeSpan.Zero);
        await service.SendDueAsync(); // already over: the trial-ended page speaks now
        Assert.Equal(3, Mine(sender, s).Count);

        // Paying: a new end date, so the reminders begin again for it.
        var accounts = new AccountControlService(s.Db, s.Clock, new ConfigurationBuilder().Build(), new NoEmailSender());
        var extended = (await accounts.ExtendAccessAsync(s.Owner.BusinessId, 30)).Value;
        s.Clock.Now = new DateTimeOffset(extended.AddDays(-6), TimeSpan.Zero);
        await service.SendDueAsync();
        Assert.Equal(4, Mine(sender, s).Count);
    }

    [Fact]
    public async Task Nothing_is_sent_without_an_email_service_to_switched_off_shops_or_unconfirmed_owners_and_a_refused_email_is_retried()
    {
        var s = await ShopAsync();
        var soon = new DateTimeOffset(s.Clock.GetUtcNow().UtcDateTime.AddDays(29), TimeSpan.Zero);
        s.Clock.Now = soon;

        Assert.Equal(0, await Service(s, new NoEmailSender()).SendDueAsync());

        var refusing = new RefusingEmailSender();
        Assert.Equal(0, await Service(s, refusing).SendDueAsync());
        Assert.Equal(0, await Service(s, refusing).SendDueAsync());
        Assert.True(refusing.Attempts >= 2); // not marked as sent, so tried again
        var sender = new CapturingEmailSender();
        await Service(s, sender).SendDueAsync(); // and it goes out once the service accepts
        Assert.Single(Mine(sender, s));

        var off = await ShopAsync();
        off.Clock.Now = soon;
        await new AccountControlService(off.Db, off.Clock, new ConfigurationBuilder().Build(), new NoEmailSender()).SetEnabledAsync(off.Owner.BusinessId, false);
        var offSender = new CapturingEmailSender();
        await Service(off, offSender).SendDueAsync();
        Assert.DoesNotContain(offSender.Sent, m => m.To == off.Email);

        var unconfirmed = await ShopAsync(requireEmail: true);
        unconfirmed.Clock.Now = soon;
        var unconfirmedSender = new CapturingEmailSender();
        await Service(unconfirmed, unconfirmedSender).SendDueAsync();
        Assert.DoesNotContain(unconfirmedSender.Sent, m => m.To == unconfirmed.Email);
    }

    [Fact]
    public void How_to_renew_comes_from_settings_and_is_empty_when_nothing_is_set()
    {
        var info = Billing();
        Assert.True(info.HasDetails);
        Assert.Equal("PHP 499 per month", info.Price);
        Assert.False(new BillingInfo(new ConfigurationBuilder().Build()).HasDetails);
    }
}
