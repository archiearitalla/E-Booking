using System.Net;
using System.Text;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using VeskaFlow.Web.Services;

namespace VeskaFlow.Web.Tests;

/// <summary>Messenger (BUSINESS_RULES 6.y23): off until the settings are filled in, only believes Meta's signature, and answers each message once. Meta's own servers are never called.</summary>
[Collection("http")]
public class MessengerTests
{
    private const string Secret = "app-secret-for-tests";
    private const string Verify = "verify-me";

    private sealed class CapturingMessenger : IMessengerSender
    {
        public List<(string Psid, string Text)> Sent { get; } = [];
        public Task<bool> SendTextAsync(string psid, string text, CancellationToken ct = default) { Sent.Add((psid, text)); return Task.FromResult(true); }
    }

    private static string Body(string psid, string mid, string? text, bool echo = false, string obj = "page") =>
        "{\"object\":\"" + obj + "\",\"entry\":[{\"id\":\"PAGE\",\"time\":1,\"messaging\":[{\"sender\":{\"id\":\"" + psid + "\"},\"recipient\":{\"id\":\"PAGE\"},\"timestamp\":1,\"message\":{\"mid\":\"" + mid + "\""
        + (text is null ? ",\"attachments\":[{\"type\":\"image\"}]" : ",\"text\":\"" + text + "\"") + (echo ? ",\"is_echo\":true" : "") + "}}]}]}";

    private static Task<HttpResponseMessage> PostAsync(HttpClient client, string body, string? signature = null, bool sign = true)
    {
        var bytes = Encoding.UTF8.GetBytes(body);
        var request = new HttpRequestMessage(HttpMethod.Post, "/webhooks/messenger") { Content = new ByteArrayContent(bytes) };
        request.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
        if (sign) request.Headers.Add("X-Hub-Signature-256", signature ?? MessengerSignature.Sign(Secret, bytes));
        return client.SendAsync(request);
    }

    [Fact]
    public void A_signature_is_only_valid_for_the_same_body_and_secret_in_the_form_Meta_sends()
    {
        var body = Encoding.UTF8.GetBytes("{\"a\":1}");
        var good = MessengerSignature.Sign(Secret, body);
        Assert.StartsWith("sha256=", good);
        Assert.True(MessengerSignature.IsValid(Secret, body, good));
        Assert.True(MessengerSignature.IsValid(Secret, body, good.ToUpperInvariant().Replace("SHA256=", "sha256=")));          // hex case does not matter
        Assert.False(MessengerSignature.IsValid(Secret, Encoding.UTF8.GetBytes("{\"a\":2}"), good));                          // another body
        Assert.False(MessengerSignature.IsValid("other-secret", body, good));                                                  // another secret
        Assert.False(MessengerSignature.IsValid(Secret, body, null));
        Assert.False(MessengerSignature.IsValid(Secret, body, ""));
        Assert.False(MessengerSignature.IsValid(Secret, body, "sha256="));
        Assert.False(MessengerSignature.IsValid(Secret, body, "sha256=zz"));                                                   // not hex
        Assert.False(MessengerSignature.IsValid(Secret, body, good[7..]));                                                     // no prefix
        Assert.False(MessengerSignature.IsValid("", body, MessengerSignature.Sign("", body)));                                 // no secret set: nothing is valid
    }

    [Fact]
    public async Task Without_the_settings_the_webhook_does_not_exist()
    {
        var (factory, client) = await HttpApp.StartAsync();
        using var _f = factory;
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/webhooks/messenger?hub.mode=subscribe&hub.verify_token={Verify}&hub.challenge=x")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await PostAsync(client, Body("P1", "m1", "hello"))).StatusCode);
    }

    [Fact]
    public async Task It_checks_the_verify_token_believes_only_a_signed_message_and_answers_each_message_once()
    {
        var shop = await Seed.CreateShopAsync();
        var db = new TestDbFactory(await TestDb.ConnectionStringAsync());
        string slug;
        await using (var ctx = db.CreateDbContext())
        {
            await ctx.Businesses.Where(b => b.Id == shop.BusinessId).ExecuteUpdateAsync(u => u.SetProperty(b => b.AiAddonUntilUtc, DateTime.UtcNow.AddDays(30)));
            slug = await ctx.Businesses.Where(b => b.Id == shop.BusinessId).Select(b => b.Slug).SingleAsync();
        }
        var set = new Dictionary<string, string> { ["Messenger__VerifyToken"] = Verify, ["Messenger__AppSecret"] = Secret, ["Messenger__PageAccessToken"] = "page-token", ["Messenger__ShopSlug"] = slug };
        foreach (var (k, v) in set) Environment.SetEnvironmentVariable(k, v);
        try
        {
            var (root, _) = await HttpApp.StartAsync();
            using var _r = root;
            var model = new FakeAiModel((_, _) => FakeAiModel.Reply("Hello from the assistant"));
            var sender = new CapturingMessenger();
            var client = root.WithWebHostBuilder(b => b.ConfigureServices(s => { s.AddSingleton<IAiModel>(model); s.AddSingleton<IMessengerSender>(sender); }))
                .CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = true });

