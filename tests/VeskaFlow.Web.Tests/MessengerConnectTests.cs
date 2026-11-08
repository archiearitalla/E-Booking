using System.Net;
using System.Text;
using System.Web;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using VeskaFlow.Web.Services;

namespace VeskaFlow.Web.Tests;

/// <summary>Each shop connects its own Facebook Pages (BUSINESS_RULES 6.y23): only the owner, only with the AI add-on, one shop per Page, tokens saved encrypted. Meta is never called.</summary>
[Collection("http")]
public class MessengerConnectTests
{
    private const string Secret = "app-secret-for-tests";
    private const string Verify = "verify-me";
    private const string AppId = "1234567890";
    private const string Key = "a-long-secret-phrase-for-tests-only";

    private sealed class FakeGraph : IMessengerGraph
    {
        public List<FacebookPage> Pages { get; set; } = [new("PAGE1", "Juan's Barbers", "tok-1"), new("PAGE2", "Second Page", "tok-2")];
        public List<string> Subscribed { get; } = [];
        public List<(string Page, string Token)> Unsubscribed { get; } = [];
        public bool SubscribeOk { get; set; } = true;
        public Task<string?> GetUserTokenAsync(string code, string redirectUri, CancellationToken ct = default) => Task.FromResult<string?>(code == "good" ? "user-token" : null);
        public Task<IReadOnlyList<FacebookPage>?> ListPagesAsync(string userToken, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<FacebookPage>?>(Pages);
        public Task<bool> SubscribeAsync(string pageId, string pageToken, CancellationToken ct = default) { if (SubscribeOk) Subscribed.Add(pageId); return Task.FromResult(SubscribeOk); }
        public Task UnsubscribeAsync(string pageId, string pageToken, CancellationToken ct = default) { Unsubscribed.Add((pageId, pageToken)); return Task.CompletedTask; }
    }

    private sealed class CapturingMessenger : IMessengerSender
    {
        public List<(string Token, string Psid, string Text)> Sent { get; } = [];
        public Task<bool> SendTextAsync(string pageToken, string psid, string text, CancellationToken ct = default) { Sent.Add((pageToken, psid, text)); return Task.FromResult(true); }
    }

    [Fact]
    public void The_login_address_asks_for_a_saved_configuration_when_one_is_set_and_for_the_permissions_otherwise()
    {
        try
        {
            SetConnectSettings();
            Environment.SetEnvironmentVariable("Messenger__ConfigId", "CFG123");
            var withConfig = AuthorizeUrlFor();
            Assert.Contains("config_id=CFG123", withConfig);
            Assert.Contains("override_default_response_type=true", withConfig);
            Assert.DoesNotContain("scope=", withConfig);

            Environment.SetEnvironmentVariable("Messenger__ConfigId", null);
            var classic = AuthorizeUrlFor();
            Assert.Contains("scope=pages_show_list,pages_messaging,pages_manage_metadata,pages_read_engagement", classic);
            Assert.DoesNotContain("config_id", classic);
        }
        finally { Environment.SetEnvironmentVariable("Messenger__ConfigId", null); ClearSettings(); }

        static string AuthorizeUrlFor() =>
            new MessengerConnectService(null!, TimeProvider.System, new MessengerOptions(Config()), new MessengerCipher(new MessengerOptions(Config())), null!, null!,
                Microsoft.Extensions.Logging.Abstractions.NullLogger<MessengerConnectService>.Instance).AuthorizeUrl("https://x.test/Admin/MessengerReturn", "S");
    }

    private sealed class StubHandler(Func<HttpRequestMessage, (HttpStatusCode, string)> answer) : HttpMessageHandler
    {
        public List<string> Paths { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Paths.Add(request.RequestUri!.AbsolutePath);
            var (status, body) = answer(request);
            return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
        }
    }

