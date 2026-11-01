using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using VeskaFlow.Web.Data;
using Microsoft.EntityFrameworkCore;

namespace VeskaFlow.Web.Services;

/// <summary>
/// The Messenger settings (BUSINESS_RULES 6.y23). All of them come from settings, never from the code. Two ways to use it, and both can be on:
/// <list type="bullet">
/// <item><b>One Page, set in the settings</b>: <c>Messenger:VerifyToken</c> (a word you choose and also type in Meta's webhook setup), <c>Messenger:AppSecret</c>
/// (the app secret, used to check that a message really comes from Meta), <c>Messenger:PageAccessToken</c> (to send replies as the Page) and <c>Messenger:ShopSlug</c>.</item>
/// <item><b>Each shop connects its own Pages</b> from Settings: <c>VerifyToken</c> and <c>AppSecret</c> as above, plus <c>Messenger:AppId</c> (the Meta app's id)
/// and <c>Messenger:TokenKey</c> (a long secret phrase the Page tokens are encrypted with before they are saved).</item>
/// </list>
/// <c>Messenger:GraphVersion</c> is the version of Meta's Graph API.
/// </summary>
public sealed class MessengerOptions(IConfiguration config)
{
    public string VerifyToken => Clean("Messenger:VerifyToken");
    public string AppSecret => Clean("Messenger:AppSecret");
    public string PageAccessToken => Clean("Messenger:PageAccessToken");
    public string ShopSlug => Clean("Messenger:ShopSlug");
    public string AppId => Clean("Messenger:AppId");
    public string TokenKey => Clean("Messenger:TokenKey");
    /// <summary>The Configuration ID of Facebook Login for Business (which permissions to ask for). When empty the classic <c>scope</c> list is used.</summary>
    public string ConfigId => Clean("Messenger:ConfigId");
    public string GraphVersion => Clean("Messenger:GraphVersion") is { Length: > 0 } v ? v : "v21.0";

    /// <summary>A Page set in the settings answers as one shop.</summary>
    public bool OnePageEnabled => VerifyToken.Length > 0 && AppSecret.Length > 0 && PageAccessToken.Length > 0 && ShopSlug.Length > 0;

    /// <summary>Shops may connect their own Pages. The key must be long enough to protect the saved tokens.</summary>
    public bool ConnectEnabled => VerifyToken.Length > 0 && AppSecret.Length > 0 && AppId.Length > 0 && TokenKey.Length >= 16;

    public bool IsEnabled => OnePageEnabled || ConnectEnabled;

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

/// <summary>
/// Encrypts a Page token (and the short list of Pages kept between two clicks) with AES-GCM and the <c>Messenger:TokenKey</c> setting. Each value is tied to a
/// <c>purpose</c> (for example the Page it belongs to), so one saved token can not be moved to another row and still be read.
/// </summary>
public sealed class MessengerCipher(MessengerOptions options)
{
    private const int NonceSize = 12, TagSize = 16;

    public bool IsAvailable => options.TokenKey.Length >= 16;

    public string Protect(string plain, string purpose)
    {
        if (!IsAvailable) throw new InvalidOperationException("Messenger:TokenKey is not set.");
        var data = Encoding.UTF8.GetBytes(plain);
        var nonce = RandomNumberGenerator.GetBytes(NonceSize);
        var cipher = new byte[data.Length];
        var tag = new byte[TagSize];
        using var aes = new AesGcm(Key(), TagSize);
        aes.Encrypt(nonce, data, cipher, tag, Encoding.UTF8.GetBytes(purpose));
        return Convert.ToBase64String([.. nonce, .. tag, .. cipher]);
    }