            // Meta's one-time check of the address.
            var ok = await client.GetAsync($"/webhooks/messenger?hub.mode=subscribe&hub.verify_token={Verify}&hub.challenge=CHALLENGE123");
            Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
            Assert.Equal("CHALLENGE123", await ok.Content.ReadAsStringAsync());
            Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/webhooks/messenger?hub.mode=subscribe&hub.verify_token=wrong&hub.challenge=x")).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync($"/webhooks/messenger?hub.mode=other&hub.verify_token={Verify}&hub.challenge=x")).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/webhooks/messenger")).StatusCode);

            // A message that is not signed with the app secret is refused and never reaches the model or the person.
            Assert.Equal(HttpStatusCode.Forbidden, (await PostAsync(client, Body("P1", "m1", "hello"), sign: false)).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await PostAsync(client, Body("P1", "m1", "hello"), signature: MessengerSignature.Sign("other", Encoding.UTF8.GetBytes("x")))).StatusCode);
            Assert.Empty(sender.Sent);
            Assert.Equal(0, model.Calls);

            // A signed message is answered, once, and the conversation is remembered for that person.
            var first = await PostAsync(client, Body("P1", "m1", "hello"));
            Assert.Equal(HttpStatusCode.OK, first.StatusCode);
            Assert.Equal("EVENT_RECEIVED", await first.Content.ReadAsStringAsync());
            var answer = Assert.Single(sender.Sent);
            Assert.Equal(("P1", "Hello from the assistant"), answer);
            await using (var check = db.CreateDbContext())
            {
                var thread = await check.MessengerThreads.AsNoTracking().SingleAsync(t => t.BusinessId == shop.BusinessId && t.Psid == "P1");
                Assert.NotNull(thread.ChatId);
                Assert.Equal("m1", thread.LastMid);
            }

            // Meta sends it again because it was slow: not answered twice. A new message continues the same conversation.
            Assert.Equal(HttpStatusCode.OK, (await PostAsync(client, Body("P1", "m1", "hello"))).StatusCode);
            Assert.Single(sender.Sent);
            Assert.Equal(1, model.Calls);
            Assert.Equal(HttpStatusCode.OK, (await PostAsync(client, Body("P1", "m2", "and then?"))).StatusCode);
            Assert.Equal(2, sender.Sent.Count);
            Assert.Equal(3, model.Conversations[^1].Count);                         // hello, the answer, and then?
            await using (var check = db.CreateDbContext())
                Assert.Single(await check.MessengerThreads.Where(t => t.BusinessId == shop.BusinessId && t.Psid == "P1").ToListAsync());

            // Another person has their own conversation.
            Assert.Equal(HttpStatusCode.OK, (await PostAsync(client, Body("P2", "m9", "hi"))).StatusCode);
            Assert.Equal("P2", sender.Sent[^1].Psid);
            Assert.Single(model.Conversations[^1]);

            // Echoes of the Page's own messages, pictures, other objects and nonsense are ignored (still a quick 200 for a real shape, never a crash).
            var before = sender.Sent.Count;
            Assert.Equal(HttpStatusCode.OK, (await PostAsync(client, Body("P1", "m3", "from the page", echo: true))).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await PostAsync(client, Body("P1", "m4", null))).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await PostAsync(client, Body("P1", "m5", "hi", obj: "instagram"))).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await PostAsync(client, "not json")).StatusCode);
            Assert.Equal(before, sender.Sent.Count);

            // Without the AI add-on the shop says nothing (the booking page is still there), and a huge body is refused.
            await using (var ctx = db.CreateDbContext())
                await ctx.Businesses.Where(b => b.Id == shop.BusinessId).ExecuteUpdateAsync(u => u.SetProperty(b => b.AiAddonUntilUtc, (DateTime?)null));
            Assert.Equal(HttpStatusCode.OK, (await PostAsync(client, Body("P1", "m6", "anyone there?"))).StatusCode);
            Assert.Equal(before, sender.Sent.Count);
            Assert.Equal(HttpStatusCode.RequestEntityTooLarge, (await PostAsync(client, new string('x', MessengerService.MaxBodyBytes + 10))).StatusCode);
        }
        finally
        {
            foreach (var k in set.Keys) Environment.SetEnvironmentVariable(k, null);
        }
    }
}
