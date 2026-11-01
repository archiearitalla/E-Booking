using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using VeskaFlow.Web.Data;
using Microsoft.EntityFrameworkCore;

namespace VeskaFlow.Web.Services;

/// <summary>
/// The Messenger settings (BUSINESS_RULES 6.y23). All of them come from settings, never from the code, and the whole feature is off until every
/// one of the four is filled in: <c>Messenger:VerifyToken</c> (a word you choose and also type in Meta's webhook setup), <c>Messenger:AppSecret</c>
/// (the app secret, used to check that a message really comes from Meta), <c>Messenger:PageAccessToken</c> (to send replies as the Page) and
/// <c>Messenger:ShopSlug</c> (the one shop this Page belongs to). <c>Messenger:GraphVersion</c> is the version of Meta's Graph API.
/// </summary>
public sealed class MessengerOptions(IConfiguration config)
{
    public string VerifyToken => Clean("Messenger:VerifyToken");
    public string AppSecret => Clean("Messenger:AppSecret");
    public string PageAccessToken => Clean("Messenger:PageAccessToken");
    public string ShopSlug => Clean("Messenger:ShopSlug");
    public string GraphVersion => Clean("Messenger:GraphVersion") is { Length: > 0 } v ? v : "v21.0";

    public bool IsEnabled => VerifyToken.Length > 0 && AppSecret.Length > 0 && PageAccessToken.Length > 0 && ShopSlug.Length > 0;

    private string Clean(string key) => (config[key] ?? "").Trim();
}

public static class MessengerSignature
{
    /// <summary>True when <paramref name="header"/> (<c>sha256=&lt;hex&gt;</c>, as Meta sends it) is the HMAC-SHA256 of the body made with the app secret.</summary>
    public static bool IsValid(string appSecret, ReadOnlySpan<byte> body, string? header)
    {
        if (appSecret.Length == 0 || header is null || !header.StartsWith("sha256=", StringComparison.OrdinalIgnoreCase)) return false;
        byte[] given;
        try { given = Convert.FromHexString(header[7..]); }
        catch (FormatException) { return false; }
        var expected = HMACSHA256.HashData(Encoding.UTF8.GetBytes(appSecret), body);
        return CryptographicOperations.FixedTimeEquals(expected, given);
    }

    public static string Sign(string appSecret, ReadOnlySpan<byte> body) =>
        "sha256=" + Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(appSecret), body)).ToLowerInvariant();
}

/// <summary>Sends a text message to a person as the Page. A fake one is used in tests.</summary>
public interface IMessengerSender
{
    Task<bool> SendTextAsync(string psid, string text, CancellationToken ct = default);
}

public sealed class GraphMessengerSender(HttpClient http, MessengerOptions options, ILogger<GraphMessengerSender> log) : IMessengerSender
{
    public async Task<bool> SendTextAsync(string psid, string text, CancellationToken ct = default)
    {
        if (!options.IsEnabled) return false;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, $"https://graph.facebook.com/{options.GraphVersion}/me/messages")
            {
                Content = JsonContent.Create(new { recipient = new { id = psid }, messaging_type = "RESPONSE", message = new { text } }),
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", options.PageAccessToken); // never in the address, so it is not logged
            using var response = await http.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode) log.LogWarning("Messenger did not accept a reply: HTTP {Status}", (int)response.StatusCode); // no names, ids or text in the log
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            log.LogWarning("A Messenger reply could not be sent: {Reason}", ex.GetType().Name);
            return false;
        }
    }
}

public enum MessengerOutcome { Disabled, BadSignature, Handled }

