using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using VeskaFlow.Web.Data;

namespace VeskaFlow.Web.Services;

/// <summary>Which web addresses a webhook may be sent to. Only public https addresses: the server must never be made to call its own network (SSRF).</summary>
public static class WebhookGuard
{
    public const int MaxUrlLength = 300;

    /// <summary>The address as a Uri when it is fine to post to, else null and the reason.</summary>
    public static Uri? Parse(string? text, out string? error)
    {
        error = null;
        var t = (text ?? "").Trim();
        if (t.Length == 0 || t.Length > MaxUrlLength || !Uri.TryCreate(t, UriKind.Absolute, out var uri)) { error = Text.WebhookUrlInvalid; return null; }
        if (uri.Scheme != Uri.UriSchemeHttps) { error = Text.WebhookUrlHttps; return null; }
        if (!string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Fragment)) { error = Text.WebhookUrlInvalid; return null; }
        var host = uri.IdnHost.ToLowerInvariant();
        if (host == "localhost" || host.EndsWith(".localhost", StringComparison.Ordinal) || host.EndsWith(".local", StringComparison.Ordinal)
            || host.EndsWith(".internal", StringComparison.Ordinal) || !host.Contains('.') && !IPAddress.TryParse(host, out _))
        { error = Text.WebhookUrlPrivate; return null; }
        if (IPAddress.TryParse(host.Trim('[', ']'), out var ip) && !IsPublicAddress(ip)) { error = Text.WebhookUrlPrivate; return null; }
        return uri;
    }

    /// <summary>True for an address on the public internet. Loopback, private, link-local, shared, multicast, reserved and unspecified addresses are not.</summary>
    public static bool IsPublicAddress(IPAddress ip)
    {
        if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
        if (IPAddress.IsLoopback(ip) || ip.Equals(IPAddress.Any) || ip.Equals(IPAddress.IPv6Any) || ip.Equals(IPAddress.None)) return false;
        var b = ip.GetAddressBytes();
        if (ip.AddressFamily == AddressFamily.InterNetwork)
        {
            return !(b[0] == 0 || b[0] == 10 || b[0] == 127 || (b[0] == 100 && b[1] is >= 64 and <= 127) || (b[0] == 169 && b[1] == 254)
                || (b[0] == 172 && b[1] is >= 16 and <= 31) || (b[0] == 192 && b[1] == 168) || (b[0] == 192 && b[1] == 0 && b[2] == 0)
                || (b[0] == 198 && b[1] is 18 or 19) || b[0] >= 224);
        }
        if (ip.AddressFamily == AddressFamily.InterNetworkV6)
            return !(ip.IsIPv6LinkLocal || ip.IsIPv6SiteLocal || ip.IsIPv6Multicast || (b[0] & 0xFE) == 0xFC); // fe80::/10, fec0::/10, ff00::/8, fc00::/7
        return false;
    }
}

/// <summary>The signature that lets the receiver know a webhook came from VeskaFlow and was not changed: HMAC-SHA256 of "timestamp.body" with the shop's secret.</summary>
public static class WebhookSigner
{
    public static string Sign(string secret, long timestamp, string body)
    {
        var mac = HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes($"{timestamp}.{body}"));
        return "sha256=" + Convert.ToHexString(mac).ToLowerInvariant();
    }

    public static string NewSecret() => "whsec_" + Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).Replace('+', '-').Replace('/', '_').TrimEnd('=');
}

public sealed record WebhookResult(bool Ok, int? StatusCode, string? Error);

/// <summary>Posts one request. Behind an interface so tests never touch the network.</summary>
public interface IWebhookTransport
{
    Task<WebhookResult> PostAsync(Uri url, IReadOnlyDictionary<string, string> headers, string body, CancellationToken ct);
}

/// <summary>
/// The real transport. It connects only to a public address it resolved itself (so a name that points inside the network, or changes after the check, is refused),
/// follows no redirects, and gives up after a few seconds.
/// </summary>
public sealed class HttpWebhookTransport : IWebhookTransport, IDisposable
{
    private readonly HttpClient client;