    [Fact]
    public async Task A_Page_the_usual_list_misses_is_found_through_the_token_s_own_permissions_and_Meta_failing_gives_null()
    {
        try
        {
            SetConnectSettings();
            var options = new MessengerOptions(Config());
            var handler = new StubHandler(r => r.RequestUri!.AbsolutePath switch
            {
                var p when p.EndsWith("/me/accounts") => (HttpStatusCode.OK, "{\"data\":[]}"),                                                 // the usual list is empty (a Page in a business portfolio)
                var p when p.EndsWith("/debug_token") => (HttpStatusCode.OK, "{\"data\":{\"granular_scopes\":[{\"scope\":\"pages_messaging\",\"target_ids\":[\"869957749543347\"]},{\"scope\":\"pages_show_list\",\"target_ids\":[\"869957749543347\"]},{\"scope\":\"email\"}]}}"),
                var p when p.EndsWith("/869957749543347") => (HttpStatusCode.OK, "{\"id\":\"869957749543347\",\"name\":\"Tambay sa Kalye\",\"access_token\":\"page-tok\"}"),
                _ => (HttpStatusCode.NotFound, "{}"),
            });
            var graph = new GraphMessengerClient(new HttpClient(handler), options, Microsoft.Extensions.Logging.Abstractions.NullLogger<GraphMessengerClient>.Instance);
            var pages = await graph.ListPagesAsync("user-token");
            var page = Assert.Single(pages!);
            Assert.Equal(("869957749543347", "Tambay sa Kalye", "page-tok"), (page.Id, page.Name, page.Token));
            Assert.Equal(1, handler.Paths.Count(p => p.EndsWith("/869957749543347")));                                                         // listed once, not twice

            // A Page that is in the usual list and in the permissions is shown once.
            var both = new StubHandler(r => r.RequestUri!.AbsolutePath switch
            {
                var p when p.EndsWith("/me/accounts") => (HttpStatusCode.OK, "{\"data\":[{\"id\":\"P1\",\"name\":\"One\",\"access_token\":\"t1\"}]}"),
                var p when p.EndsWith("/debug_token") => (HttpStatusCode.OK, "{\"data\":{\"granular_scopes\":[{\"scope\":\"pages_messaging\",\"target_ids\":[\"P1\"]}]}}"),
                _ => (HttpStatusCode.NotFound, "{}"),
            });
            var one = await new GraphMessengerClient(new HttpClient(both), options, Microsoft.Extensions.Logging.Abstractions.NullLogger<GraphMessengerClient>.Instance).ListPagesAsync("user-token");
            Assert.Equal("P1", Assert.Single(one!).Id);

            // When Meta answers neither way, the result is null (a refusal), not an empty list.
            var down = new StubHandler(_ => (HttpStatusCode.BadRequest, "{\"error\":{\"message\":\"(#100) not allowed\",\"type\":\"OAuthException\",\"code\":100,\"error_subcode\":33}}")); // the reason is read for the log, never thrown
            Assert.Null(await new GraphMessengerClient(new HttpClient(down), options, Microsoft.Extensions.Logging.Abstractions.NullLogger<GraphMessengerClient>.Instance).ListPagesAsync("user-token"));
        }
        finally { ClearSettings(); }
    }

    [Fact]
    public void The_approved_setting_is_off_unless_it_is_true()
    {
        try
        {
            Environment.SetEnvironmentVariable("Messenger__Approved", null);
            Assert.False(new MessengerOptions(Config()).Approved);
            Environment.SetEnvironmentVariable("Messenger__Approved", "yes please");
            Assert.False(new MessengerOptions(Config()).Approved);
            Environment.SetEnvironmentVariable("Messenger__Approved", "TRUE");
            Assert.True(new MessengerOptions(Config()).Approved);
        }
        finally { Environment.SetEnvironmentVariable("Messenger__Approved", null); }
    }

    private static readonly string[] SettingNames = ["Messenger__Approved", "Messenger__ConfigId", "Messenger__VerifyToken", "Messenger__AppSecret", "Messenger__AppId", "Messenger__TokenKey", "Messenger__PageAccessToken", "Messenger__ShopSlug"];

    private static void SetConnectSettings()
    {
        foreach (var n in SettingNames) Environment.SetEnvironmentVariable(n, null);
        Environment.SetEnvironmentVariable("Messenger__VerifyToken", Verify);
        Environment.SetEnvironmentVariable("Messenger__AppSecret", Secret);
        Environment.SetEnvironmentVariable("Messenger__AppId", AppId);
        Environment.SetEnvironmentVariable("Messenger__TokenKey", Key);
    }

