using System.Net;
using VeskaFlow.Web.Services;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using static VeskaFlow.Web.Tests.Seed;

namespace VeskaFlow.Web.Tests;

/// <summary>An email sender that keeps what it was asked to send, so a test can open the link in it.</summary>
public sealed class CapturingEmailSender : IEmailSender
{
    public bool IsConfigured => true;
    public List<(string To, string Subject, string Body)> Sent { get; } = [];

    public Task<bool> SendAsync(string to, string subject, string body, CancellationToken ct = default)
    {
        Sent.Add((to, subject, body));
        return Task.FromResult(true);
    }
}

/// <summary>Email confirmation before use, and the platform owner's switch to enable or disable a shop.</summary>
[Collection("http")] // some tests start the real app, which reads its settings from environment variables
public class AccountControlTests
{
    private static string Id(string prefix) => prefix + Guid.NewGuid().ToString("N")[..10];

    private sealed record Setup(TestDbFactory Db, TestClock Clock, OnboardingService Onboarding, AuthService Auth, AccountControlService Accounts);

    private static async Task<Setup> SetupAsync(IConfiguration? config = null, IEmailSender? sender = null)
    {
        var db = new TestDbFactory(await TestDb.ConnectionStringAsync());
        var clock = new TestClock(BaseNow);
        var accounts = new AccountControlService(db, clock, config ?? new ConfigurationBuilder().Build(), sender ?? new NoEmailSender());
        return new Setup(db, clock, new OnboardingService(db, clock), new AuthService(db), accounts);
    }

    private static Task<ServiceResult<RegisteredOwner>> RegisterAsync(Setup s, bool requireEmail, bool requireApproval = false, string? email = null) =>
        s.Onboarding.RegisterAsync(new RegisterRequest("Clinic", Id("ac"), "Dr A", email ?? $"{Id("o")}@example.com", "correct-horse-1",
            RequireEmailConfirmation: requireEmail, RequireApproval: requireApproval));

    [Fact]
    public async Task A_new_owner_cannot_sign_in_until_the_link_is_opened_and_the_link_works_once()
    {
        var s = await SetupAsync();
        var owner = (await RegisterAsync(s, requireEmail: true)).Value!;
        Assert.NotNull(owner.ConfirmationToken);

        var early = await s.Auth.SignInAsync(owner.Email, "correct-horse-1");
        Assert.Equal(FailureKind.PolicyViolation, early.Failure);
        Assert.Equal(Text.EmailNotConfirmed, early.Message);

        // The wrong password still gets the one generic message: nothing is revealed to a stranger.
        Assert.Equal(Text.LoginFailed, (await s.Auth.SignInAsync(owner.Email, "wrong-password")).Message);

        Assert.True((await s.Accounts.ConfirmEmailAsync(owner.ConfirmationToken)).IsSuccess);
        Assert.True((await s.Auth.SignInAsync(owner.Email, "correct-horse-1")).IsSuccess);

        // Used once: the same link is dead now. Unknown and empty tokens fail the same way.
        Assert.False((await s.Accounts.ConfirmEmailAsync(owner.ConfirmationToken)).IsSuccess);
        Assert.False((await s.Accounts.ConfirmEmailAsync("nonsense")).IsSuccess);
        Assert.False((await s.Accounts.ConfirmEmailAsync(null)).IsSuccess);

        // The token is stored only as a hash.
        await using var ctx = s.Db.CreateDbContext();
        var user = await ctx.AdminUsers.SingleAsync(u => u.Id == owner.AdminUserId);
        Assert.Null(user.EmailTokenHash);
    }

    [Fact]
    public async Task The_link_expires_after_48_hours_and_a_resend_replaces_the_old_link()
    {
        var s = await SetupAsync();
        var owner = (await RegisterAsync(s, requireEmail: true)).Value!;

        s.Clock.Now = BaseNow.AddHours(49);
        Assert.False((await s.Accounts.ConfirmEmailAsync(owner.ConfirmationToken)).IsSuccess);

        var fresh = await s.Accounts.IssueConfirmationAsync(owner.Email.ToUpperInvariant()); // the address is matched without case
        Assert.NotNull(fresh);
        Assert.NotEqual(owner.ConfirmationToken, fresh!.Token);
        Assert.False((await s.Accounts.ConfirmEmailAsync(owner.ConfirmationToken)).IsSuccess); // the old link is gone
        Assert.True((await s.Accounts.ConfirmEmailAsync(fresh.Token)).IsSuccess);

        // Nothing to resend for a confirmed owner or an address nobody has, and the answer does not tell which.
        Assert.Null(await s.Accounts.IssueConfirmationAsync(owner.Email));
        Assert.Null(await s.Accounts.IssueConfirmationAsync("nobody@example.com"));
    }