/// <summary>
/// Answers messages that arrive at the shop's Facebook Page with the AI receptionist (the same one as on the booking page, with the same rules and limits).
/// Only a text message from a person is answered; an echo of the Page's own message, a picture and anything else is ignored. Meta delivers a message again
/// when it is not answered in time, so the id of the last message of each person is kept and the same one is never answered twice.
/// </summary>
public sealed class MessengerService(
    MessengerOptions options, IDbContextFactory<AppDbContext> dbFactory, TimeProvider time, AiReceptionistService assistant, IMessengerSender sender, ILogger<MessengerService> log)
{
    public const int MaxBodyBytes = 64 * 1024;
    public const int MaxReplyChars = 1900; // Messenger takes up to 2000
    private const int KeepDays = 30;

    public async Task<MessengerOutcome> HandleAsync(byte[] body, string? signature, string baseUrl, CancellationToken ct)
    {
        if (!options.IsEnabled) return MessengerOutcome.Disabled;
        if (!MessengerSignature.IsValid(options.AppSecret, body, signature)) return MessengerOutcome.BadSignature;

        foreach (var (psid, mid, text) in ReadTexts(body))
        {
            try { await ReplyAsync(psid, mid, text, baseUrl, ct); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                log.LogWarning("A Messenger message could not be answered: {Reason}", ex.GetType().Name);
            }
        }
        return MessengerOutcome.Handled;
    }

    private static List<(string Psid, string Mid, string Text)> ReadTexts(byte[] body)
    {
        var found = new List<(string, string, string)>();
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (!doc.RootElement.TryGetProperty("object", out var obj) || obj.GetString() != "page") return found;
            if (!doc.RootElement.TryGetProperty("entry", out var entries) || entries.ValueKind != JsonValueKind.Array) return found;
            foreach (var entry in entries.EnumerateArray())
            {
                if (!entry.TryGetProperty("messaging", out var events) || events.ValueKind != JsonValueKind.Array) continue;
                foreach (var e in events.EnumerateArray())
                {
                    if (!e.TryGetProperty("message", out var message) || message.ValueKind != JsonValueKind.Object) continue;
                    if (message.TryGetProperty("is_echo", out var echo) && echo.ValueKind == JsonValueKind.True) continue;
                    var psid = e.TryGetProperty("sender", out var s) && s.TryGetProperty("id", out var id) ? id.GetString() : null;
                    var mid = message.TryGetProperty("mid", out var m) ? m.GetString() : null;
                    var text = message.TryGetProperty("text", out var t) ? t.GetString() : null;
                    if (string.IsNullOrWhiteSpace(psid) || psid.Length > 100 || string.IsNullOrWhiteSpace(mid) || mid.Length > 200 || string.IsNullOrWhiteSpace(text)) continue;
                    found.Add((psid, mid, text));
                }
            }
        }
        catch (JsonException) { /* not a message we understand: nothing to answer */ }
        return found;
    }

    private async Task ReplyAsync(string psid, string mid, string text, string baseUrl, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var shopId = await db.Businesses.AsNoTracking().Where(b => b.Slug == options.ShopSlug).Select(b => (Guid?)b.Id).FirstOrDefaultAsync(ct);
        if (shopId is null) return; // the shop in the settings does not exist

        var now = time.GetUtcNow().UtcDateTime;
        var thread = await db.MessengerThreads.FirstOrDefaultAsync(t => t.BusinessId == shopId && t.Psid == psid, ct);
        if (thread?.LastMid == mid) return; // Meta delivered it again: already answered
        if (thread is null)
        {
            // People of this shop who have not written for a month are forgotten (their conversations are removed the same way).
            await db.MessengerThreads.Where(t => t.BusinessId == shopId && t.UpdatedAtUtc < now.AddDays(-KeepDays)).ExecuteDeleteAsync(ct);
            thread = new MessengerThread { BusinessId = shopId.Value, Psid = psid };
            db.MessengerThreads.Add(thread);
        }

        var result = await assistant.SendAsync(options.ShopSlug, thread.ChatId, text, baseUrl, ct);
        if (!result.IsSuccess && thread.ChatId is not null && result.Failure is FailureKind.NotFound or FailureKind.PolicyViolation && result.Message != Text.AiChatUnavailable)
            result = await assistant.SendAsync(options.ShopSlug, null, text, baseUrl, ct); // the old conversation is gone or full: start a new one

        thread.LastMid = mid;
        thread.UpdatedAtUtc = now;
        string? answer = null;
        if (result.IsSuccess) { thread.ChatId = result.Value!.ChatId; answer = result.Value.Text; }
        else if (result.Failure is FailureKind.Validation or FailureKind.PolicyViolation) answer = result.Message; // a short, friendly reason from the assistant
        // NotFound (the shop has no AI add-on, or is closed): say nothing, the booking page is still there.
        await db.SaveChangesAsync(ct);

        if (!string.IsNullOrWhiteSpace(answer))
            await sender.SendTextAsync(psid, answer.Length > MaxReplyChars ? answer[..MaxReplyChars] : answer, ct);
    }
}