    /// <summary>The text, or null when it was changed, made with another key or purpose, or is not ours at all.</summary>
    public string? Unprotect(string? text, string purpose)
    {
        if (!IsAvailable || string.IsNullOrEmpty(text)) return null;
        try
        {
            var all = Convert.FromBase64String(text);
            if (all.Length < NonceSize + TagSize) return null;
            var nonce = all[..NonceSize];
            var tag = all[NonceSize..(NonceSize + TagSize)];
            var cipher = all[(NonceSize + TagSize)..];
            var plain = new byte[cipher.Length];
            using var aes = new AesGcm(Key(), TagSize);
            aes.Decrypt(nonce, cipher, tag, plain, Encoding.UTF8.GetBytes(purpose));
            return Encoding.UTF8.GetString(plain);
        }
        catch (Exception ex) when (ex is FormatException or CryptographicException) { return null; }
    }

    private byte[] Key() => SHA256.HashData(Encoding.UTF8.GetBytes(options.TokenKey));
}

/// <summary>Sends a text message to a person as a Page, with that Page's token. A fake one is used in tests.</summary>
public interface IMessengerSender
{
    Task<bool> SendTextAsync(string pageToken, string psid, string text, CancellationToken ct = default);
}

public sealed class GraphMessengerSender(HttpClient http, MessengerOptions options, ILogger<GraphMessengerSender> log) : IMessengerSender
{
    public async Task<bool> SendTextAsync(string pageToken, string psid, string text, CancellationToken ct = default)
    {
        if (!options.IsEnabled || pageToken.Length == 0) return false;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, $"https://graph.facebook.com/{options.GraphVersion}/me/messages")
            {
                Content = JsonContent.Create(new { recipient = new { id = psid }, messaging_type = "RESPONSE", message = new { text } }),
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", pageToken); // never in the address, so it is not logged
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

/// <summary>A Facebook Page the signed-in person manages, with the token to act as it.</summary>
public sealed record FacebookPage(string Id, string Name, string Token);

/// <summary>The calls to Meta needed to connect a Page. A fake one is used in tests.</summary>
public interface IMessengerGraph
{
    /// <summary>Turns the code Facebook sent back into a long-lived user token, or null.</summary>
    Task<string?> GetUserTokenAsync(string code, string redirectUri, CancellationToken ct = default);
    /// <summary>The Pages that person manages (each with its own token), or null when Meta refused.</summary>
    Task<IReadOnlyList<FacebookPage>?> ListPagesAsync(string userToken, CancellationToken ct = default);
    /// <summary>Asks Meta to send this Page's messages to our webhook.</summary>
    Task<bool> SubscribeAsync(string pageId, string pageToken, CancellationToken ct = default);
    Task UnsubscribeAsync(string pageId, string pageToken, CancellationToken ct = default);
}

public sealed class GraphMessengerClient(HttpClient http, MessengerOptions options, ILogger<GraphMessengerClient> log) : IMessengerGraph
{
    private string Base => $"https://graph.facebook.com/{options.GraphVersion}";

    public async Task<string?> GetUserTokenAsync(string code, string redirectUri, CancellationToken ct = default)
    {
        var shortLived = await TokenAsync($"client_id={Uri.EscapeDataString(options.AppId)}&redirect_uri={Uri.EscapeDataString(redirectUri)}&client_secret={Uri.EscapeDataString(options.AppSecret)}&code={Uri.EscapeDataString(code)}", ct);
        if (shortLived is null) return null;
        // A Page token made from a long-lived user token does not expire; one made from a short-lived token lasts about an hour.
        return await TokenAsync($"grant_type=fb_exchange_token&client_id={Uri.EscapeDataString(options.AppId)}&client_secret={Uri.EscapeDataString(options.AppSecret)}&fb_exchange_token={Uri.EscapeDataString(shortLived)}", ct);
    }

    private async Task<string?> TokenAsync(string query, CancellationToken ct)
    {
        try
        {
            using var response = await http.GetAsync($"{Base}/oauth/access_token?{query}", ct);
            if (!response.IsSuccessStatusCode) { log.LogWarning("Meta did not give a token: HTTP {Status}", (int)response.StatusCode); return null; }
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            return doc.RootElement.TryGetProperty("access_token", out var t) ? t.GetString() : null;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            log.LogWarning("Meta could not be reached for a token: {Reason}", ex.GetType().Name);
            return null;
        }
    }

    public async Task<IReadOnlyList<FacebookPage>?> ListPagesAsync(string userToken, CancellationToken ct = default)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, $"{Base}/me/accounts?fields=id,name,access_token&limit=50");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", userToken);
            using var response = await http.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode) { log.LogWarning("Meta did not list the Pages: HTTP {Status}", (int)response.StatusCode); return null; }
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            var pages = new List<FacebookPage>();
            if (doc.RootElement.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
                foreach (var p in data.EnumerateArray())
                {
                    var id = p.TryGetProperty("id", out var i) ? i.GetString() : null;
                    var name = p.TryGetProperty("name", out var n) ? n.GetString() : null;
                    var token = p.TryGetProperty("access_token", out var t) ? t.GetString() : null;
                    if (string.IsNullOrWhiteSpace(id) || id.Length > 100 || string.IsNullOrWhiteSpace(token)) continue;
                    var label = (string.IsNullOrWhiteSpace(name) ? id : name).Trim();
                    pages.Add(new FacebookPage(id, label.Length > 200 ? label[..200] : label, token));
                }
            return pages;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            log.LogWarning("Meta could not be reached for the Pages: {Reason}", ex.GetType().Name);
            return null;
        }
    }