    /// <summary>The Messenger settings as they are in the environment right now (the real app reads them the same way).</summary>
    private static Microsoft.Extensions.Configuration.IConfiguration Config()
    {
        var config = new Microsoft.Extensions.Configuration.ConfigurationManager();
        foreach (var n in SettingNames) config[n.Replace("__", ":")] = Environment.GetEnvironmentVariable(n);
        return config;
    }

    private static void ClearSettings() { foreach (var n in SettingNames) Environment.SetEnvironmentVariable(n, null); }

    private static string Body(string pageId, string psid, string mid, string text) =>
        "{\"object\":\"page\",\"entry\":[{\"id\":\"" + pageId + "\",\"time\":1,\"messaging\":[{\"sender\":{\"id\":\"" + psid + "\"},\"recipient\":{\"id\":\"" + pageId + "\"},\"timestamp\":1,\"message\":{\"mid\":\"" + mid + "\",\"text\":\"" + text + "\"}}]}]}";

    private static Task<HttpResponseMessage> PostSignedAsync(HttpClient client, string body)
    {
        var bytes = Encoding.UTF8.GetBytes(body);
        var request = new HttpRequestMessage(HttpMethod.Post, "/webhooks/messenger") { Content = new ByteArrayContent(bytes) };
        request.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
        request.Headers.Add("X-Hub-Signature-256", MessengerSignature.Sign(Secret, bytes));
        return client.SendAsync(request);
    }