    public HttpWebhookTransport()
    {
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            ConnectTimeout = TimeSpan.FromSeconds(5),
            ConnectCallback = async (context, ct) =>
            {
                var addresses = (await Dns.GetHostAddressesAsync(context.DnsEndPoint.Host, ct)).Where(WebhookGuard.IsPublicAddress).ToArray();
                if (addresses.Length == 0) throw new HttpRequestException("The address is not a public one.");
                var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
                try
                {
                    await socket.ConnectAsync(addresses, context.DnsEndPoint.Port, ct);
                    return new NetworkStream(socket, ownsSocket: true);
                }
                catch { socket.Dispose(); throw; }
            },
        };
        client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(8) };
    }

    public async Task<WebhookResult> PostAsync(Uri url, IReadOnlyDictionary<string, string> headers, string body, CancellationToken ct)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
            foreach (var (name, value) in headers) request.Headers.TryAddWithoutValidation(name, value);
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            var code = (int)response.StatusCode;
            return code is >= 200 and < 300 ? new WebhookResult(true, code, null) : new WebhookResult(false, code, $"HTTP {code}");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or OperationCanceledException && !ct.IsCancellationRequested)
        {
            return new WebhookResult(false, null, ex is TaskCanceledException ? "Timed out" : ex.GetType().Name); // never the address or the secret
        }
    }

    public void Dispose() => client.Dispose();
}

public sealed record WebhookSettings(bool Enabled, string? Url, string? Secret);

public sealed record WebhookLogRow(Guid Id, string Event, string Status, int Attempts, DateTime CreatedAtUtc, DateTime? DeliveredAtUtc, int? LastStatusCode, string? LastError);