    public async Task<bool> SubscribeAsync(string pageId, string pageToken, CancellationToken ct = default)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, $"{Base}/{Uri.EscapeDataString(pageId)}/subscribed_apps")
            {
                Content = new FormUrlEncodedContent(new Dictionary<string, string> { ["subscribed_fields"] = "messages" }),
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", pageToken);
            using var response = await http.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode) log.LogWarning("Meta did not subscribe the Page: HTTP {Status}", (int)response.StatusCode);
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            log.LogWarning("Meta could not be reached to subscribe a Page: {Reason}", ex.GetType().Name);
            return false;
        }
    }

    public async Task UnsubscribeAsync(string pageId, string pageToken, CancellationToken ct = default)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Delete, $"{Base}/{Uri.EscapeDataString(pageId)}/subscribed_apps");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", pageToken);
            using var response = await http.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode) log.LogWarning("Meta did not unsubscribe the Page: HTTP {Status}", (int)response.StatusCode);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            log.LogWarning("Meta could not be reached to unsubscribe a Page: {Reason}", ex.GetType().Name);
        }
    }
}

public sealed record MessengerPage(string PageId, string PageName, DateTime ConnectedAtUtc);

/// <summary>
/// A shop owner connecting their own Facebook Pages (BUSINESS_RULES 6.y23). Only for a shop with the AI add-on, a Page belongs to one shop only,
/// a shop may have a few Pages, and the Page token is saved encrypted. Every call names the shop from the signed-in owner's claims.
/// </summary>
public sealed class MessengerConnectService(
    IDbContextFactory<AppDbContext> dbFactory, TimeProvider time, MessengerOptions options, MessengerCipher cipher, IMessengerGraph graph, AiAccess ai, ILogger<MessengerConnectService> log)
{
    public const int MaxPagesPerShop = 5;

    /// <summary>Where Facebook sends the person: its own login and permission screen. <paramref name="state"/> is checked when they come back.</summary>
    public string AuthorizeUrl(string redirectUri, string state)
    {
        var url = $"https://www.facebook.com/{options.GraphVersion}/dialog/oauth?client_id={Uri.EscapeDataString(options.AppId)}&redirect_uri={Uri.EscapeDataString(redirectUri)}"
            + $"&state={Uri.EscapeDataString(state)}&response_type=code";
        // Facebook Login for Business asks for its saved configuration (the permissions are set in the Meta app); classic Facebook Login asks for them here.
        return options.ConfigId.Length > 0
            ? url + $"&config_id={Uri.EscapeDataString(options.ConfigId)}&override_default_response_type=true"
            : url + "&scope=pages_show_list,pages_messaging,pages_manage_metadata";
    }

    /// <summary>The Pages the person who just came back from Facebook manages.</summary>
    public async Task<ServiceResult<IReadOnlyList<FacebookPage>>> PagesFromCodeAsync(string code, string redirectUri, CancellationToken ct = default)
    {
        if (!options.ConnectEnabled) return ServiceResult<IReadOnlyList<FacebookPage>>.Fail(FailureKind.NotFound, Text.NotFound);
        var user = await graph.GetUserTokenAsync(code, redirectUri, ct);
        if (user is null) return ServiceResult<IReadOnlyList<FacebookPage>>.Fail(FailureKind.Validation, Text.MessengerFacebookRefused);
        var pages = await graph.ListPagesAsync(user, ct);
        if (pages is null) return ServiceResult<IReadOnlyList<FacebookPage>>.Fail(FailureKind.Validation, Text.MessengerFacebookRefused);
        if (pages.Count == 0) return ServiceResult<IReadOnlyList<FacebookPage>>.Fail(FailureKind.Validation, Text.MessengerNoPages);
        return ServiceResult<IReadOnlyList<FacebookPage>>.Ok(pages.Take(10).ToList());
    }

    public async Task<ServiceResult<MessengerPage>> ConnectAsync(Guid businessId, FacebookPage page, CancellationToken ct = default)
    {
        if (!options.ConnectEnabled) return ServiceResult<MessengerPage>.Fail(FailureKind.NotFound, Text.NotFound);
        if (!await ai.IsActiveAsync(businessId, ct)) return ServiceResult<MessengerPage>.Fail(FailureKind.PolicyViolation, Text.MessengerNeedsAi);

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var existing = await db.MessengerConnections.FirstOrDefaultAsync(c => c.PageId == page.Id, ct);
        if (existing is not null && existing.BusinessId != businessId)
            return ServiceResult<MessengerPage>.Fail(FailureKind.PolicyViolation, Text.MessengerPageTaken);
        if (existing is null && await db.MessengerConnections.CountAsync(c => c.BusinessId == businessId, ct) >= MaxPagesPerShop)
            return ServiceResult<MessengerPage>.Fail(FailureKind.PolicyViolation, Text.MessengerTooManyPages(MaxPagesPerShop));

        if (!await graph.SubscribeAsync(page.Id, page.Token, ct))
            return ServiceResult<MessengerPage>.Fail(FailureKind.Validation, Text.MessengerSubscribeFailed);

        var now = time.GetUtcNow().UtcDateTime;
        var protectedToken = cipher.Protect(page.Token, TokenPurpose(page.Id));
        if (existing is null)
        {
            existing = new MessengerConnection { PageId = page.Id, BusinessId = businessId, ConnectedAtUtc = now };
            db.MessengerConnections.Add(existing);
        }
        existing.PageName = page.Name;
        existing.PageTokenEnc = protectedToken;
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException ex) when (ex.InnerException is Npgsql.PostgresException { SqlState: "23505" })
        {
            return ServiceResult<MessengerPage>.Fail(FailureKind.PolicyViolation, Text.MessengerPageTaken); // another shop connected it at the same moment
        }
        return ServiceResult<MessengerPage>.Ok(new MessengerPage(existing.PageId, existing.PageName, existing.ConnectedAtUtc));
    }

    public async Task<IReadOnlyList<MessengerPage>> ListAsync(Guid businessId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var rows = await db.MessengerConnections.AsNoTracking().Where(c => c.BusinessId == businessId).OrderBy(c => c.ConnectedAtUtc).ToListAsync(ct);
        return rows.Select(c => new MessengerPage(c.PageId, c.PageName, DateTime.SpecifyKind(c.ConnectedAtUtc, DateTimeKind.Utc))).ToList();
    }

    /// <summary>Stops the Page's messages and forgets it. A Page of another shop is "not found".</summary>
    public async Task<bool> DisconnectAsync(Guid businessId, string pageId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var row = await db.MessengerConnections.FirstOrDefaultAsync(c => c.PageId == pageId && c.BusinessId == businessId, ct);
        if (row is null) return false;
        if (cipher.Unprotect(row.PageTokenEnc, TokenPurpose(row.PageId)) is { } token) await graph.UnsubscribeAsync(row.PageId, token, ct);
        db.MessengerConnections.Remove(row);
        await db.SaveChangesAsync(ct);
        return true;
    }

    /// <summary>Which shop a Page belongs to and the token to answer with (for the webhook), or null when it is not connected or its token can not be read.</summary>
    public async Task<(Guid BusinessId, string Slug, string Token)?> FindAsync(string pageId, CancellationToken ct = default)
    {
        if (!cipher.IsAvailable) return null;
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var row = await db.MessengerConnections.AsNoTracking().Where(c => c.PageId == pageId)
            .Join(db.Businesses.AsNoTracking(), c => c.BusinessId, b => b.Id, (c, b) => new { c.PageId, c.BusinessId, c.PageTokenEnc, b.Slug })
            .FirstOrDefaultAsync(ct);
        if (row is null) return null;
        var token = cipher.Unprotect(row.PageTokenEnc, TokenPurpose(row.PageId));
        if (token is null) { log.LogWarning("A saved Messenger token could not be read (was Messenger:TokenKey changed?)"); return null; }
        return (row.BusinessId, row.Slug, token);
    }

    internal static string TokenPurpose(string pageId) => "messenger-page-token:" + pageId;
}