    [Fact]
    public async Task Without_the_requirement_the_owner_is_confirmed_at_once()
    {
        var s = await SetupAsync();
        var owner = (await RegisterAsync(s, requireEmail: false)).Value!;
        Assert.Null(owner.ConfirmationToken);
        Assert.True((await s.Auth.SignInAsync(owner.Email, "correct-horse-1")).IsSuccess);
    }

    [Fact]
    public async Task A_shop_switched_off_cannot_sign_in_or_take_online_bookings_and_switching_it_on_restores_both()
    {
        var s = await SetupAsync();
        var owner = (await RegisterAsync(s, requireEmail: false)).Value!;
        var bookings = new BookingService(s.Db, s.Clock);
        var catalog = new ShopCatalogService(s.Db, s.Clock);
        await using var ctx = s.Db.CreateDbContext();
        var service = await ctx.Services.Where(x => x.BusinessId == owner.BusinessId).Select(x => x.Id).SingleAsync();

        Assert.True((await s.Accounts.SetEnabledAsync(owner.BusinessId, false)).IsSuccess);
        Assert.False(await s.Accounts.IsEnabledAsync(owner.BusinessId));
        Assert.Equal(Text.ShopDisabled, (await s.Auth.SignInAsync(owner.Email, "correct-horse-1")).Message);
        Assert.False((await catalog.GetBySlugAsync(owner.Slug))!.AcceptingBookings);

        var refused = await bookings.CreateAsync(new CreateBookingRequest(owner.BusinessId, service, null, Manila(5, 10), "Maria",
            "09175550081", null, Id("k"), BookingActor.Customer("Maria")));
        Assert.Equal(FailureKind.PolicyViolation, refused.Failure);

        Assert.True((await s.Accounts.SetEnabledAsync(owner.BusinessId, true)).IsSuccess);
        Assert.True((await s.Auth.SignInAsync(owner.Email, "correct-horse-1")).IsSuccess);
        Assert.True((await catalog.GetBySlugAsync(owner.Slug))!.AcceptingBookings);
        Assert.Equal(FailureKind.NotFound, (await s.Accounts.SetEnabledAsync(Guid.NewGuid(), false)).Failure);
    }

    [Fact]
    public async Task With_approval_required_a_new_shop_starts_switched_off()
    {
        var s = await SetupAsync();
        var owner = (await RegisterAsync(s, requireEmail: false, requireApproval: true)).Value!;
        Assert.False(owner.Enabled);
        Assert.False(await s.Accounts.IsEnabledAsync(owner.BusinessId));
        await s.Accounts.SetEnabledAsync(owner.BusinessId, true);
        Assert.True((await s.Auth.SignInAsync(owner.Email, "correct-horse-1")).IsSuccess);
    }

    [Fact]
    public async Task Only_a_confirmed_owner_listed_in_the_platform_emails_is_the_platform_owner()
    {
        var admin = $"{Id("boss")}@example.com";
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Platform:AdminEmails"] = $"someone@else.com, {admin.ToUpperInvariant()}" }).Build();
        var s = await SetupAsync(config);
        var boss = (await RegisterAsync(s, requireEmail: false, email: admin)).Value!;
        var other = (await RegisterAsync(s, requireEmail: false)).Value!;
        var unconfirmed = (await RegisterAsync(s, requireEmail: true)).Value!;

        Assert.True(await s.Accounts.IsPlatformAdminAsync(boss.AdminUserId));
        Assert.False(await s.Accounts.IsPlatformAdminAsync(other.AdminUserId));
        Assert.False(await s.Accounts.IsPlatformAdminAsync(null));
        Assert.False(await new AccountControlService(s.Db, s.Clock, new ConfigurationBuilder().Build(), new NoEmailSender()).IsPlatformAdminAsync(boss.AdminUserId)); // nobody listed

        var shops = await s.Accounts.ListShopsAsync();
        Assert.True(shops.Single(x => x.BusinessId == boss.BusinessId).EmailConfirmed);
        Assert.False(shops.Single(x => x.BusinessId == unconfirmed.BusinessId).EmailConfirmed);

        Assert.True((await s.Accounts.ConfirmManuallyAsync(unconfirmed.BusinessId)).IsSuccess);
        Assert.True((await s.Auth.SignInAsync(unconfirmed.Email, "correct-horse-1")).IsSuccess);
        Assert.Equal(FailureKind.NotFound, (await s.Accounts.ConfirmManuallyAsync(unconfirmed.BusinessId)).Failure); // already confirmed
    }

