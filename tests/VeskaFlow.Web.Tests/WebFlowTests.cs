using System.Net;
using static VeskaFlow.Web.Tests.HttpApp;

namespace VeskaFlow.Web.Tests;

/// <summary>The real app over HTTP (in-process server) against the real PostgreSQL test database.</summary>
[Collection("http")] // these tests set process-wide environment variables, so they never run in parallel
public class WebFlowTests
{
    private static Task<(AppFactory Factory, HttpClient Client)> StartAsync(int authPerMinute = 1000) =>
        HttpApp.StartAsync(authPerMinute);

    private static Dictionary<string, string> RegisterFields(string email, string slug) => new()
    {
        ["Input.BusinessName"] = "Ben's Barbershop",
        ["Input.Slug"] = slug,
        ["Input.OwnerName"] = "Ben Santos",
        ["Input.Email"] = email,
        ["Input.Password"] = "correct-horse-1",
    };

    [Fact]
    public async Task Registering_signs_the_owner_in_and_the_about_page_shows_name_and_role()
    {
        var (factory, client) = await StartAsync();
        using var _ = factory;

        var response = await PostFormAsync(client, "/Register", RegisterFields($"{Unique("a")}@example.com", Unique("shop")));
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/Admin", response.Headers.Location!.OriginalString);

        var admin = await client.GetAsync("/Admin");
        Assert.Equal(HttpStatusCode.OK, admin.StatusCode);
        var adminHtml = await admin.Content.ReadAsStringAsync();
        Assert.DoesNotContain("Ben Santos", adminHtml); // the name and role are not repeated on every page

        var about = await client.GetAsync("/Admin/About");
        Assert.Equal(HttpStatusCode.OK, about.StatusCode);
        var html = await about.Content.ReadAsStringAsync();
        Assert.Contains("Ben Santos", html);
        Assert.Contains("OWNER", html);
    }

    [Fact]
    public async Task The_admin_folder_requires_sign_in()
    {
        var (factory, client) = await StartAsync();
        using var _ = factory;

        var response = await client.GetAsync("/Admin");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var location = response.Headers.Location!;
        var path = location.IsAbsoluteUri ? location.AbsolutePath : location.OriginalString;
        Assert.StartsWith("/Login", path);
        Assert.Contains("ReturnUrl=%2FAdmin", location.OriginalString);
    }

    [Fact]
    public async Task Signing_out_clears_the_cookie()
    {
        var (factory, client) = await StartAsync();
        using var _ = factory;
        await PostFormAsync(client, "/Register", RegisterFields($"{Unique("a")}@example.com", Unique("shop")));
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/Admin")).StatusCode);

        var logout = await PostFormAsync(client, "/Logout", new Dictionary<string, string>(), tokenPage: "/Admin");
        Assert.Equal(HttpStatusCode.Redirect, logout.StatusCode);

        Assert.Equal(HttpStatusCode.Redirect, (await client.GetAsync("/Admin")).StatusCode);
    }

