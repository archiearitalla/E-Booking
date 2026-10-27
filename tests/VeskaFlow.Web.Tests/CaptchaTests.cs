using System.Net;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using VeskaFlow.Web.Services;

namespace VeskaFlow.Web.Tests;

/// <summary>The "are you a person" check: off without keys, strict with them, and never letting a failed check through.</summary>
[Collection("http")]
public class CaptchaTests
{
    private static string Id(string prefix) => prefix + Guid.NewGuid().ToString("N")[..10];

    private sealed class FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public int Calls { get; private set; }
        public string? LastBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls++;
            LastBody = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct);
            return respond(request);
        }
    }

    private static TurnstileVerifier Verifier(FakeHandler handler, bool configured = true) =>
        new(new HttpClient(handler), new ConfigurationBuilder().AddInMemoryCollection(configured
            ? new Dictionary<string, string?> { ["Turnstile:SiteKey"] = "site", ["Turnstile:SecretKey"] = "secret" }
            : new Dictionary<string, string?>()).Build(), NullLogger<TurnstileVerifier>.Instance);

    private static HttpResponseMessage Json(string json) => new(HttpStatusCode.OK) { Content = new StringContent(json) };

    [Fact]
    public async Task Without_keys_everything_passes_and_nothing_is_called()
    {
        var handler = new FakeHandler(_ => Json("{\"success\":false}"));
        var verifier = Verifier(handler, configured: false);
        Assert.False(verifier.IsEnabled);
        Assert.True(await verifier.VerifyAsync(null, null));
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task With_keys_only_a_confirmed_token_passes_and_any_failure_refuses()
    {
        var ok = new FakeHandler(_ => Json("{\"success\":true}"));
        Assert.True(await Verifier(ok).VerifyAsync("token-1", "203.0.113.7"));
        Assert.Contains("secret=secret", ok.LastBody);
        Assert.Contains("response=token-1", ok.LastBody);
        Assert.Contains("remoteip=203.0.113.7", ok.LastBody);

        var no = new FakeHandler(_ => Json("{\"success\":false,\"error-codes\":[\"invalid-input-response\"]}"));
        Assert.False(await Verifier(no).VerifyAsync("token-2", null));
        Assert.False(await Verifier(ok).VerifyAsync("", null)); // no token: not even asked
        Assert.False(await Verifier(ok).VerifyAsync(new string('x', 3000), null));
        Assert.False(await Verifier(new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.BadGateway))).VerifyAsync("t", null));
        Assert.False(await Verifier(new FakeHandler(_ => Json("not json"))).VerifyAsync("t", null));
        Assert.False(await Verifier(new FakeHandler(_ => throw new HttpRequestException("offline"))).VerifyAsync("t", null));
    }

    private sealed class FakeCaptcha : ICaptcha
    {
        public bool IsEnabled => true;
        public string SiteKey => "site-key-123";
        public Task<bool> VerifyAsync(string? token, string? clientIp, CancellationToken ct = default) => Task.FromResult(token == "good");
    }

    [Fact]
    public async Task Sign_up_and_forgot_password_need_the_check_when_keys_are_set_and_the_policy_allows_only_Cloudflare()
    {
        Environment.SetEnvironmentVariable("Turnstile__SiteKey", "site-key-123");
        Environment.SetEnvironmentVariable("Turnstile__SecretKey", "secret-key");
        try
        {
            var (factory, _) = await HttpApp.StartAsync();
            using var _f = factory;
            var client = factory.WithWebHostBuilder(b => b.ConfigureServices(s => { s.AddSingleton<ICaptcha>(new FakeCaptcha()); s.AddSingleton<IEmailSender>(new CapturingEmailSender()); }))
                .CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = true });

            var page = await client.GetAsync("/Register");
            var html = await page.Content.ReadAsStringAsync();
            Assert.Contains("cf-turnstile", html);
            Assert.Contains("data-sitekey=\"site-key-123\"", html);
            Assert.DoesNotContain("secret-key", html);
            var csp = string.Join(" ", page.Headers.GetValues("Content-Security-Policy"));
            Assert.Contains("script-src 'self' https://challenges.cloudflare.com", csp);
            Assert.Contains("frame-src https://challenges.cloudflare.com", csp);

            Dictionary<string, string> Form(string slug, string? token)
            {
                var f = new Dictionary<string, string>
                {
                    ["Input.Accept"] = "true", ["Input.BusinessName"] = "Captcha Shop", ["Input.Slug"] = slug, ["Input.OwnerName"] = "Owner",
                    ["Input.Email"] = $"{Id("c")}@example.com", ["Input.Password"] = "correct-horse-1", ["Input.Industry"] = "clinic",
                };
                if (token is not null) f["cf-turnstile-response"] = token;
                return f;
            }

            var slug = Id("cp");
            var refused = await HttpApp.PostFormAsync(client, "/Register", Form(slug, null), tokenPage: "/Register");
            Assert.Equal(HttpStatusCode.OK, refused.StatusCode);
            Assert.Contains("could not check that you are a person", System.Net.WebUtility.HtmlDecode(await refused.Content.ReadAsStringAsync()));
            var wrong = await HttpApp.PostFormAsync(client, "/Register", Form(slug, "bad"), tokenPage: "/Register");
            Assert.Equal(HttpStatusCode.OK, wrong.StatusCode);

            var accepted = await HttpApp.PostFormAsync(client, "/Register", Form(slug, "good"), tokenPage: "/Register");
            Assert.Equal(HttpStatusCode.Redirect, accepted.StatusCode);

            var forgotRefused = await HttpApp.PostFormAsync(client, "/ForgotPassword", new() { ["Email"] = "a@example.com" });
            Assert.Contains("could not check that you are a person", System.Net.WebUtility.HtmlDecode(await forgotRefused.Content.ReadAsStringAsync()));
            var forgotOk = await HttpApp.PostFormAsync(client, "/ForgotPassword", new() { ["Email"] = "a@example.com", ["cf-turnstile-response"] = "good" });
            Assert.Contains("we sent a link", await forgotOk.Content.ReadAsStringAsync().ContinueWith(t => System.Net.WebUtility.HtmlDecode(t.Result)));
        }
        finally
        {
            Environment.SetEnvironmentVariable("Turnstile__SiteKey", null);
            Environment.SetEnvironmentVariable("Turnstile__SecretKey", null);
        }
    }

    [Fact]
    public async Task Without_keys_the_pages_show_no_widget_and_the_policy_stays_closed()
    {
        var (factory, client) = await HttpApp.StartAsync();
        using var _f = factory;
        var page = await client.GetAsync("/Register");
        Assert.DoesNotContain("cf-turnstile", await page.Content.ReadAsStringAsync());
        Assert.DoesNotContain("cloudflare", string.Join(" ", page.Headers.GetValues("Content-Security-Policy")));
    }
}
