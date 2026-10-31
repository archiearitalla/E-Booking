using System.Net;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using VeskaFlow.Core.Domain;
using VeskaFlow.Web.Services;
using static VeskaFlow.Web.Tests.Seed;

namespace VeskaFlow.Web.Tests;

/// <summary>"Require email confirmation to book" (BUSINESS_RULES 8.8): the booking is held until the emailed link is pressed.</summary>
[Collection("http")]
public class EmailConfirmTests
{
    private static string Id(string prefix) => prefix + Guid.NewGuid().ToString("N")[..10];

    private sealed record Ctx(TestDbFactory Db, TestClock Clock, BookingService Bookings, CustomerMailer Mailer, CapturingEmailSender Sender, Shop Shop);

    private static async Task<Ctx> SetupAsync(bool requireConfirm = true)
    {
        var db = new TestDbFactory(await TestDb.ConnectionStringAsync());
        var clock = new TestClock(BaseNow);
        var shop = await CreateShopAsync();
        await using (var ctx = db.CreateDbContext())
        {
            var day = DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime);
            await ctx.EmailDailies.Where(e => e.Day == day).ExecuteDeleteAsync();
            await ctx.Businesses.Where(b => b.Id == shop.BusinessId).ExecuteUpdateAsync(u => u.SetProperty(b => b.RequireEmailConfirm, requireConfirm));
        }
        var sender = new CapturingEmailSender();
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            { ["Email:DailyLimit"] = "100000", ["Email:FreeShopDailyMax"] = "1000" }).Build();
        var mailer = new CustomerMailer(sender, new EmailQuota(db, clock, config), db, NullLogger<CustomerMailer>.Instance);
        return new Ctx(db, clock, new BookingService(db, clock), mailer, sender, shop);
    }

    private static CreateBookingRequest Req(Ctx c, DateTime start, string? email, string? phone = null, BookingActor? actor = null) =>
        Request(c.Shop, start, phone, actor: actor) with { Email = email };

    [Fact]
    public async Task With_the_switch_off_a_booking_is_confirmed_at_once_as_before()
    {
        var c = await SetupAsync(requireConfirm: false);
        var made = (await c.Bookings.CreateAsync(Req(c, Manila(5, 10), null))).Value!;
        Assert.Equal(BookingStatus.Confirmed, made.Booking.Status);
        Assert.Null(made.EmailConfirmToken);
    }

    [Fact]
    public async Task With_the_switch_on_an_email_is_needed_and_the_booking_is_held_and_still_blocks_the_time()
    {
        var c = await SetupAsync();
        var noEmail = await c.Bookings.CreateAsync(Req(c, Manila(5, 10), null));
        Assert.Equal(FailureKind.Validation, noEmail.Failure);
        Assert.Equal(Text.EmailRequiredToBook, noEmail.FieldErrors["email"]);

        var made = (await c.Bookings.CreateAsync(Req(c, Manila(5, 10), "maria@example.com"))).Value!;
        Assert.Equal(BookingStatus.Held, made.Booking.Status);
        Assert.False(string.IsNullOrEmpty(made.EmailConfirmToken));
        await using (var ctx = c.Db.CreateDbContext())
        {
            var row = await ctx.Bookings.SingleAsync(b => b.Id == made.Booking.Id);
            Assert.Equal(BaseNow.UtcDateTime.AddMinutes(20), row.HoldExpiresAtUtc);
            Assert.NotEqual(made.EmailConfirmToken, row.EmailConfirmHash); // only a hash is stored
            Assert.Equal(64, row.EmailConfirmHash!.Length);
            var audit = await ctx.AuditLogs.SingleAsync(a => a.BookingId == made.Booking.Id && a.Action == "CREATED");
            Assert.Equal("HELD", audit.ToStatus);
        }

        // The held time is taken: nobody else can book it, and it is not offered.
        Assert.Equal(FailureKind.SlotTaken, (await c.Bookings.CreateAsync(Req(c, Manila(5, 10), "other@example.com"))).Failure);
        Assert.DoesNotContain(Manila(5, 10), (await c.Bookings.GetFreeStartsAsync(c.Shop.BusinessId, c.Shop.ServiceId, null, new DateOnly(2030, 3, 5))).Value!);

        // Staff bookings (walk-ins, phone-ins) never need an email and are confirmed at once.
        var staff = (await c.Bookings.CreateAsync(Req(c, Manila(5, 14), null, actor: BookingActor.Staff(Guid.NewGuid(), "Ana")))).Value!;
        Assert.Equal(BookingStatus.Confirmed, staff.Booking.Status);
        Assert.Null(staff.EmailConfirmToken);
    }

    [Fact]
    public async Task The_link_email_is_sent_to_the_customer_and_pressing_it_confirms_once_with_an_audit_row()
    {
        var c = await SetupAsync();
        var made = (await c.Bookings.CreateAsync(Req(c, Manila(5, 10), "maria@example.com"))).Value!;
        await using (var ctx = c.Db.CreateDbContext()) // the shop's own "email my customers" switch does not stop this email
            await ctx.Businesses.Where(b => b.Id == c.Shop.BusinessId).ExecuteUpdateAsync(u => u.SetProperty(b => b.CustomerEmails, false));

        Assert.Equal(MailResult.Sent, await c.Mailer.SendConfirmLinkAsync(c.Shop.BusinessId, made.Booking.Id, "https://flow.example.com/", made.EmailConfirmToken!));
        var mail = Assert.Single(c.Sender.Sent);
        Assert.Equal("maria@example.com", mail.To);
        Assert.Contains($"https://flow.example.com/confirm-booking/{made.EmailConfirmToken}", mail.Body);
        Assert.Contains($"https://flow.example.com/booking/{made.Booking.ManageToken}", mail.Body); // to see or cancel it

        Assert.Equal(BookingStatus.Held, (await c.Bookings.GetByEmailTokenAsync(made.EmailConfirmToken))!.Booking.Status);
        var confirmed = await c.Bookings.ConfirmByEmailAsync(made.EmailConfirmToken);
        Assert.True(confirmed.IsSuccess, confirmed.Message);
        Assert.Equal(BookingStatus.Confirmed, confirmed.Value!.Status);
        Assert.True((await c.Bookings.ConfirmByEmailAsync(made.EmailConfirmToken)).IsSuccess); // pressing it again is fine

        await using var check = c.Db.CreateDbContext();
        var row = await check.Bookings.SingleAsync(b => b.Id == made.Booking.Id);
        Assert.Null(row.HoldExpiresAtUtc);
        Assert.Single(await check.AuditLogs.Where(a => a.BookingId == made.Booking.Id && a.Action == "EMAIL_CONFIRMED").ToListAsync());

        // Only the emailed secret works. The manage link (which the customer sees on screen) and made-up secrets do not.
        Assert.Equal(FailureKind.NotFound, (await c.Bookings.ConfirmByEmailAsync(made.Booking.ManageToken)).Failure);
        Assert.Equal(FailureKind.NotFound, (await c.Bookings.ConfirmByEmailAsync("no-such-secret")).Failure);
        Assert.Equal(FailureKind.NotFound, (await c.Bookings.ConfirmByEmailAsync(null)).Failure);
        Assert.Null(await c.Bookings.GetByEmailTokenAsync(made.Booking.ManageToken));
    }

    [Fact]
    public async Task A_hold_that_ran_out_is_released_and_the_link_says_so()
    {
        var c = await SetupAsync();
        var made = (await c.Bookings.CreateAsync(Req(c, Manila(5, 10), "maria@example.com"))).Value!;

        c.Clock.Now = c.Clock.Now.AddMinutes(21); // past the 20-minute hold
        // Even before anything is cleaned up, the time is offered again and a new customer can take it.
        Assert.Contains(Manila(5, 10), (await c.Bookings.GetFreeStartsAsync(c.Shop.BusinessId, c.Shop.ServiceId, null, new DateOnly(2030, 3, 5))).Value!);

        var late = await c.Bookings.ConfirmByEmailAsync(made.EmailConfirmToken);
        Assert.Equal(FailureKind.PolicyViolation, late.Failure);
        Assert.Equal(Text.ConfirmLinkExpired, late.Message);
        await using (var ctx = c.Db.CreateDbContext())
        {
            var row = await ctx.Bookings.SingleAsync(b => b.Id == made.Booking.Id);
            Assert.Equal(BookingStatus.Cancelled, row.Status);
            Assert.Equal("SYSTEM", row.CancelledBy);
            Assert.Single(await ctx.AuditLogs.Where(a => a.BookingId == made.Booking.Id && a.Action == "HOLD_EXPIRED").ToListAsync());
        }
        Assert.Equal(FailureKind.PolicyViolation, (await c.Bookings.ConfirmByEmailAsync(made.EmailConfirmToken)).Failure); // the link stays refused

        // A new customer takes the freed time.
        var again = await c.Bookings.CreateAsync(Req(c, Manila(5, 10), "other@example.com"));
        Assert.True(again.IsSuccess, again.Message);
    }

    [Fact]
    public async Task The_worker_sweep_releases_only_holds_that_ran_out_and_a_failed_email_gives_the_time_back()
    {
        var c = await SetupAsync();
        var stale = (await c.Bookings.CreateAsync(Req(c, Manila(5, 10), "a@example.com"))).Value!;
        var fresh = (await c.Bookings.CreateAsync(Req(c, Manila(5, 12), "b@example.com"))).Value!;
        await using (var ctx = c.Db.CreateDbContext())
            await ctx.Bookings.Where(b => b.Id == stale.Booking.Id).ExecuteUpdateAsync(u => u.SetProperty(b => b.HoldExpiresAtUtc, BaseNow.UtcDateTime.AddMinutes(-1)));

        Assert.Equal(1, await c.Bookings.ExpireHoldsAsync());
        Assert.Equal(0, await c.Bookings.ExpireHoldsAsync()); // nothing more to release
        await using var check = c.Db.CreateDbContext();
        Assert.Equal(BookingStatus.Cancelled, (await check.Bookings.SingleAsync(b => b.Id == stale.Booking.Id)).Status);
        Assert.Equal(BookingStatus.Held, (await check.Bookings.SingleAsync(b => b.Id == fresh.Booking.Id)).Status);

        // The email could not be sent: the time is given back. A booking that was confirmed in the meantime is left alone.
        Assert.True(await c.Bookings.ReleaseHeldAsync(c.Shop.BusinessId, fresh.Booking.Id, Text.ConfirmEmailNotSent));
        Assert.False(await c.Bookings.ReleaseHeldAsync(c.Shop.BusinessId, fresh.Booking.Id, "again"));
        var kept = (await c.Bookings.CreateAsync(Req(c, Manila(5, 14), "c@example.com"))).Value!;
        Assert.True((await c.Bookings.ConfirmByEmailAsync(kept.EmailConfirmToken)).IsSuccess);
        Assert.False(await c.Bookings.ReleaseHeldAsync(c.Shop.BusinessId, kept.Booking.Id, "too late"));
        Assert.Equal(BookingStatus.Confirmed, (await check.Bookings.AsNoTracking().SingleAsync(b => b.Id == kept.Booking.Id)).Status);
    }

    private sealed class FailingSender : IEmailSender
    {
        public bool IsConfigured => true;
        public Task<bool> SendAsync(string to, string subject, string body, string? html = null, CancellationToken ct = default) => Task.FromResult(false);
    }

    [Fact]
    public async Task The_booking_page_needs_an_email_holds_the_time_until_the_link_is_pressed_and_gives_it_back_if_the_email_fails()
    {
        Environment.SetEnvironmentVariable("Email__DailyLimit", "100000"); // the test database is shared and the real day carries over
        Environment.SetEnvironmentVariable("Email__FreeShopDailyMax", "1000");
        try
        {
            var (factory, _) = await HttpApp.StartAsync();
            using var _f = factory;
            var sender = new CapturingEmailSender();
            var good = factory.WithWebHostBuilder(b => b.ConfigureServices(s =>
                { s.AddKeyedSingleton<IEmailSender>("raw", sender); s.AddSingleton<IEmailSender>(sender); }))
                .CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = true });
            var bad = factory.WithWebHostBuilder(b => b.ConfigureServices(s =>
                { var f = new FailingSender(); s.AddKeyedSingleton<IEmailSender>("raw", f); s.AddSingleton<IEmailSender>(f); }))
                .CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = true });

            var db = new TestDbFactory(await TestDb.ConnectionStringAsync());
            var slug = Id("ec");
            var owner = (await new OnboardingService(db, TimeProvider.System).RegisterAsync(
                new RegisterRequest("Confirm Shop", slug, "Owner", $"{Id("o")}@example.com", "correct-horse-1", TrialDays: 30))).Value!;
            await using var ctx = db.CreateDbContext();
            await ctx.Businesses.Where(b => b.Id == owner.BusinessId).ExecuteUpdateAsync(u => u.SetProperty(b => b.RequireEmailConfirm, true));
            var serviceId = await ctx.Services.Where(s => s.BusinessId == owner.BusinessId && !s.IsOther).Select(s => s.Id).SingleAsync();
            var starts = (await new BookingService(db, TimeProvider.System).GetFreeStartsAsync(owner.BusinessId, serviceId, null, NextOpenDay())).Value!;
            var url = $"/b/{slug}/confirm?serviceId={serviceId}&start={Uri.EscapeDataString(DisplayFormat.StartParam(starts[0]))}";

            async Task<HttpResponseMessage> Post(HttpClient client, string email, string phone)
            {
                var page = await client.GetStringAsync(url);
                var fields = new Dictionary<string, string>();
                foreach (Match tag in Regex.Matches(page, "<input[^>]*>"))
                {
                    if (!Regex.IsMatch(tag.Value, "type=\"hidden\"")) continue;
                    fields[Regex.Match(tag.Value, "name=\"([^\"]*)\"").Groups[1].Value] = WebUtility.HtmlDecode(Regex.Match(tag.Value, "value=\"([^\"]*)\"").Groups[1].Value);
                }
                fields["Input.Name"] = "Maria Santos";
                fields["Input.Phone"] = phone;
                fields["Input.Email"] = email;
                return await client.PostAsync($"/b/{slug}/confirm", new FormUrlEncodedContent(fields));
            }

            // The form says the email is needed, and a missing one is refused with nothing booked.
            var form = WebUtility.HtmlDecode(await good.GetStringAsync(url));
            Assert.Contains("we send a link there to confirm your booking", form);
            var missing = await Post(good, "", "09175550801");
            Assert.Equal(HttpStatusCode.OK, missing.StatusCode);
            Assert.Contains("Please give an email address", WebUtility.HtmlDecode(await missing.Content.ReadAsStringAsync()));
            Assert.Equal(0, await ctx.Bookings.CountAsync(b => b.BusinessId == owner.BusinessId));

            // A failing email service: the form comes back with the message and the time is not left booked.
            var failed = await Post(bad, "maria@example.com", "09175550802");
            Assert.Equal(HttpStatusCode.OK, failed.StatusCode);
            Assert.Contains("could not send the confirmation email", WebUtility.HtmlDecode(await failed.Content.ReadAsStringAsync()));
            Assert.Equal(0, await ctx.Bookings.CountAsync(b => b.BusinessId == owner.BusinessId && (b.Status == BookingStatus.Held || b.Status == BookingStatus.Confirmed)));
            Assert.Equal(1, await ctx.Bookings.CountAsync(b => b.BusinessId == owner.BusinessId && b.Status == BookingStatus.Cancelled));

            // A working one: held, one email with the link, and the manage page says to check the email.
            var ok = await Post(good, "maria@example.com", "09175550803");
            Assert.Equal(HttpStatusCode.Redirect, ok.StatusCode);
            var manageUrl = ok.Headers.Location!.OriginalString;
            Assert.StartsWith("/booking/", manageUrl);
            var mail = Assert.Single(sender.Sent, m => m.To == "maria@example.com" && m.Body.Contains("/confirm-booking/"));
            var link = Regex.Match(mail.Body, @"https?://[^\s]*/confirm-booking/[^\s]+").Value;
            Assert.NotEmpty(link);
            Assert.Contains("Check your email", WebUtility.HtmlDecode(await good.GetStringAsync(manageUrl)));
            Assert.Equal(1, await ctx.Bookings.CountAsync(b => b.BusinessId == owner.BusinessId && b.Status == BookingStatus.Held));

            // Opening the link only shows the booking; the button confirms.
            var path = new Uri(link).AbsolutePath;
            var confirmPage = WebUtility.HtmlDecode(await good.GetStringAsync(path));
            Assert.Contains("Confirm my booking", confirmPage);
            Assert.Equal(1, await ctx.Bookings.CountAsync(b => b.BusinessId == owner.BusinessId && b.Status == BookingStatus.Held));
            var pressed = await HttpApp.PostFormAsync(good, path, new(), tokenPage: path);
            Assert.Equal(HttpStatusCode.Redirect, pressed.StatusCode);
            Assert.Equal(manageUrl, pressed.Headers.Location!.OriginalString);
            Assert.Equal(1, await ctx.Bookings.CountAsync(b => b.BusinessId == owner.BusinessId && b.Status == BookingStatus.Confirmed));
            Assert.Contains("Your booking is confirmed", WebUtility.HtmlDecode(await good.GetStringAsync(manageUrl)));

            // Unknown secrets are "not found".
            Assert.Equal(HttpStatusCode.NotFound, (await good.GetAsync("/confirm-booking/not-a-real-secret")).StatusCode);
        }
        finally
        {
            Environment.SetEnvironmentVariable("Email__DailyLimit", null);
            Environment.SetEnvironmentVariable("Email__FreeShopDailyMax", null);
        }
    }
}
