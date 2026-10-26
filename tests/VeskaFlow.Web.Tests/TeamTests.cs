using System.Net;
using VeskaFlow.Web.Services;
using Microsoft.EntityFrameworkCore;

namespace VeskaFlow.Web.Tests;

/// <summary>Owner, manager and staff: what each may open, adding and switching off people, isolation between shops.</summary>
[Collection("http")]
public class TeamTests
{
    private static string Id(string prefix) => prefix + Guid.NewGuid().ToString("N")[..10];

    private static async Task<HttpClient> SignInAsync(AppFactory factory, string email, string password)
    {
        var client = HttpApp.NewClient(factory);
        var response = await HttpApp.PostFormAsync(client, "/Login", new() { ["Input.Email"] = email, ["Input.Password"] = password });
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        return client;
    }

    [Fact]
    public async Task Staff_and_managers_see_only_what_their_role_allows_and_a_switched_off_person_is_signed_out()
    {
        var (factory, _) = await HttpApp.StartAsync();
        using var _f = factory;
        var db = new TestDbFactory(await TestDb.ConnectionStringAsync());
        var ownerEmail = $"{Id("o")}@example.com";
        var owner = (await new OnboardingService(db, TimeProvider.System).RegisterAsync(
            new RegisterRequest("Team Shop", Id("tm"), "Owner", ownerEmail, "correct-horse-1", TrialDays: 30))).Value!;
        var team = new TeamService(db, TimeProvider.System);

        var staffEmail = $"{Id("s")}@example.com";
        var managerEmail = $"{Id("m")}@example.com";
        var staff = (await team.AddAsync(owner.BusinessId, "Sam Staff", staffEmail, "STAFF", "staff-password-1")).Value!;
        Assert.True((await team.AddAsync(owner.BusinessId, "Mia Manager", managerEmail, "MANAGER", "manager-password-1")).IsSuccess);

        var ownerClient = await SignInAsync(factory, ownerEmail, "correct-horse-1");
        var staffClient = await SignInAsync(factory, staffEmail, "staff-password-1");
        var managerClient = await SignInAsync(factory, managerEmail, "manager-password-1");

        async Task<HttpStatusCode> Get(HttpClient c, string path) => (await c.GetAsync(path)).StatusCode;

        foreach (var path in new[] { "/Admin", "/Admin/WalkIn", "/Admin/Reminders", "/Admin/FollowUps" })
        {
            Assert.Equal(HttpStatusCode.OK, await Get(staffClient, path));
            Assert.Equal(HttpStatusCode.OK, await Get(managerClient, path));
        }
        foreach (var path in new[] { "/Admin/Customers", "/Admin/Reports", "/Admin/Services", "/Admin/Staff", "/Admin/Closures", "/Admin/Audit" })
        {
            Assert.Equal(HttpStatusCode.NotFound, await Get(staffClient, path));
            Assert.Equal(HttpStatusCode.OK, await Get(managerClient, path));
            Assert.Equal(HttpStatusCode.OK, await Get(ownerClient, path));
        }
        foreach (var path in new[] { "/Admin/Settings", "/Admin/Team" })
        {
            Assert.Equal(HttpStatusCode.NotFound, await Get(staffClient, path));
            Assert.Equal(HttpStatusCode.NotFound, await Get(managerClient, path));
            Assert.Equal(HttpStatusCode.OK, await Get(ownerClient, path));
        }

        // The menu only shows what the person can open.
        var staffMenu = await staffClient.GetStringAsync("/Admin");
        Assert.DoesNotContain("href=\"/Admin/Settings\"", staffMenu);
        Assert.DoesNotContain("href=\"/Admin/Reports\"", staffMenu);
        Assert.Contains("href=\"/Admin/Reports\"", await managerClient.GetStringAsync("/Admin"));
        Assert.Contains("href=\"/Admin/Team\"", await ownerClient.GetStringAsync("/Admin"));

        // Switching someone off ends their session at once and blocks a new sign-in.
        Assert.True((await team.SetActiveAsync(owner.BusinessId, staff.Id, false)).IsSuccess);
        var after = await staffClient.GetAsync("/Admin");
        Assert.Equal(HttpStatusCode.Redirect, after.StatusCode);
        Assert.Contains("/Login", after.Headers.Location!.OriginalString);
        var again = await HttpApp.PostFormAsync(HttpApp.NewClient(factory), "/Login", new() { ["Input.Email"] = staffEmail, ["Input.Password"] = "staff-password-1" });
        Assert.Equal(HttpStatusCode.OK, again.StatusCode); // the login page again, not a redirect into the back office
    }

    [Fact]
    public async Task Adding_people_is_validated_limited_and_kept_within_the_shop()
    {
        var db = new TestDbFactory(await TestDb.ConnectionStringAsync());
        var team = new TeamService(db, TimeProvider.System);
        var onboarding = new OnboardingService(db, TimeProvider.System);
        var a = (await onboarding.RegisterAsync(new RegisterRequest("A", Id("ta"), "Owner A", $"{Id("o")}@example.com", "correct-horse-1"))).Value!;
        var b = (await onboarding.RegisterAsync(new RegisterRequest("B", Id("tb"), "Owner B", $"{Id("o")}@example.com", "correct-horse-1"))).Value!;

        var email = $"{Id("p")}@example.com";
        Assert.Contains("role", (await team.AddAsync(a.BusinessId, "Pat", email, "OWNER", "long-enough-1")).FieldErrors.Keys); // no second owner
        Assert.Contains("password", (await team.AddAsync(a.BusinessId, "Pat", email, "STAFF", "short")).FieldErrors.Keys);
        Assert.Contains("email", (await team.AddAsync(a.BusinessId, "Pat", "not-an-email", "STAFF", "long-enough-1")).FieldErrors.Keys);
        var pat = (await team.AddAsync(a.BusinessId, "Pat", email, "STAFF", "long-enough-1")).Value!;
        Assert.Contains("email", (await team.AddAsync(b.BusinessId, "Pat Two", email.ToUpperInvariant(), "STAFF", "long-enough-1")).FieldErrors.Keys); // one email, one account

        // Another shop's owner cannot switch off, re-password or even see this person; the owner cannot be switched off.
        Assert.Equal(FailureKind.NotFound, (await team.SetActiveAsync(b.BusinessId, pat.Id, false)).Failure);
        Assert.Equal(FailureKind.NotFound, (await team.SetPasswordAsync(b.BusinessId, pat.Id, "another-password-1")).Failure);
        Assert.DoesNotContain(await team.ListAsync(b.BusinessId), m => m.Id == pat.Id);
        Assert.Equal(FailureKind.PolicyViolation, (await team.SetActiveAsync(a.BusinessId, a.AdminUserId, false)).Failure);
        Assert.Null(await team.GetAccessAsync(pat.Id, b.BusinessId));

        Assert.True((await team.SetPasswordAsync(a.BusinessId, pat.Id, "another-password-1")).IsSuccess);
        var auth = new AuthService(db);
        Assert.True((await auth.SignInAsync(email, "another-password-1")).IsSuccess);
        Assert.False((await auth.SignInAsync(email, "long-enough-1")).IsSuccess);
    }
}
