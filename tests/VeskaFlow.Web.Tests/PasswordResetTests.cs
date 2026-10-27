using System.Net;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Configuration;
using VeskaFlow.Web.Services;

namespace VeskaFlow.Web.Tests;

/// <summary>Forgot password (one-hour single-use link) and changing your own password.</summary>
[Collection("http")]
public class PasswordResetTests
{
    private static string Id(string prefix) => prefix + Guid.NewGuid().ToString("N")[..10];

    private sealed record Setup(TestDbFactory Db, TestClock Clock, OnboardingService Onboarding, AuthService Auth, AccountControlService Accounts);

    private static async Task<Setup> SetupAsync()
    {
        var db = new TestDbFactory(await TestDb.ConnectionStringAsync());
        var clock = new TestClock(Seed.BaseNow);
        return new Setup(db, clock, new OnboardingService(db, clock), new AuthService(db),
            new AccountControlService(db, clock, new ConfigurationBuilder().Build(), new CapturingEmailSender()));
    }

    private static async Task<(RegisteredOwner Owner, string Email)> OwnerAsync(Setup s)
    {
        var email = $"{Id("o")}@example.com";
        var owner = (await s.Onboarding.RegisterAsync(new RegisterRequest("Cuts", Id("pr"), "Owner", email, "old-password-1"))).Value!;
        return (owner, email);
    }

    [Fact]
    public async Task A_reset_link_sets_a_new_password_once_and_expires_after_an_hour()
    {
        var s = await SetupAsync();
        var (_, email) = await OwnerAsync(s);

        Assert.Null(await s.Accounts.IssuePasswordResetAsync("nobody@example.com")); // silent for an unknown address
        var issued = (await s.Accounts.IssuePasswordResetAsync(email.ToUpperInvariant()))!;
        Assert.Null(await s.Accounts.IssuePasswordResetAsync(email)); // asking again within a minute sends nothing new

        Assert.True(await s.Accounts.IsResetTokenValidAsync(issued.Token));
        Assert.False(await s.Accounts.IsResetTokenValidAsync("wrong-token"));
        Assert.Contains("password", (await s.Accounts.ResetPasswordAsync(issued.Token, "short")).FieldErrors.Keys);

        Assert.True((await s.Accounts.ResetPasswordAsync(issued.Token, "new-password-1")).IsSuccess);
        Assert.True((await s.Auth.SignInAsync(email, "new-password-1")).IsSuccess);
        Assert.False((await s.Auth.SignInAsync(email, "old-password-1")).IsSuccess);
        Assert.Equal(FailureKind.NotFound, (await s.Accounts.ResetPasswordAsync(issued.Token, "another-password-1")).Failure); // single use

        var second = (await s.Accounts.IssuePasswordResetAsync(email))!;
        s.Clock.Now = s.Clock.Now.AddMinutes(61);
        Assert.False(await s.Accounts.IsResetTokenValidAsync(second.Token));
        Assert.Equal(FailureKind.NotFound, (await s.Accounts.ResetPasswordAsync(second.Token, "another-password-1")).Failure);
    }

    [Fact]
    public async Task Switched_off_people_and_shops_get_no_reset_link_and_a_reset_confirms_the_email()
    {
        var s = await SetupAsync();
        var off = await OwnerAsync(s);
        await s.Accounts.SetEnabledAsync(off.Owner.BusinessId, false);
        Assert.Null(await s.Accounts.IssuePasswordResetAsync(off.Email));

        var unconfirmed = $"{Id("u")}@example.com";
        await s.Onboarding.RegisterAsync(new RegisterRequest("Cuts", Id("pu"), "Owner", unconfirmed, "old-password-1", RequireEmailConfirmation: true));
        Assert.False((await s.Auth.SignInAsync(unconfirmed, "old-password-1")).IsSuccess);
        var issued = (await s.Accounts.IssuePasswordResetAsync(unconfirmed))!;
        Assert.True((await s.Accounts.ResetPasswordAsync(issued.Token, "new-password-1")).IsSuccess);
        Assert.True((await s.Auth.SignInAsync(unconfirmed, "new-password-1")).IsSuccess); // opening the link proved the address
    }