/// <summary>
/// Outgoing webhooks (BUSINESS_RULES 6.y24). Events are written to an outbox and posted by a background worker, so a slow or broken receiver never slows or breaks
/// a booking. A shop's events come from its audit log (one audit row, one event, never replayed from before the shop switched it on); platform events
/// (a payment, a new shop) go to the address in <c>Webhooks:PlatformUrl</c>. Every request is signed. A failed delivery is retried a few times with a longer wait each time.
/// </summary>
public sealed class WebhookService(IDbContextFactory<AppDbContext> dbFactory, TimeProvider time, IWebhookTransport transport, IConfiguration config, ILogger<WebhookService> log)
{
    public static readonly TimeSpan[] Backoff = [TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(30), TimeSpan.FromHours(2), TimeSpan.FromHours(6)];
    public const int MaxAttempts = 6;
    public const string CursorKey = "webhook_audit_cursor";
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull };

    private DateTime Now => time.GetUtcNow().UtcDateTime;

    // ---------------------------------------------------------------- the shop's settings

    public async Task<WebhookSettings?> GetSettingsAsync(Guid businessId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.Businesses.AsNoTracking().Where(b => b.Id == businessId).Select(b => new WebhookSettings(b.WebhookEnabled, b.WebhookUrl, b.WebhookSecret)).FirstOrDefaultAsync(ct);
    }

    /// <summary>Saves the address and the switch. A secret is made the first time. Error key "url".</summary>
    public async Task<ServiceResult<bool>> SaveAsync(Guid businessId, string? url, bool enabled, CancellationToken ct = default)
    {
        var text = (url ?? "").Trim();
        string? saved = null;
        if (text.Length > 0)
        {
            if (WebhookGuard.Parse(text, out var error) is null) return ServiceResult<bool>.Invalid(new Dictionary<string, string> { ["url"] = error! });
            saved = text;
        }
        else if (enabled) return ServiceResult<bool>.Invalid(new Dictionary<string, string> { ["url"] = Text.WebhookUrlNeeded });

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var shop = await db.Businesses.FirstOrDefaultAsync(b => b.Id == businessId, ct);
        if (shop is null) return ServiceResult<bool>.Fail(FailureKind.NotFound, Text.NotFound);
        shop.WebhookUrl = saved;
        shop.WebhookEnabled = enabled && saved is not null;
        shop.WebhookSecret ??= WebhookSigner.NewSecret();
        await db.SaveChangesAsync(ct);
        return ServiceResult<bool>.Ok(true);
    }

    public async Task<ServiceResult<string>> RegenerateSecretAsync(Guid businessId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var secret = WebhookSigner.NewSecret();
        var changed = await db.Businesses.Where(b => b.Id == businessId).ExecuteUpdateAsync(s => s.SetProperty(b => b.WebhookSecret, secret), ct);
        return changed == 0 ? ServiceResult<string>.Fail(FailureKind.NotFound, Text.NotFound) : ServiceResult<string>.Ok(secret);
    }

    public async Task<IReadOnlyList<WebhookLogRow>> RecentAsync(Guid businessId, int take = 20, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.WebhookEvents.AsNoTracking().Where(e => e.BusinessId == businessId).OrderByDescending(e => e.CreatedAtUtc).Take(take)
            .Select(e => new WebhookLogRow(e.Id, e.Event, e.Status, e.Attempts, e.CreatedAtUtc, e.DeliveredAtUtc, e.LastStatusCode, e.LastError)).ToListAsync(ct);
    }

    /// <summary>Sends a test event now, so the owner sees at once whether the receiver answers.</summary>
    public async Task<WebhookResult> SendTestAsync(Guid businessId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var shop = await db.Businesses.AsNoTracking().Where(b => b.Id == businessId).Select(b => new { b.Id, b.Name, b.Slug, b.TimeZone, b.WebhookUrl, b.WebhookSecret }).FirstOrDefaultAsync(ct);
        if (shop?.WebhookUrl is null || shop.WebhookSecret is null) return new WebhookResult(false, null, Text.WebhookUrlNeeded);
        var payload = Envelope(Guid.NewGuid(), "test.ping", Now, new { id = shop.Id, slug = shop.Slug, name = shop.Name, timeZone = shop.TimeZone }, new { message = "This is a test from VeskaFlow." });
        return await DeliverAsync(shop.WebhookUrl, shop.WebhookSecret, Guid.NewGuid(), "test.ping", payload, ct);
    }

    // ---------------------------------------------------------------- building events

    private static string Envelope(Guid id, string eventName, DateTime at, object? shop, object data) =>
        JsonSerializer.Serialize(new { id, @event = eventName, createdAt = at.ToString("O"), shop, data }, Json);

    /// <summary>The shop events made from audit actions. Anything else in the audit log is not an event.</summary>
    public static string? EventFor(string action) => action switch
    {
        "CREATED" => "booking.created",
        "CONFIRMED" or "EMAIL_CONFIRMED" => "booking.confirmed",
        "CANCELLED" => "booking.cancelled",
        "COMPLETED" => "booking.completed",
        "NO_SHOW" => "booking.no_show",
        "RESCHEDULED" => "booking.rescheduled",
        "MANUAL_SALE" => "sale.recorded",
        _ => null,
    };

    /// <summary>
    /// Turns new audit rows of shops that switched webhooks on into events. The first run starts from "now", so switching it on never replays history.
    /// Returns how many events were made.
    /// </summary>
    public async Task<int> PublishNewAsync(CancellationToken ct = default)
    {
        var made = 0;
        for (var round = 0; round < 20; round++)
        {
            var (batchMade, rows) = await PublishBatchAsync(ct);
            made += batchMade;
            if (rows < 300) break; // that was the last batch
        }
        return made;
    }

    private async Task<(int Made, int Rows)> PublishBatchAsync(CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var cursorRow = await db.PlatformSettings.FirstOrDefaultAsync(s => s.Key == CursorKey, ct);
        if (cursorRow is null)
        {
            var max = await db.AuditLogs.MaxAsync(a => (long?)a.Id, ct) ?? 0;
            db.PlatformSettings.Add(new PlatformSetting { Key = CursorKey, Value = max.ToString(), UpdatedAtUtc = Now });
            await db.SaveChangesAsync(ct);
            return (0, 0);
        }
        var cursor = long.Parse(cursorRow.Value);
        var rows = await db.AuditLogs.AsNoTracking().Where(a => a.Id > cursor).OrderBy(a => a.Id).Take(300).ToListAsync(ct);
        if (rows.Count == 0) return (0, 0);

        var ids = rows.Select(r => r.BusinessId).Distinct().ToList();
        var shops = await db.Businesses.AsNoTracking().Where(b => ids.Contains(b.Id) && b.WebhookEnabled && b.WebhookUrl != null)
            .Select(b => new { b.Id, b.Name, b.Slug, b.TimeZone }).ToDictionaryAsync(b => b.Id, ct);

        var made = 0;
        foreach (var row in rows)
        {
            if (EventFor(row.Action) is not { } eventName || row.BookingId is null || !shops.TryGetValue(row.BusinessId, out var shop)) continue;
            var booking = await db.Bookings.AsNoTracking().Where(b => b.Id == row.BookingId && b.BusinessId == row.BusinessId).Select(b => new
            {
                b.Id, Status = b.Status.ToString(), Service = b.ServiceNameSnapshot, Staff = b.Staff.Name, b.StartsAtUtc, b.EndsAtUtc, b.PriceSnapshot, b.RegularPriceSnapshot,
                b.DiscountPercentSnapshot, b.PaidAmount, CustomerName = b.Customer.Name, b.Customer.Phone, b.Customer.Email,
            }).FirstOrDefaultAsync(ct);
            if (booking is null) continue;
            var local = VeskaFlow.Core.Scheduling.LocalTime.ToLocal(booking.StartsAtUtc, shop.TimeZone);
            var data = new
            {
                booking = new
                {
                    id = booking.Id, status = booking.Status.ToUpperInvariant(), service = booking.Service, staff = booking.Staff,
                    startsAtUtc = DateTime.SpecifyKind(booking.StartsAtUtc, DateTimeKind.Utc).ToString("O"), endsAtUtc = DateTime.SpecifyKind(booking.EndsAtUtc, DateTimeKind.Utc).ToString("O"),
                    startsAtLocal = local.ToString("yyyy-MM-dd HH:mm"), price = booking.PriceSnapshot, regularPrice = booking.RegularPriceSnapshot,
                    discountPercent = booking.DiscountPercentSnapshot, paidAmount = booking.PaidAmount, reason = row.Reason,
                },
                customer = new { name = booking.CustomerName, phone = DisplayFormat.IsPlaceholderPhone(booking.Phone) ? null : booking.Phone, email = booking.Email },
                actor = row.ActorLabel,
            };
            var id = Guid.NewGuid();
            db.WebhookEvents.Add(new WebhookEvent
            {
                Id = id, BusinessId = shop.Id, Event = eventName, SourceAuditId = row.Id, CreatedAtUtc = Now, NextAttemptAtUtc = Now,
                Payload = Envelope(id, eventName, row.AtUtc, new { id = shop.Id, slug = shop.Slug, name = shop.Name, timeZone = shop.TimeZone }, data),
            });
            made++;
        }
        cursorRow.Value = rows[^1].Id.ToString();
        cursorRow.UpdatedAtUtc = Now;
        await db.SaveChangesAsync(ct);
        return (made, rows.Count);
    }

    private string? PlatformUrl => config["Webhooks:PlatformUrl"] is { Length: > 0 } u ? u.Trim() : null;
    private string? PlatformSecret => config["Webhooks:PlatformSecret"] is { Length: > 0 } s ? s.Trim() : null;
    public bool PlatformConfigured => PlatformUrl is not null && PlatformSecret is { Length: >= 16 } && WebhookGuard.Parse(PlatformUrl, out _) is not null;

    /// <summary>Queues an event for the platform owner's address (a new shop, a payment). Nothing happens when it is not set up. Never throws.</summary>
    public async Task EnqueuePlatformAsync(string eventName, object data, CancellationToken ct = default)
    {
        if (!PlatformConfigured) return;
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            var id = Guid.NewGuid();
            db.WebhookEvents.Add(new WebhookEvent { Id = id, BusinessId = null, Event = eventName, CreatedAtUtc = Now, NextAttemptAtUtc = Now, Payload = Envelope(id, eventName, Now, null, data) });
            await db.SaveChangesAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.LogWarning("A platform webhook event could not be queued: {Reason}", ex.GetType().Name);
        }
    }

    // ---------------------------------------------------------------- delivery

    private async Task<WebhookResult> DeliverAsync(string url, string secret, Guid deliveryId, string eventName, string body, CancellationToken ct)
    {
        var uri = WebhookGuard.Parse(url, out var error);
        if (uri is null) return new WebhookResult(false, null, error);
        var stamp = time.GetUtcNow().ToUnixTimeSeconds();
        var headers = new Dictionary<string, string>
        {
            ["User-Agent"] = "VeskaFlow-Webhook/1",
            ["X-VeskaFlow-Event"] = eventName,
            ["X-VeskaFlow-Delivery"] = deliveryId.ToString(),
            ["X-VeskaFlow-Timestamp"] = stamp.ToString(),
            ["X-VeskaFlow-Signature"] = WebhookSigner.Sign(secret, stamp, body),
        };
        return await transport.PostAsync(uri, headers, body, ct);
    }

    /// <summary>Posts the events that are due, a few at a time, and plans the next try of a failed one. Returns how many were delivered.</summary>
    public async Task<int> DeliverDueAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var due = await db.WebhookEvents.Where(e => e.Status == "PENDING" && e.NextAttemptAtUtc <= Now).OrderBy(e => e.NextAttemptAtUtc).Take(25).ToListAsync(ct);
        var shopIds = due.Where(e => e.BusinessId != null).Select(e => e.BusinessId!.Value).Distinct().ToList();
        var shops = await db.Businesses.AsNoTracking().Where(b => shopIds.Contains(b.Id)).Select(b => new { b.Id, b.WebhookEnabled, b.WebhookUrl, b.WebhookSecret }).ToDictionaryAsync(b => b.Id, ct);

        var delivered = 0;
        foreach (var e in due)
        {
            string? url, secret;
            if (e.BusinessId is { } sid)
            {
                if (!shops.TryGetValue(sid, out var shop)) { db.WebhookEvents.Remove(e); continue; } // the shop was deleted
                if (!shop.WebhookEnabled || shop.WebhookUrl is null || shop.WebhookSecret is null) { e.Status = "FAILED"; e.LastError = "Webhooks were switched off."; continue; }
                (url, secret) = (shop.WebhookUrl, shop.WebhookSecret);
            }
            else if (PlatformConfigured) (url, secret) = (PlatformUrl, PlatformSecret);
            else { e.Status = "FAILED"; e.LastError = "The platform webhook is not set up."; continue; }

            var result = await DeliverAsync(url!, secret!, e.Id, e.Event, e.Payload, ct);
            e.Attempts++;
            e.LastStatusCode = result.StatusCode;
            e.LastError = result.Ok ? null : result.Error;
            if (result.Ok) { e.Status = "DELIVERED"; e.DeliveredAtUtc = Now; delivered++; }
            else if (e.Attempts >= MaxAttempts) e.Status = "FAILED";
            else e.NextAttemptAtUtc = Now + Backoff[Math.Min(e.Attempts - 1, Backoff.Length - 1)];
        }
        // Old rows go: delivered after a week, failed after a month.
        var week = Now.AddDays(-7); var month = Now.AddDays(-30);
        await db.WebhookEvents.Where(e => (e.Status == "DELIVERED" && e.CreatedAtUtc < week) || (e.Status == "FAILED" && e.CreatedAtUtc < month)).ExecuteDeleteAsync(ct);
        await db.SaveChangesAsync(ct);
        return delivered;
    }
}

/// <summary>Makes events from the audit log and posts them: every 10 seconds.</summary>
public sealed class WebhookWorker(IServiceScopeFactory scopes, IConfiguration config, ILogger<WebhookWorker> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!config.GetValue("Notices:Enabled", true)) return;
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(10));
            do
            {
                try
                {
                    await using var scope = scopes.CreateAsyncScope();
                    var service = scope.ServiceProvider.GetRequiredService<WebhookService>();
                    await service.PublishNewAsync(stoppingToken);
                    await service.DeliverDueAsync(stoppingToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    log.LogWarning("The webhooks could not run: {Reason}", ex.GetType().Name);
                }
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException) { }
    }
}