    [Fact]
    public async Task The_confirmation_email_carries_a_working_link()
    {
        var sender = new CapturingEmailSender();
        var s = await SetupAsync(sender: sender);
        var owner = (await RegisterAsync(s, requireEmail: true)).Value!;

        Assert.True(await s.Accounts.SendConfirmationAsync(new IssuedConfirmation(owner.ConfirmationToken!, owner.Email, owner.DisplayName), "https://flow.example.com/"));

        var mail = sender.Sent.Single();
        Assert.Equal(owner.Email, mail.To);
        Assert.Contains($"https://flow.example.com/ConfirmEmail?token={Uri.EscapeDataString(owner.ConfirmationToken!)}", mail.Body);
        Assert.False(await new AccountControlService(s.Db, s.Clock, new ConfigurationBuilder().Build(), new NoEmailSender())
            .SendConfirmationAsync(new IssuedConfirmation("t", "a@b.c", "A"), "https://x")); // no email service: not sent
    }

    // ------------------------------------------------------------ in the running app

    private static HttpClient ClientWith(AppFactory factory, IEmailSender sender) =>
        factory.WithWebHostBuilder(b => b.ConfigureServices(s => s.AddSingleton(sender)))
            .CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = true });

    private static Dictionary<string, string> Form(string email, string slug) => new()
    {
        ["Input.BusinessName"] = "Dr Santos Clinic", ["Input.Slug"] = slug, ["Input.OwnerName"] = "Dr Santos",
        ["Input.Email"] = email, ["Input.Password"] = "correct-horse-1", ["Input.Industry"] = "clinic",
    };

    [Fact]
    public async Task Signing_up_sends_a_link_does_not_sign_in_and_opening_the_link_allows_sign_in()
    {
        var (factory, _) = await HttpApp.StartAsync(requireEmailConfirmation: true);
        using var _f = factory;
        var sender = new CapturingEmailSender();
        var client = ClientWith(factory, sender);
        var email = $"{Id("dr")}@example.com";

        var signup = await HttpApp.PostFormAsync(client, "/Register", Form(email, Id("sl")));
        Assert.Equal(HttpStatusCode.Redirect, signup.StatusCode);
        Assert.Equal("/CheckEmail", signup.Headers.Location!.OriginalString);

        // Not signed in: the back office sends the visitor to the sign-in page.
        var admin = await client.GetAsync("/Admin");
        Assert.Equal(HttpStatusCode.Redirect, admin.StatusCode);
        Assert.Contains("/Login", admin.Headers.Location!.OriginalString);
        Assert.Contains(email, WebUtility.HtmlDecode(await client.GetStringAsync("/CheckEmail")));

        // Signing in before confirming is refused with the reason, and offers to send the link again.
        var tooEarly = await HttpApp.PostFormAsync(client, "/Login", new() { ["Input.Email"] = email, ["Input.Password"] = "correct-horse-1" });
        Assert.Equal(HttpStatusCode.OK, tooEarly.StatusCode);
        var tooEarlyHtml = WebUtility.HtmlDecode(await tooEarly.Content.ReadAsStringAsync());
        Assert.Contains(Text.EmailNotConfirmed, tooEarlyHtml);
        Assert.Contains(Text.ConfirmResend, tooEarlyHtml);

        // The email holds the link; opening it confirms the account.
        var mail = Assert.Single(sender.Sent);
        var link = mail.Body.Split('\n').Single(l => l.Contains("/ConfirmEmail?token="));
        var path = new Uri(link.Trim()).PathAndQuery;
        var confirmed = WebUtility.HtmlDecode(await client.GetStringAsync(path));
        Assert.Contains(Text.ConfirmDone, confirmed);
        Assert.Contains(Text.ConfirmLinkInvalid, WebUtility.HtmlDecode(await client.GetStringAsync(path))); // single use

        var signedIn = await HttpApp.PostFormAsync(client, "/Login", new() { ["Input.Email"] = email, ["Input.Password"] = "correct-horse-1" });
        Assert.Equal(HttpStatusCode.Redirect, signedIn.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/Admin")).StatusCode);
    }

    [Fact]
    public async Task Resend_answers_the_same_for_any_address_and_only_mails_a_real_waiting_account()
    {
        var (factory, _) = await HttpApp.StartAsync(requireEmailConfirmation: true);
        using var _f = factory;
        var sender = new CapturingEmailSender();
        var client = ClientWith(factory, sender);
        var email = $"{Id("dr")}@example.com";
        await HttpApp.PostFormAsync(client, "/Register", Form(email, Id("sl")));
        sender.Sent.Clear();

        var real = await HttpApp.PostFormAsync(client, "/CheckEmail?handler=Resend", new() { ["email"] = email }, tokenPage: "/CheckEmail");
        var unknown = await HttpApp.PostFormAsync(client, "/CheckEmail?handler=Resend", new() { ["email"] = "nobody@example.com" }, tokenPage: "/CheckEmail");

        Assert.Equal(HttpStatusCode.OK, real.StatusCode);
        Assert.Equal(HttpStatusCode.OK, unknown.StatusCode);
        Assert.Contains(Text.ConfirmResent, WebUtility.HtmlDecode(await real.Content.ReadAsStringAsync()));
        Assert.Contains(Text.ConfirmResent, WebUtility.HtmlDecode(await unknown.Content.ReadAsStringAsync()));
        Assert.Equal(email, Assert.Single(sender.Sent).To); // only the real, waiting account got an email
    }

    [Fact]
    public async Task The_platform_page_is_hidden_from_everyone_but_the_platform_owner_who_can_switch_shops_off_and_on()
    {
        var bossEmail = $"{Id("boss")}@example.com";
        var (factory, _) = await HttpApp.StartAsync(platformAdminEmails: bossEmail);
        using var _f = factory;
        var boss = HttpApp.NewClient(factory);
        var bossReg = await HttpApp.PostFormAsync(boss, "/Register", Form(bossEmail, Id("bs")));
        Assert.Equal(HttpStatusCode.Redirect, bossReg.StatusCode);

        var tenant = HttpApp.NewClient(factory);
        var tenantEmail = $"{Id("shop")}@example.com";
        var tenantSlug = Id("ts");
        await HttpApp.PostFormAsync(tenant, "/Register", Form(tenantEmail, tenantSlug));

        // A signed-in shop owner who is not the platform owner finds nothing there; neither does a visitor who is not signed in.
        Assert.Equal(HttpStatusCode.NotFound, (await tenant.GetAsync("/Platform")).StatusCode);
        var visitor = await HttpApp.NewClient(factory).GetAsync("/Platform");
        Assert.Equal(HttpStatusCode.Redirect, visitor.StatusCode);
        Assert.Contains("/Login", visitor.Headers.Location!.OriginalString);

        var page = WebUtility.HtmlDecode(await boss.GetStringAsync("/Platform"));
        Assert.Contains(tenantEmail, page);
        Assert.Contains($"/b/{tenantSlug}", page);

        // Switch the shop off: its owner is signed out on the next click, and the booking page closes.
        await using var ctx = new TestDbFactory(await TestDb.ConnectionStringAsync()).CreateDbContext();
        var tenantShop = await ctx.Businesses.SingleAsync(b => b.Slug == tenantSlug);
        var off = await HttpApp.PostFormAsync(boss, "/Platform?handler=Enable", new() { ["id"] = tenantShop.Id.ToString(), ["on"] = "false" }, tokenPage: "/Platform");
        Assert.Equal(HttpStatusCode.Redirect, off.StatusCode);

        var blocked = await tenant.GetAsync("/Admin");
        Assert.Equal(HttpStatusCode.Redirect, blocked.StatusCode);
        Assert.Contains("/Login?disabled=true", blocked.Headers.Location!.OriginalString);
        Assert.Contains(Text.ShopDisabled, WebUtility.HtmlDecode(await tenant.GetStringAsync("/Login?disabled=true")));
        Assert.Contains(Text.TrialEndedPublic, WebUtility.HtmlDecode(await tenant.GetStringAsync($"/b/{tenantSlug}")));
        var signIn = await HttpApp.PostFormAsync(tenant, "/Login", new() { ["Input.Email"] = tenantEmail, ["Input.Password"] = "correct-horse-1" });
        Assert.Contains(Text.ShopDisabled, WebUtility.HtmlDecode(await signIn.Content.ReadAsStringAsync()));

        // Switch it back on.
        await HttpApp.PostFormAsync(boss, "/Platform?handler=Enable", new() { ["id"] = tenantShop.Id.ToString(), ["on"] = "true" }, tokenPage: "/Platform");
        var back = await HttpApp.PostFormAsync(tenant, "/Login", new() { ["Input.Email"] = tenantEmail, ["Input.Password"] = "correct-horse-1" });
        Assert.Equal(HttpStatusCode.Redirect, back.StatusCode);

        // The platform owner cannot switch off their own shop (they would be locked out).
        var ownShop = await ctx.Businesses.SingleAsync(b => b.Id == ctx.AdminUsers.Where(u => u.Email == bossEmail).Select(u => u.BusinessId).First());
        await HttpApp.PostFormAsync(boss, "/Platform?handler=Enable", new() { ["id"] = ownShop.Id.ToString(), ["on"] = "false" }, tokenPage: "/Platform");
        Assert.True(await new AccountControlService(new TestDbFactory(await TestDb.ConnectionStringAsync()), TimeProvider.System,
            new ConfigurationBuilder().Build(), new NoEmailSender()).IsEnabledAsync(ownShop.Id));
    }
}