public enum MessengerOutcome { Disabled, BadSignature, Handled }

/// <summary>
/// Answers messages that arrive at a connected Facebook Page with the AI receptionist (the same one as on the booking page, with the same rules and limits).
/// Only a text message from a person is answered; an echo of the Page's own message, a picture and anything else is ignored. Meta delivers a message again
/// when it is not answered in time, so the id of the last message of each person is kept and the same one is never answered twice. The Page named in the
/// message decides the shop: a Page a shop connected, or else the one Page set in the settings.
/// </summary>
public sealed class MessengerService(
    MessengerOptions options, IDbContextFactory<AppDbContext> dbFactory, TimeProvider time, AiReceptionistService assistant, IMessengerSender sender,
    MessengerConnectService connections, ILogger<MessengerService> log)
{
    public const int MaxBodyBytes = 64 * 1024;
    public const int MaxReplyChars = 1900; // Messenger takes up to 2000
    private const int KeepDays = 30;

    public async Task<MessengerOutcome> HandleAsync(byte[] body, string? signature, string baseUrl, CancellationToken ct)
    {
        if (!options.IsEnabled) return MessengerOutcome.Disabled;
        if (!MessengerSignature.IsValid(options.AppSecret, body, signature)) return MessengerOutcome.BadSignature;

        foreach (var (pageId, psid, mid, text) in ReadTexts(body))
        {
            try { await ReplyAsync(pageId, psid, mid, text, baseUrl, ct); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                log.LogWarning("A Messenger message could not be answered: {Reason}", ex.GetType().Name);
            }
        }
        return MessengerOutcome.Handled;
    }

    private static List<(string PageId, string Psid, string Mid, string Text)> ReadTexts(byte[] body)
    {
        var found = new List<(string, string, string, string)>();
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (!doc.RootElement.TryGetProperty("object", out var obj) || obj.GetString() != "page") return found;
            if (!doc.RootElement.TryGetProperty("entry", out var entries) || entries.ValueKind != JsonValueKind.Array) return found;
            foreach (var entry in entries.EnumerateArray())
            {
                var pageId = entry.TryGetProperty("id", out var pid) ? pid.GetString() : null;
                if (string.IsNullOrWhiteSpace(pageId) || pageId.Length > 100) continue;
                if (!entry.TryGetProperty("messaging", out var events) || events.ValueKind != JsonValueKind.Array) continue;
                foreach (var e in events.EnumerateArray())
                {
                    if (!e.TryGetProperty("message", out var message) || message.ValueKind != JsonValueKind.Object) continue;
                    if (message.TryGetProperty("is_echo", out var echo) && echo.ValueKind == JsonValueKind.True) continue;
                    var psid = e.TryGetProperty("sender", out var s) && s.TryGetProperty("id", out var id) ? id.GetString() : null;
                    var mid = message.TryGetProperty("mid", out var m) ? m.GetString() : null;
                    var text = message.TryGetProperty("text", out var t) ? t.GetString() : null;
                    if (string.IsNullOrWhiteSpace(psid) || psid.Length > 100 || string.IsNullOrWhiteSpace(mid) || mid.Length > 200 || string.IsNullOrWhiteSpace(text)) continue;
                    found.Add((pageId, psid, mid, text));
                }
            }
        }
        catch (JsonException) { /* not a message we understand: nothing to answer */ }
        return found;
    }

    private async Task ReplyAsync(string pageId, string psid, string mid, string text, string baseUrl, CancellationToken ct)
    {
        // The shop and the token: a Page a shop connected first, otherwise the one Page from the settings.
        Guid shopId;
        string slug, pageToken;
        if (await connections.FindAsync(pageId, ct) is { } connected)
            (shopId, slug, pageToken) = connected;
        else if (options.OnePageEnabled)
        {
            await using var lookup = await dbFactory.CreateDbContextAsync(ct);
            var found = await lookup.Businesses.AsNoTracking().Where(b => b.Slug == options.ShopSlug).Select(b => (Guid?)b.Id).FirstOrDefaultAsync(ct);
            if (found is null) return; // the shop in the settings does not exist
            (shopId, slug, pageToken) = (found.Value, options.ShopSlug, options.PageAccessToken);
        }
        else return; // a Page nobody connected

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var now = time.GetUtcNow().UtcDateTime;
        var thread = await db.MessengerThreads.FirstOrDefaultAsync(t => t.BusinessId == shopId && t.Psid == psid, ct);
        if (thread?.LastMid == mid) return; // Meta delivered it again: already answered
        if (thread is null)
        {
            // People of this shop who have not written for a month are forgotten (their conversations are removed the same way).
            await db.MessengerThreads.Where(t => t.BusinessId == shopId && t.UpdatedAtUtc < now.AddDays(-KeepDays)).ExecuteDeleteAsync(ct);
            thread = new MessengerThread { BusinessId = shopId, Psid = psid };
            db.MessengerThreads.Add(thread);
        }

        var result = await assistant.SendAsync(slug, thread.ChatId, text, baseUrl, ct);
        if (!result.IsSuccess && thread.ChatId is not null && result.Failure is FailureKind.NotFound or FailureKind.PolicyViolation && result.Message != Text.AiChatUnavailable)
            result = await assistant.SendAsync(slug, null, text, baseUrl, ct); // the old conversation is gone or full: start a new one

        thread.LastMid = mid;
        thread.UpdatedAtUtc = now;
        string? answer = null;
        if (result.IsSuccess) { thread.ChatId = result.Value!.ChatId; answer = result.Value.Text; }
        else if (result.Failure is FailureKind.Validation or FailureKind.PolicyViolation) answer = result.Message; // a short, friendly reason from the assistant
        // NotFound (the shop has no AI add-on, or is closed): say nothing, the booking page is still there.
        await db.SaveChangesAsync(ct);

        if (!string.IsNullOrWhiteSpace(answer))
            await sender.SendTextAsync(pageToken, psid, answer.Length > MaxReplyChars ? answer[..MaxReplyChars] : answer, ct);
    }
}
