using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace VeskaFlow.Web.Services;

/// <summary>A "are you a person" check on sign-up and forgot password. Off until both Turnstile keys are configured.</summary>
public interface ICaptcha
{
    bool IsEnabled { get; }
    string SiteKey { get; }
    Task<bool> VerifyAsync(string? token, string? clientIp, CancellationToken ct = default);
}

/// <summary>
/// Cloudflare Turnstile (free). <c>Turnstile:SiteKey</c> goes to the browser, <c>Turnstile:SecretKey</c> stays on the server.
/// With both set the check is required; a failed or unreachable check refuses the request (it never lets a bot through).
/// </summary>
public sealed class TurnstileVerifier(HttpClient http, IConfiguration config, ILogger<TurnstileVerifier> log) : ICaptcha
{
    private const string VerifyUrl = "https://challenges.cloudflare.com/turnstile/v0/siteverify";

    public string SiteKey => config["Turnstile:SiteKey"]?.Trim() ?? "";
    private string SecretKey => config["Turnstile:SecretKey"]?.Trim() ?? "";
    public bool IsEnabled => SiteKey.Length > 0 && SecretKey.Length > 0;

    public async Task<bool> VerifyAsync(string? token, string? clientIp, CancellationToken ct = default)
    {
        if (!IsEnabled) return true;
        if (string.IsNullOrWhiteSpace(token) || token.Length > 2048) return false;
        try
        {
            var form = new Dictionary<string, string> { ["secret"] = SecretKey, ["response"] = token };
            if (!string.IsNullOrEmpty(clientIp)) form["remoteip"] = clientIp;
            using var response = await http.PostAsync(VerifyUrl, new FormUrlEncodedContent(form), ct);
            if (!response.IsSuccessStatusCode) return false;
            var body = await response.Content.ReadFromJsonAsync<SiteVerify>(cancellationToken: ct);
            return body?.Success == true;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
        {
            log.LogWarning("The Turnstile check could not be completed: {Reason}", ex.GetType().Name);
            return false;
        }
    }

    private sealed record SiteVerify([property: JsonPropertyName("success")] bool Success);
}

/// <summary>Used when nobody set up Turnstile: everything passes.</summary>
public sealed class NoCaptcha : ICaptcha
{
    public bool IsEnabled => false;
    public string SiteKey => "";
    public Task<bool> VerifyAsync(string? token, string? clientIp, CancellationToken ct = default) => Task.FromResult(true);
}