    [Fact]
    public async Task Changing_your_own_password_needs_the_current_one()
    {
        var s = await SetupAsync();
        var (owner, email) = await OwnerAsync(s);

        Assert.Contains("current", (await s.Auth.ChangePasswordAsync(owner.AdminUserId, "wrong", "new-password-1")).FieldErrors.Keys);
        Assert.Contains("current", (await s.Auth.ChangePasswordAsync(owner.AdminUserId, "", "new-password-1")).FieldErrors.Keys);
        Assert.Contains("newPassword", (await s.Auth.ChangePasswordAsync(owner.AdminUserId, "old-password-1", "short")).FieldErrors.Keys);
        Assert.True((await s.Auth.ChangePasswordAsync(owner.AdminUserId, "old-password-1", "new-password-1")).IsSuccess);
        Assert.True((await s.Auth.SignInAsync(email, "new-password-1")).IsSuccess);
        Assert.False((await s.Auth.SignInAsync(email, "old-password-1")).IsSuccess);
        Assert.Equal(FailureKind.NotFound, (await s.Auth.ChangePasswordAsync(Guid.NewGuid(), "x", "new-password-1")).Failure);
    }

    [Fact]
    public async Task The_pages_send_the_email_give_the_same_answer_for_every_address_and_let_the_person_sign_in_with_the_new_password()
    {
        var (factory, _) = await HttpApp.StartAsync();
        using var _f = factory;
        var sender = new CapturingEmailSender();
        var client = factory.WithWebHostBuilder(b => b.ConfigureServices(sv => sv.AddSingleton<IEmailSender>(sender)))
            .CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = true });

        var db = new TestDbFactory(await TestDb.ConnectionStringAsync());
        var email = $"{Id("w")}@example.com";
        await new OnboardingService(db, TimeProvider.System).RegisterAsync(new RegisterRequest("Cuts", Id("pw"), "Owner", email, "old-password-1"));

        Assert.Contains("Forgot your password?", await client.GetStringAsync("/Login"));
        var known = await HttpApp.PostFormAsync(client, "/ForgotPassword", new() { ["Email"] = email });
        var unknown = await HttpApp.PostFormAsync(client, "/ForgotPassword", new() { ["Email"] = "nobody@example.com" });
        Assert.Equal(HttpStatusCode.OK, known.StatusCode);
        Assert.Equal(await known.Content.ReadAsStringAsync().ContinueWith(t => Regex.Replace(t.Result, "__RequestVerificationToken[^>]*>", "")),
            await unknown.Content.ReadAsStringAsync().ContinueWith(t => Regex.Replace(t.Result, "__RequestVerificationToken[^>]*>", "")));
        var mail = Assert.Single(sender.Sent);
        Assert.Equal(email, mail.To);
        var link = Regex.Match(mail.Body, @"/ResetPassword\?token=[\w\-]+").Value;
        Assert.NotEmpty(link);

        Assert.Contains("Choose a new password", await client.GetStringAsync(link));
        var mismatch = await HttpApp.PostFormAsync(client, link, new() { ["Token"] = link[(link.IndexOf('=') + 1)..], ["Password"] = "new-password-1", ["Confirm"] = "different-1" });
        Assert.Contains("not the same", System.Net.WebUtility.HtmlDecode(await mismatch.Content.ReadAsStringAsync()));
        var done = await HttpApp.PostFormAsync(client, link, new() { ["Token"] = link[(link.IndexOf('=') + 1)..], ["Password"] = "new-password-1", ["Confirm"] = "new-password-1" });
        Assert.Equal(HttpStatusCode.Redirect, done.StatusCode);
        Assert.Contains("/Login?reset=true", done.Headers.Location!.OriginalString);

        var signIn = await HttpApp.PostFormAsync(client, "/Login", new() { ["Input.Email"] = email, ["Input.Password"] = "new-password-1" });
        Assert.Equal(HttpStatusCode.Redirect, signIn.StatusCode);
        Assert.Contains("link has expired", await client.GetStringAsync(link)); // the link is used up
    }
}