    [Fact]
    public async Task Registering_a_taken_slug_or_email_shows_a_friendly_error_not_a_500()
    {
        var (factory, client) = await StartAsync();
        using var _ = factory;
        var email = $"{Unique("a")}@example.com";
        var slug = Unique("shop");
        await PostFormAsync(client, "/Register", RegisterFields(email, slug));

        using var second = NewClient(factory); // a fresh browser
        var response = await PostFormAsync(second, "/Register", RegisterFields(email, slug));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var html = WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync());
        Assert.Contains(Text.SlugTaken, html);
        Assert.Contains(Text.EmailTaken, html);
    }

    [Fact]
    public async Task Wrong_password_and_unknown_email_show_the_same_message_and_do_not_sign_in()
    {
        var (factory, client) = await StartAsync();
        using var _ = factory;
        var email = $"{Unique("a")}@example.com";
        await PostFormAsync(client, "/Register", RegisterFields(email, Unique("shop")));

        using var browser = NewClient(factory);
        var wrong = await PostFormAsync(browser, "/Login", new() { ["Input.Email"] = email, ["Input.Password"] = "nope-nope-nope" });
        var unknown = await PostFormAsync(browser, "/Login", new() { ["Input.Email"] = "nobody@example.com", ["Input.Password"] = "nope-nope-nope" });

        Assert.Equal(HttpStatusCode.OK, wrong.StatusCode);
        Assert.Equal(HttpStatusCode.OK, unknown.StatusCode);
        Assert.Contains(Text.LoginFailed, await wrong.Content.ReadAsStringAsync());
        Assert.Contains(Text.LoginFailed, await unknown.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.Redirect, (await browser.GetAsync("/Admin")).StatusCode);
    }

    [Fact]
    public async Task Signing_in_with_the_right_password_works_and_only_local_return_urls_are_followed()
    {
        var (factory, client) = await StartAsync();
        using var _ = factory;
        var email = $"{Unique("a")}@example.com";
        await PostFormAsync(client, "/Register", RegisterFields(email, Unique("shop")));

        using var b1 = NewClient(factory);
        var external = await PostFormAsync(b1, "/Login", new() { ["Input.Email"] = email, ["Input.Password"] = "correct-horse-1", ["ReturnUrl"] = "https://evil.example/steal" });
        Assert.Equal(HttpStatusCode.Redirect, external.StatusCode);
        Assert.Equal("/Admin", external.Headers.Location!.OriginalString);

        using var b2 = NewClient(factory);
        var local = await PostFormAsync(b2, "/Login", new() { ["Input.Email"] = email, ["Input.Password"] = "correct-horse-1", ["ReturnUrl"] = "/Admin" });
        Assert.Equal("/Admin", local.Headers.Location!.OriginalString);
        Assert.Equal(HttpStatusCode.OK, (await b2.GetAsync("/Admin")).StatusCode);
    }

    [Fact]
    public async Task Too_many_sign_in_attempts_per_minute_get_429_but_viewing_the_form_is_not_limited()
    {
        var (factory, client) = await StartAsync(authPerMinute: 3);
        using var _ = factory;
        var body = new Dictionary<string, string> { ["Input.Email"] = "nobody@example.com", ["Input.Password"] = "nope-nope-nope" };

        var statuses = new List<HttpStatusCode>();
        for (var i = 0; i < 5; i++)
            statuses.Add((await PostFormAsync(client, "/Login", new Dictionary<string, string>(body))).StatusCode);

        Assert.Equal(new[] { HttpStatusCode.OK, HttpStatusCode.OK, HttpStatusCode.OK, HttpStatusCode.TooManyRequests, HttpStatusCode.TooManyRequests }, statuses);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/Login")).StatusCode);
    }

    [Fact]
    public async Task A_post_without_the_antiforgery_token_is_rejected()
    {
        var (factory, client) = await StartAsync();
        using var _ = factory;

        var response = await client.PostAsync("/Login", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["Input.Email"] = "a@example.com", ["Input.Password"] = "whatever-123",
        }));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Pages_carry_the_security_headers_and_the_auth_cookie_is_http_only()
    {
        var (factory, client) = await StartAsync();
        using var _ = factory;

        var login = await client.GetAsync("/Login");
        Assert.Equal("nosniff", login.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.Equal("DENY", login.Headers.GetValues("X-Frame-Options").Single());
        Assert.Contains("script-src 'self'", login.Headers.GetValues("Content-Security-Policy").Single());

        var register = await PostFormAsync(client, "/Register", RegisterFields($"{Unique("a")}@example.com", Unique("shop")));
        var authCookie = register.Headers.GetValues("Set-Cookie").Single(c => c.StartsWith("bb.auth="));
        Assert.Contains("httponly", authCookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=lax", authCookie, StringComparison.OrdinalIgnoreCase);
    }
}