    private static async Task<(HttpClient Client, Guid BusinessId)> RegisterShopAsync(WebApplicationFactory<Program> factory, TestDbFactory db, bool aiAddon)
    {
        var email = $"own{Guid.NewGuid():N}@example.com";
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = true });
        var form = new Dictionary<string, string>
        {
            ["Input.BusinessName"] = "Shop " + Guid.NewGuid().ToString("N")[..6], ["Input.OwnerName"] = "Owner", ["Input.Email"] = email,
            ["Input.Password"] = "correct-horse-battery", ["Input.Accept"] = "true", ["Input.Industry"] = "barbershop",
        };
        await HttpApp.PostFormAsync(client, "/Register", form);
        await using var ctx = db.CreateDbContext();
        var businessId = await ctx.AdminUsers.Where(u => u.Email == email).Select(u => u.BusinessId).SingleAsync();
        if (aiAddon) await ctx.Businesses.Where(b => b.Id == businessId).ExecuteUpdateAsync(u => u.SetProperty(b => b.AiAddonUntilUtc, DateTime.UtcNow.AddDays(30)));
        return (client, businessId);
    }

    private static async Task<string> StartConnectAsync(HttpClient client)
    {
        var response = await client.GetAsync("/Admin/Messenger?handler=Start");
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var location = response.Headers.Location!.ToString();
        Assert.StartsWith("https://www.facebook.com/", location);
        Assert.Contains($"client_id={AppId}", location);
        Assert.Contains(Uri.EscapeDataString("/Admin/MessengerReturn"), location);
        Assert.Contains("pages_messaging", location);
        return HttpUtility.ParseQueryString(new Uri(location).Query)["state"]!;
    }

    [Fact]
    public void The_cipher_round_trips_and_refuses_a_changed_value_another_purpose_or_another_key()
    {
        Environment.SetEnvironmentVariable("Messenger__TokenKey", Key);
        try
        {
            var config = Config();
            var cipher = new MessengerCipher(new MessengerOptions(config));
            var sealedText = cipher.Protect("secret-page-token", "purpose-a");
            Assert.DoesNotContain("secret-page-token", sealedText);
            Assert.Equal("secret-page-token", cipher.Unprotect(sealedText, "purpose-a"));
            Assert.NotEqual(sealedText, cipher.Protect("secret-page-token", "purpose-a"));                                // a new random nonce each time
            Assert.Null(cipher.Unprotect(sealedText, "purpose-b"));                                                       // tied to its purpose
            Assert.Null(cipher.Unprotect(sealedText[..^4] + "AAAA", "purpose-a"));                                        // changed
            Assert.Null(cipher.Unprotect("not base64!", "purpose-a"));
            Assert.Null(cipher.Unprotect("", "purpose-a"));
            Assert.Null(cipher.Unprotect(null, "purpose-a"));

            Environment.SetEnvironmentVariable("Messenger__TokenKey", "another-long-secret-phrase-entirely");
            var other = new MessengerCipher(new MessengerOptions(Config()));
            Assert.Null(other.Unprotect(sealedText, "purpose-a"));                                                        // another key

            Environment.SetEnvironmentVariable("Messenger__TokenKey", "short");
            var weak = new MessengerCipher(new MessengerOptions(Config()));
            Assert.False(weak.IsAvailable);                                                                               // a short key is not accepted
            Assert.Throws<InvalidOperationException>(() => weak.Protect("x", "p"));
        }
        finally { Environment.SetEnvironmentVariable("Messenger__TokenKey", null); }
    }

    [Fact]
    public async Task The_owner_connects_a_Page_it_answers_for_that_shop_only_and_disconnecting_stops_it()
    {
        SetConnectSettings();
        try
        {
            var db = new TestDbFactory(await TestDb.ConnectionStringAsync());
            var (root, _) = await HttpApp.StartAsync();
            using var _r = root;
            var graph = new FakeGraph();
            var sender = new CapturingMessenger();
            var model = new FakeAiModel((_, _) => FakeAiModel.Reply("Hello from the assistant"));
            var factory = root.WithWebHostBuilder(b => b.ConfigureServices(s => { s.AddSingleton<IAiModel>(model); s.AddSingleton<IMessengerGraph>(graph); s.AddSingleton<IMessengerSender>(sender); }));

            await using (var clean = db.CreateDbContext()) await clean.MessengerConnections.ExecuteDeleteAsync();

            // Without the AI add-on the shop can not connect anything.
            var (noAi, _) = await RegisterShopAsync(factory, db, aiAddon: false);
            Assert.Contains(Text.MessengerNeedsAi, WebUtility.HtmlDecode(await noAi.GetStringAsync("/Admin/Messenger")));
            var refused = await noAi.GetAsync("/Admin/Messenger?handler=Start");
            Assert.Equal(HttpStatusCode.Redirect, refused.StatusCode);
            Assert.DoesNotContain("facebook.com", refused.Headers.Location!.ToString());

            var (owner, businessId) = await RegisterShopAsync(factory, db, aiAddon: true);

            // Connect is a link, not a form: the site's policy lets a form post only to its own address, so a form that ends at facebook.com would be stopped by the browser.
            var startPage = WebUtility.HtmlDecode(await owner.GetStringAsync("/Admin/Messenger"));
            Assert.Contains("href=\"/Admin/Messenger?handler=Start\"", startPage);
            Assert.Contains(Text.MessengerReviewNote, startPage);                                                          // shops are told it only answers testers until Meta approves
            Assert.DoesNotContain("handler=Connect", startPage);

            // Coming back with the wrong state, or the wrong code, connects nothing.
            await StartConnectAsync(owner);
            var wrongState = await owner.GetAsync("/Admin/MessengerReturn?code=good&state=wrong");
            Assert.Equal(HttpStatusCode.Redirect, wrongState.StatusCode);
            Assert.DoesNotContain("PAGE1", WebUtility.HtmlDecode(await owner.GetStringAsync("/Admin/Messenger")));
            var state = await StartConnectAsync(owner);
            await owner.GetAsync($"/Admin/MessengerReturn?code=bad&state={state}");
            Assert.DoesNotContain("Juan", WebUtility.HtmlDecode(await owner.GetStringAsync("/Admin/Messenger")));
            var replay = await owner.GetAsync($"/Admin/MessengerReturn?code=good&state={state}");                         // the state was used up
            Assert.DoesNotContain("Juan", WebUtility.HtmlDecode(await owner.GetStringAsync("/Admin/Messenger")));
            Assert.Equal(HttpStatusCode.Redirect, replay.StatusCode);

            // The right way: Facebook sends them back, they see their Pages and choose one.
            state = await StartConnectAsync(owner);
            Assert.Equal(HttpStatusCode.Redirect, (await owner.GetAsync($"/Admin/MessengerReturn?code=good&state={state}")).StatusCode);
            var choose = WebUtility.HtmlDecode(await owner.GetStringAsync("/Admin/Messenger"));
            Assert.Contains(Text.MessengerChoose, choose);
            Assert.Contains("Juan's Barbers", choose);
            Assert.Contains("Second Page", choose);
            Assert.DoesNotContain("tok-1", choose);                                                                        // a token never reaches the page
            Assert.Empty(graph.Subscribed);

            Assert.Equal(HttpStatusCode.Redirect, (await HttpApp.PostFormAsync(owner, "/Admin/Messenger?handler=Choose", new() { ["id"] = "PAGE1" }, tokenPage: "/Admin/Messenger")).StatusCode);
            Assert.Equal(new[] { "PAGE1" }, graph.Subscribed);
            await using (var check = db.CreateDbContext())
            {
                var row = await check.MessengerConnections.AsNoTracking().SingleAsync(c => c.BusinessId == businessId);
                Assert.Equal("PAGE1", row.PageId);
                Assert.Equal("Juan's Barbers", row.PageName);
                Assert.NotEqual("tok-1", row.PageTokenEnc);
                Assert.DoesNotContain("tok-1", row.PageTokenEnc);                                                          // saved encrypted
            }
            var done = WebUtility.HtmlDecode(await owner.GetStringAsync("/Admin/Messenger"));
            Assert.Contains("Juan's Barbers", done);
            Assert.DoesNotContain(Text.MessengerChoose, done);                                                             // the choice was used up

            // A message to that Page is answered as this shop with that Page's token; a Page nobody connected is not.
            var signedClient = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
            Assert.Equal(HttpStatusCode.OK, (await PostSignedAsync(signedClient, Body("PAGE1", "U1", "m1", "hello"))).StatusCode);
            var sent = Assert.Single(sender.Sent);
            Assert.Equal(("tok-1", "U1", "Hello from the assistant"), sent);
            Assert.Equal(HttpStatusCode.OK, (await PostSignedAsync(signedClient, Body("PAGE2", "U2", "m2", "hello"))).StatusCode);
            Assert.Single(sender.Sent);
            await using (var check = db.CreateDbContext())
                Assert.True(await check.MessengerThreads.AnyAsync(t => t.BusinessId == businessId && t.Psid == "U1"));

            // Another shop can not take the same Page, and can not disconnect it.
            var (other, otherId) = await RegisterShopAsync(factory, db, aiAddon: true);
            state = await StartConnectAsync(other);
            await other.GetAsync($"/Admin/MessengerReturn?code=good&state={state}");
            await HttpApp.PostFormAsync(other, "/Admin/Messenger?handler=Choose", new() { ["id"] = "PAGE1" }, tokenPage: "/Admin/Messenger");
            await using (var check = db.CreateDbContext())
                Assert.Equal(businessId, (await check.MessengerConnections.AsNoTracking().SingleAsync(c => c.PageId == "PAGE1")).BusinessId);
            Assert.Equal(HttpStatusCode.NotFound, (await HttpApp.PostFormAsync(other, "/Admin/Messenger?handler=Disconnect", new() { ["id"] = "PAGE1" }, tokenPage: "/Admin/Messenger")).StatusCode);
            await using (var check = db.CreateDbContext())
                Assert.Equal(1, await check.MessengerConnections.CountAsync(c => c.PageId == "PAGE1"));

            // Disconnecting removes it, tells Meta, and the Page is no longer answered.
            Assert.Equal(HttpStatusCode.Redirect, (await HttpApp.PostFormAsync(owner, "/Admin/Messenger?handler=Disconnect", new() { ["id"] = "PAGE1" }, tokenPage: "/Admin/Messenger")).StatusCode);
            Assert.Equal(("PAGE1", "tok-1"), Assert.Single(graph.Unsubscribed));
            await using (var check = db.CreateDbContext())
                Assert.False(await check.MessengerConnections.AnyAsync(c => c.BusinessId == businessId));
            Assert.Equal(HttpStatusCode.OK, (await PostSignedAsync(signedClient, Body("PAGE1", "U1", "m3", "hello again"))).StatusCode);
            Assert.Single(sender.Sent);
            Assert.NotEqual(Guid.Empty, otherId);
        }
        finally { ClearSettings(); }
    }

    [Fact]
    public async Task A_shop_can_connect_a_few_Pages_but_not_more_and_a_failed_subscription_saves_nothing()
    {
        SetConnectSettings();
        try
        {
            var db = new TestDbFactory(await TestDb.ConnectionStringAsync());
            var (root, _) = await HttpApp.StartAsync();
            using var _r = root;
            var graph = new FakeGraph();
            var factory = root.WithWebHostBuilder(b => b.ConfigureServices(s => { s.AddSingleton<IAiModel>(new FakeAiModel((_, _) => FakeAiModel.Reply("x"))); s.AddSingleton<IMessengerGraph>(graph); }));
            await using (var clean = db.CreateDbContext()) await clean.MessengerConnections.ExecuteDeleteAsync();
            var (_, businessId) = await RegisterShopAsync(factory, db, aiAddon: true);

            using var scope = factory.Services.CreateScope();
            var connect = scope.ServiceProvider.GetRequiredService<MessengerConnectService>();

            graph.SubscribeOk = false;
            var failed = await connect.ConnectAsync(businessId, new FacebookPage("PX", "Failing", "tok-x"));
            Assert.False(failed.IsSuccess);
            Assert.Equal(Text.MessengerSubscribeFailed, failed.Message);
            Assert.Empty(await connect.ListAsync(businessId));
            graph.SubscribeOk = true;

            for (var i = 1; i <= MessengerConnectService.MaxPagesPerShop; i++)
                Assert.True((await connect.ConnectAsync(businessId, new FacebookPage($"PG{i}", $"Page {i}", $"tok-{i}"))).IsSuccess);
            var tooMany = await connect.ConnectAsync(businessId, new FacebookPage("PG99", "One too many", "tok-99"));
            Assert.False(tooMany.IsSuccess);
            Assert.Equal(Text.MessengerTooManyPages(MessengerConnectService.MaxPagesPerShop), tooMany.Message);
            Assert.True((await connect.ConnectAsync(businessId, new FacebookPage("PG1", "Page 1 renamed", "tok-new"))).IsSuccess); // connecting the same Page again just refreshes it
            Assert.Equal(MessengerConnectService.MaxPagesPerShop, (await connect.ListAsync(businessId)).Count);
            Assert.Equal("tok-new", (await connect.FindAsync("PG1"))!.Value.Token);

            await using (var ctx = db.CreateDbContext())
                await ctx.Businesses.Where(b => b.Id == businessId).ExecuteUpdateAsync(u => u.SetProperty(b => b.AiAddonUntilUtc, (DateTime?)null));
            Assert.Equal(Text.MessengerNeedsAi, (await connect.ConnectAsync(businessId, new FacebookPage("PG1", "Again", "t"))).Message);   // the add-on ended
            Assert.Null(await connect.FindAsync("NOT-CONNECTED"));
        }
        finally { ClearSettings(); }
    }

    [Fact]
    public async Task Without_the_connect_settings_the_page_says_so_and_cannot_start_a_connection()
    {
        ClearSettings();
        var db = new TestDbFactory(await TestDb.ConnectionStringAsync());
        var (root, _) = await HttpApp.StartAsync();
        using var _r = root;
        var (owner, _) = await RegisterShopAsync(root, db, aiAddon: true);
        Assert.Contains(Text.MessengerNotSetUp, WebUtility.HtmlDecode(await owner.GetStringAsync("/Admin/Messenger")));
        Assert.Equal(HttpStatusCode.NotFound, (await owner.GetAsync("/Admin/Messenger?handler=Start")).StatusCode);
        Assert.Equal(HttpStatusCode.Redirect, (await HttpApp.NewClient(root).GetAsync("/Admin/Messenger")).StatusCode);                  // signed out: sent to sign in
    }
}
