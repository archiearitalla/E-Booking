using System.Security.Cryptography;
using System.Text.Json;
using VeskaFlow.Web.Services;
using Microsoft.AspNetCore.Mvc;

namespace VeskaFlow.Web.Pages.Admin;

/// <summary>
/// The owner connects the shop's own Facebook Pages to the AI receptionist (BUSINESS_RULES 6.y23). The shop is always the signed-in one. Facebook's login
/// and permission screen is Facebook's own: we only receive a one-time code, check the <c>state</c> we made, and keep the chosen Page's token encrypted.
/// </summary>
[AdminAccess(AdminLevel.Owner)]
public class MessengerModel(MessengerConnectService connect, MessengerOptions options, MessengerCipher cipher, AiAccess ai, ShopSettingsService settings, TimeProvider time, IConfiguration config) : AdminPageModel
{
    public const string StateCookie = "vf_fb_state";
    public const string PickCookie = "vf_fb_pick";
    private static readonly TimeSpan StateLife = TimeSpan.FromMinutes(10), PickLife = TimeSpan.FromMinutes(15);

    public bool Available => options.ConnectEnabled;
    /// <summary>True until the platform owner sets <c>Messenger:Approved</c>: shops are told the assistant only answers testers for now.</summary>
    public bool ShowReviewNote => !options.Approved;
    public bool AiActive { get; private set; }
    public IReadOnlyList<MessengerPage> Pages { get; private set; } = [];
    /// <summary>Pages Facebook just showed this person, waiting for them to choose one (names only, never tokens).</summary>
    public IReadOnlyList<(string Id, string Name)> Choices { get; private set; } = [];
    public string TimeZoneName { get; private set; } = "Asia/Manila";
    public bool CanAddMore => Pages.Count < MessengerConnectService.MaxPagesPerShop;

    public async Task<IActionResult> OnGetAsync(CancellationToken ct)
    {
        await LoadAsync(ct);
        Choices = ReadPick()?.Select(p => (p.Id, p.Name)).ToList() ?? [];
        return Page();
    }

    /// <summary>
    /// Sends the owner to Facebook. The random <c>state</c> is also kept in a cookie so only the person who started it can finish it. This is a link (GET), not a form:
    /// the site's security policy only lets a form post to its own address, and the browser would stop a form that ends at facebook.com. It changes no record.
    /// </summary>
    public async Task<IActionResult> OnGetStartAsync(CancellationToken ct)
    {
        if (!Available) return NotFound();
        if (!await ai.IsActiveAsync(BusinessId, ct)) { Failure = Text.MessengerNeedsAi; return RedirectToPage(); }
        var state = Convert.ToBase64String(RandomNumberGenerator.GetBytes(24)).Replace('+', '-').Replace('/', '_').TrimEnd('=');
        var cookie = CookieOptionsFor(StateLife);
        cookie.Secure = Request.IsHttps;
        Response.Cookies.Append(StateCookie, state, cookie);
        return Redirect(connect.AuthorizeUrl(RedirectUri(Request, config), state));
    }

    public async Task<IActionResult> OnPostChooseAsync(string? id, CancellationToken ct)
    {
        var page = ReadPick()?.FirstOrDefault(p => p.Id == id);
        Response.Cookies.Delete(PickCookie, new CookieOptions { Path = "/Admin" });
        if (page is null) { Failure = Text.MessengerStateInvalid; return RedirectToPage(); }
        var result = await connect.ConnectAsync(BusinessId, new FacebookPage(page.Id, page.Name, page.Token), ct);
        if (result.IsSuccess) Success = Text.MessengerConnectedDone; else Failure = result.Message;
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostDisconnectAsync(string? id, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(id) || !await connect.DisconnectAsync(BusinessId, id, ct)) return NotFound(); // a Page of another shop is not found
        Success = Text.MessengerDisconnectedDone;
        return RedirectToPage();
    }

    private async Task LoadAsync(CancellationToken ct)
    {
        AiActive = await ai.IsActiveAsync(BusinessId, ct);
        Pages = await connect.ListAsync(BusinessId, ct);
        TimeZoneName = (await settings.GetSettingsAsync(BusinessId, ct))?.TimeZone ?? TimeZoneName;
    }

    // ---- the Pages kept between Facebook's screen and the owner's choice (encrypted, tied to this shop, short-lived)

    public sealed record PickedPage(string Id, string Name, string Token);
    private sealed record PickPayload(long Expires, List<PickedPage> Pages);

    public static string PickPurpose(Guid businessId) => "messenger-pick:" + businessId;

    public static CookieOptions CookieOptionsFor(TimeSpan life) => new() { HttpOnly = true, SameSite = SameSiteMode.Lax, MaxAge = life, Path = "/Admin", IsEssential = true };

    /// <summary>Called when Facebook sends the owner back: remember the Pages they can choose from.</summary>
    public static void WritePick(HttpResponse response, MessengerCipher cipher, Guid businessId, IEnumerable<FacebookPage> pages, TimeProvider time)
    {
        var payload = new PickPayload(time.GetUtcNow().Add(PickLife).ToUnixTimeSeconds(), pages.Select(p => new PickedPage(p.Id, p.Name, p.Token)).ToList());
        var options = CookieOptionsFor(PickLife);
        options.Secure = response.HttpContext.Request.IsHttps;
        response.Cookies.Append(PickCookie, cipher.Protect(JsonSerializer.Serialize(payload), PickPurpose(businessId)), options);
    }

    private List<PickedPage>? ReadPick()
    {
        var plain = cipher.Unprotect(Request.Cookies[PickCookie], PickPurpose(BusinessId));
        if (plain is null) return null;
        try
        {
            var payload = JsonSerializer.Deserialize<PickPayload>(plain);
            return payload is not null && payload.Expires > time.GetUtcNow().ToUnixTimeSeconds() ? payload.Pages : null;
        }
        catch (JsonException) { return null; }
    }

    /// <summary>Where Facebook sends the owner back to. It must be listed in the Meta app's login settings, exactly as written.</summary>
    public static string RedirectUri(HttpRequest request, IConfiguration config)
    {
        var baseUrl = config["App:PublicBaseUrl"] is { Length: > 0 } configured ? configured.Trim().TrimEnd('/') : $"{request.Scheme}://{request.Host}";
        return baseUrl + "/Admin/MessengerReturn";
    }
}
