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
    public List<(string To, string Subject, string Body, string? Html)> Sent { get; } = [];

    public Task<bool> SendAsync(string to, string subject, string body, string? html = null, CancellationToken ct = default)
    {
        Sent.Add((to, subject, body, html));
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
        var service = await ctx.Services.Where(x => x.BusinessId == owner.BusinessId && !x.IsOther).Select(x => x.Id).SingleAsync();

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
    public async Task The_backup_reminder_remembers_when_a_copy_was_saved_and_counts_the_days()
    {
        var s = await SetupAsync();
        await using (var ctx = s.Db.CreateDbContext())
            await ctx.PlatformSettings.Where(x => x.Key == "backup_copy_saved").ExecuteDeleteAsync(); // the test database is shared
        Assert.Null(await s.Accounts.GetBackupCopySavedAsync());
        Assert.Null(AccountControlService.DaysSince(null, s.Clock.GetUtcNow().UtcDateTime));

        await s.Accounts.MarkBackupCopySavedAsync();
        var saved = await s.Accounts.GetBackupCopySavedAsync();
        Assert.Equal(s.Clock.GetUtcNow().UtcDateTime, saved!.Value, TimeSpan.FromSeconds(1));
        Assert.Equal(0, AccountControlService.DaysSince(saved, s.Clock.GetUtcNow().UtcDateTime));
        Assert.Equal(31, AccountControlService.DaysSince(saved, saved.Value.AddDays(31).AddHours(3)));

        s.Clock.Now = s.Clock.Now.AddDays(5);
        await s.Accounts.MarkBackupCopySavedAsync(); // pressing the button again moves the date, it does not add a row
        Assert.Equal(s.Clock.GetUtcNow().UtcDateTime, (await s.Accounts.GetBackupCopySavedAsync())!.Value, TimeSpan.FromSeconds(1));
        await using var check = s.Db.CreateDbContext();
        Assert.Equal(1, await check.PlatformSettings.CountAsync(x => x.Key == "backup_copy_saved"));
    }

    [Fact]
    public async Task The_platform_page_shows_the_backup_card_and_only_the_platform_owner_can_record_a_saved_copy()
    {
        var bossEmail = $"{Id("boss")}@example.com";
        var (factory, _) = await HttpApp.StartAsync(platformAdminEmails: bossEmail);
        using var _f = factory;
        var boss = HttpApp.NewClient(factory);
        await HttpApp.PostFormAsync(boss, "/Register", Form(bossEmail, Id("bs")));
        var tenant = HttpApp.NewClient(factory);
        await HttpApp.PostFormAsync(tenant, "/Register", Form($"{Id("shop")}@example.com", Id("ts")));

        var page = WebUtility.HtmlDecode(await boss.GetStringAsync("/Platform"));
        Assert.Contains(Text.PlatformBackupTitle, page);
        Assert.Contains("href=\"https://github.com/archiearitalla/E-Booking/actions/workflows/backup.yml\"", page);
        Assert.Contains($"data-confirm=\"{Text.PlatformBackupAsk}\"", page);

        var marked = await HttpApp.PostFormAsync(boss, "/Platform?handler=BackupSaved", new(), tokenPage: "/Platform");
        Assert.Equal(HttpStatusCode.Redirect, marked.StatusCode);
        Assert.Contains("Last copy saved", WebUtility.HtmlDecode(await boss.GetStringAsync("/Platform")));

        var other = await HttpApp.PostFormAsync(tenant, "/Platform?handler=BackupSaved", new(), tokenPage: "/Admin");
        Assert.Equal(HttpStatusCode.NotFound, other.StatusCode); // a shop owner finds nothing there
    }

    [Fact]
    public async Task The_platform_owner_can_extend_a_shops_access_from_the_later_of_today_and_the_current_end()
    {
        var s = await SetupAsync();
        var owner = (await RegisterAsync(s, requireEmail: false)).Value!;
        Assert.Equal(FailureKind.PolicyViolation, (await s.Accounts.ExtendAccessAsync(owner.BusinessId, 30)).Failure); // no end date yet

        var end = s.Clock.GetUtcNow().UtcDateTime.AddDays(30);
        await using (var ctx = s.Db.CreateDbContext())
            await ctx.Businesses.Where(b => b.Id == owner.BusinessId).ExecuteUpdateAsync(u => u.SetProperty(b => b.TrialEndsAtUtc, end));
        var shop = () => s.Accounts.ListShopsAsync().ContinueWith(t => t.Result.Single(x => x.BusinessId == owner.BusinessId));

        var first = await s.Accounts.ExtendAccessAsync(owner.BusinessId, 30);
        Assert.True(first.IsSuccess);
        Assert.Equal(end.AddDays(30), first.Value, TimeSpan.FromMinutes(1));

        // After the end date has passed, the new period counts from today.
        s.Clock.Now = s.Clock.Now.AddDays(200);
        var late = await s.Accounts.ExtendAccessAsync(owner.BusinessId, 7);
        Assert.Equal(s.Clock.GetUtcNow().UtcDateTime.AddDays(7), late.Value, TimeSpan.FromMinutes(1));
        Assert.Equal(late.Value, (await shop()).TrialEndsAtUtc!.Value, TimeSpan.FromMinutes(1));

        Assert.Equal(FailureKind.Validation, (await s.Accounts.ExtendAccessAsync(owner.BusinessId, 5)).Failure); // not one of the choices
        Assert.Equal(FailureKind.NotFound, (await s.Accounts.ExtendAccessAsync(Guid.NewGuid(), 30)).Failure);
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

    private static async Task<int> RowsOfAsync(TestDbFactory db, Guid businessId)
    {
        await using var ctx = db.CreateDbContext();
        return await ctx.Businesses.CountAsync(x => x.Id == businessId) + await ctx.AdminUsers.CountAsync(x => x.BusinessId == businessId)
            + await ctx.StaffMembers.CountAsync(x => x.BusinessId == businessId) + await ctx.Services.CountAsync(x => x.BusinessId == businessId)
            + await ctx.StaffServices.CountAsync(x => x.BusinessId == businessId) + await ctx.Customers.CountAsync(x => x.BusinessId == businessId)
            + await ctx.Bookings.CountAsync(x => x.BusinessId == businessId) + await ctx.AuditLogs.CountAsync(x => x.BusinessId == businessId)
            + await ctx.BusinessHours.CountAsync(x => x.BusinessId == businessId) + await ctx.WeeklyClosures.CountAsync(x => x.BusinessId == businessId)
            + await ctx.Blackouts.CountAsync(x => x.BusinessId == businessId)
            + await ctx.WorkingHours.CountAsync(h => ctx.StaffMembers.Any(s => s.Id == h.StaffId && s.BusinessId == businessId));
    }

    [Fact]
    public async Task Deleting_a_shop_removes_everything_in_it_only_with_the_typed_address_and_never_touches_another_shop()
    {
        var s = await SetupAsync();
        var doomed = (await RegisterAsync(s, requireEmail: false)).Value!;
        var keeper = (await RegisterAsync(s, requireEmail: false)).Value!;
        var me = (await RegisterAsync(s, requireEmail: false)).Value!; // the platform owner's own shop

        // Give the doomed shop real data: a completed booking with a follow-up and a record, a closure, a day off and an audit trail.
        await using (var ctx = s.Db.CreateDbContext())
        {
            var service = await ctx.Services.Where(x => x.BusinessId == doomed.BusinessId && !x.IsOther).Select(x => x.Id).SingleAsync();
            var staff = await ctx.StaffMembers.Where(x => x.BusinessId == doomed.BusinessId).Select(x => x.Id).SingleAsync();
            var bookings = new BookingService(s.Db, s.Clock);
            var booked = (await bookings.CreateAsync(new CreateBookingRequest(doomed.BusinessId, service, staff, Manila(5, 10), "Maria",
                "09175550101", "maria@example.com", Id("k"), BookingActor.Customer("Maria")))).Value!.Booking;
            s.Clock.Now = new DateTimeOffset(Manila(5, 10, 10), TimeSpan.Zero);
            Assert.True((await bookings.TransitionAsync(doomed.BusinessId, booked.Id, VeskaFlow.Core.Domain.BookingStatus.Completed,
                BookingActor.Staff(doomed.AdminUserId, "Dr"))).IsSuccess);
            await new VisitRecordService(s.Db).SetNotesAsync(doomed.BusinessId, booked.Id, "Allergic to penicillin");
            await new FollowUpService(s.Db, s.Clock).SetAsync(doomed.BusinessId, booked.Id, new DateOnly(2030, 4, 5), null);
            var settings = new ShopSettingsService(s.Db, s.Clock);
            await settings.AddWeeklyClosureAsync(doomed.BusinessId, new WeeklyClosureInput(null, 0, "Day off"));
            await settings.AddClosureAsync(doomed.BusinessId, new ClosureInput(null, new DateOnly(2030, 5, 1), new DateOnly(2030, 5, 2), "Holiday"));
        }
        var before = new { Doomed = await RowsOfAsync(s.Db, doomed.BusinessId), Keeper = await RowsOfAsync(s.Db, keeper.BusinessId) };
        Assert.True(before.Doomed > 15);

        // Refused: wrong or empty confirmation, the shop you are in, an unknown shop. Nothing changes.
        var wrong = await s.Accounts.DeleteShopAsync(doomed.BusinessId, "not-the-address", me.BusinessId);
        Assert.Equal(FailureKind.Validation, wrong.Failure);
        Assert.Contains("confirm", wrong.FieldErrors.Keys);
        Assert.Equal(FailureKind.Validation, (await s.Accounts.DeleteShopAsync(doomed.BusinessId, "", me.BusinessId)).Failure);
        Assert.Equal(FailureKind.PolicyViolation, (await s.Accounts.DeleteShopAsync(me.BusinessId, me.Slug, me.BusinessId)).Failure);
        Assert.Equal(FailureKind.NotFound, (await s.Accounts.DeleteShopAsync(Guid.NewGuid(), "x", me.BusinessId)).Failure);
        Assert.Equal(before.Doomed, await RowsOfAsync(s.Db, doomed.BusinessId));

        // Allowed: the typed address (any letter case, spaces around it ignored).
        Assert.True((await s.Accounts.DeleteShopAsync(doomed.BusinessId, $"  {doomed.Slug.ToUpperInvariant()} ", me.BusinessId)).IsSuccess);

        Assert.Equal(0, await RowsOfAsync(s.Db, doomed.BusinessId)); // every table, including the owner's account
        Assert.Equal(before.Keeper, await RowsOfAsync(s.Db, keeper.BusinessId));
        Assert.True(await RowsOfAsync(s.Db, me.BusinessId) > 0);

        // The address and the email can be used again.
        var again = await s.Onboarding.RegisterAsync(new RegisterRequest("Clinic", doomed.Slug, "Dr A", doomed.Email, "correct-horse-1"));
        Assert.True(again.IsSuccess, again.Message);
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

    // ------------------------------------------------------------ Brevo (the same service the veskacode.com form uses)

    private sealed class RecordingHandler(HttpStatusCode status, bool fail = false) : HttpMessageHandler
    {
        public List<(string Url, string? ApiKey, string Body)> Calls { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls.Add((request.RequestUri!.ToString(), request.Headers.TryGetValues("api-key", out var key) ? key.Single() : null,
                request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct)));
            if (fail) throw new HttpRequestException("network down");
            return new HttpResponseMessage(status);
        }
    }

    private static IConfiguration BrevoConfig(bool configured = true) => new ConfigurationBuilder().AddInMemoryCollection(
        new Dictionary<string, string?>
        {
            ["Email:BrevoApiKey"] = configured ? "secret-key" : "", ["Email:From"] = configured ? "me@example.com" : "", ["Email:FromName"] = "VeskaFlow",
        }).Build();

    [Fact]
    public async Task Brevo_gets_the_request_it_expects_and_a_delivery_problem_is_a_false_never_an_exception()
    {
        var ok = new RecordingHandler(HttpStatusCode.Created);
        var sender = new BrevoEmailSender(new HttpClient(ok), BrevoConfig(), Microsoft.Extensions.Logging.Abstractions.NullLogger<BrevoEmailSender>.Instance);
        Assert.True(sender.IsConfigured);
        Assert.True(await sender.SendAsync("owner@example.com", "Confirm", "plain text", "<b>html</b>"));

        var call = Assert.Single(ok.Calls);
        Assert.Equal("https://api.brevo.com/v3/smtp/email", call.Url);
        Assert.Equal("secret-key", call.ApiKey);
        Assert.Contains("\"email\":\"me@example.com\"", call.Body);
        Assert.Contains("\"name\":\"VeskaFlow\"", call.Body);
        Assert.Contains("\"to\":[{\"email\":\"owner@example.com\"}]", call.Body);
        Assert.Contains("\"textContent\":\"plain text\"", call.Body);
        Assert.Contains("\"htmlContent\":\"\\u003Cb\\u003Ehtml\\u003C/b\\u003E\"", call.Body);

        var refused = new BrevoEmailSender(new HttpClient(new RecordingHandler(HttpStatusCode.BadRequest)), BrevoConfig(), Microsoft.Extensions.Logging.Abstractions.NullLogger<BrevoEmailSender>.Instance);
        Assert.False(await refused.SendAsync("owner@example.com", "Confirm", "x"));

        var down = new BrevoEmailSender(new HttpClient(new RecordingHandler(HttpStatusCode.OK, fail: true)), BrevoConfig(), Microsoft.Extensions.Logging.Abstractions.NullLogger<BrevoEmailSender>.Instance);
        Assert.False(await down.SendAsync("owner@example.com", "Confirm", "x"));
    }

    [Fact]
    public async Task Brevo_without_a_key_or_sender_sends_nothing()
    {
        var handler = new RecordingHandler(HttpStatusCode.Created);
        var sender = new BrevoEmailSender(new HttpClient(handler), BrevoConfig(configured: false), Microsoft.Extensions.Logging.Abstractions.NullLogger<BrevoEmailSender>.Instance);

        Assert.False(sender.IsConfigured);
        Assert.False(await sender.SendAsync("owner@example.com", "Confirm", "x"));
        Assert.Empty(handler.Calls);
        await Task.CompletedTask;
    }

    // ------------------------------------------------------------ in the running app

    private static HttpClient ClientWith(AppFactory factory, IEmailSender sender) =>
        factory.WithWebHostBuilder(b => b.ConfigureServices(s => s.AddSingleton(sender)))
            .CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = true });

    private static Dictionary<string, string> Form(string email, string slug) => new()
    {
        ["Input.Accept"] = "true", ["Input.BusinessName"] = "Dr Santos Clinic", ["Input.Slug"] = slug, ["Input.OwnerName"] = "Dr Santos",
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

        // The "check your email" page asks the server whether the link was opened; the address never appears in the URL.
        Assert.Contains("\"confirmed\":false", await client.GetStringAsync("/CheckEmail?handler=Status"));
        Assert.Contains("data-poll-confirmed", await client.GetStringAsync("/CheckEmail"));

        // The email holds the link (and a button for email programs that show HTML); opening it confirms the account.
        var mail = Assert.Single(sender.Sent);
        Assert.Contains("Confirm my email", mail.Html);
        var link = mail.Body.Split('\n').Single(l => l.Contains("/ConfirmEmail?token="));
        var path = new Uri(link.Trim()).PathAndQuery;
        var confirmed = WebUtility.HtmlDecode(await client.GetStringAsync(path));
        Assert.Contains(Text.ConfirmDone, confirmed);
        Assert.Contains("\"confirmed\":true", await client.GetStringAsync("/CheckEmail?handler=Status")); // the page moves on by itself
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

        // Deleting: the page asks for the typed address (and a confirm window); the own shop has no delete at all.
        var withDelete = WebUtility.HtmlDecode(await boss.GetStringAsync("/Platform"));
        Assert.Contains($"name=\"confirm\"", withDelete);
        Assert.Contains($"data-confirm=\"{Text.PlatformDeleteAsk}\"", withDelete);
        var wrongDelete = await HttpApp.PostFormAsync(boss, "/Platform?handler=Delete", new() { ["id"] = tenantShop.Id.ToString(), ["confirm"] = "nope" }, tokenPage: "/Platform");
        Assert.Equal(HttpStatusCode.Redirect, wrongDelete.StatusCode);
        Assert.Contains(Text.PlatformDeleteMismatch, WebUtility.HtmlDecode(await boss.GetStringAsync("/Platform")));
        Assert.True(await ctx.Businesses.AsNoTracking().AnyAsync(b => b.Id == tenantShop.Id));

        // The platform owner cannot switch off their own shop (they would be locked out).
        var ownShop = await ctx.Businesses.SingleAsync(b => b.Id == ctx.AdminUsers.Where(u => u.Email == bossEmail).Select(u => u.BusinessId).First());
        await HttpApp.PostFormAsync(boss, "/Platform?handler=Enable", new() { ["id"] = ownShop.Id.ToString(), ["on"] = "false" }, tokenPage: "/Platform");
        Assert.True(await new AccountControlService(new TestDbFactory(await TestDb.ConnectionStringAsync()), TimeProvider.System,
            new ConfigurationBuilder().Build(), new NoEmailSender()).IsEnabledAsync(ownShop.Id));

        // The own shop has no delete form, and a posted delete for it is refused too.
        var latest = WebUtility.HtmlDecode(await boss.GetStringAsync("/Platform"));
        Assert.DoesNotContain($"confirm-{ownShop.Id}", latest);
        await HttpApp.PostFormAsync(boss, "/Platform?handler=Delete", new() { ["id"] = ownShop.Id.ToString(), ["confirm"] = ownShop.Slug }, tokenPage: "/Platform");
        Assert.True(await ctx.Businesses.AsNoTracking().AnyAsync(b => b.Id == ownShop.Id));
        Assert.Contains(Text.PlatformCannotDeleteOwn, WebUtility.HtmlDecode(await boss.GetStringAsync("/Platform")));

        // With the typed address the shop and its owner's account are gone for good.
        var gone = await HttpApp.PostFormAsync(boss, "/Platform?handler=Delete", new() { ["id"] = tenantShop.Id.ToString(), ["confirm"] = tenantSlug }, tokenPage: "/Platform");
        Assert.Equal(HttpStatusCode.Redirect, gone.StatusCode);
        Assert.Contains(Text.PlatformDeleted, WebUtility.HtmlDecode(await boss.GetStringAsync("/Platform")));
        Assert.False(await ctx.Businesses.AsNoTracking().AnyAsync(b => b.Id == tenantShop.Id));
        Assert.False(await ctx.AdminUsers.AsNoTracking().AnyAsync(u => u.Email == tenantEmail));
        Assert.Equal(HttpStatusCode.NotFound, (await HttpApp.NewClient(factory).GetAsync($"/b/{tenantSlug}")).StatusCode);
    }
}
