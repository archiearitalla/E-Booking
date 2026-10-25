using System.Net.Http.Headers;
using System.Net.Http.Json;

namespace VeskaFlow.Web.Services;

/// <summary>Sends one plain-text email. Returns false when it could not be sent (never throws for a delivery problem).</summary>
public interface IEmailSender
{
    /// <summary>False when no email service is set up: the app then shows the link to the administrator instead of emailing it.</summary>
    bool IsConfigured { get; }

    Task<bool> SendAsync(string to, string subject, string body, CancellationToken ct = default);
}

/// <summary>Used when no email service is configured.</summary>
public sealed class NoEmailSender : IEmailSender
{
    public bool IsConfigured => false;
    public Task<bool> SendAsync(string to, string subject, string body, CancellationToken ct = default) => Task.FromResult(false);
}

/// <summary>
/// Sends through the Resend HTTP API (free tier, no SMTP: free hosting often blocks SMTP ports). Configuration:
/// <c>Email:ResendApiKey</c> and <c>Email:From</c> (an address on a domain verified in Resend). The key is never logged.
/// </summary>
public sealed class ResendEmailSender(HttpClient http, IConfiguration config, ILogger<ResendEmailSender> log) : IEmailSender
{
    public bool IsConfigured => !string.IsNullOrWhiteSpace(config["Email:ResendApiKey"]) && !string.IsNullOrWhiteSpace(config["Email:From"]);

    public async Task<bool> SendAsync(string to, string subject, string body, CancellationToken ct = default)
    {
        if (!IsConfigured) return false;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.resend.com/emails")
            {
                Content = JsonContent.Create(new { from = config["Email:From"], to = new[] { to }, subject, text = body }),
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", config["Email:ResendApiKey"]);
            using var response = await http.SendAsync(request, ct);
            if (response.IsSuccessStatusCode) return true;
            log.LogWarning("Email was not accepted by the email service (HTTP {Status}).", (int)response.StatusCode);
            return false;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            log.LogWarning("Email could not be sent: {Reason}", ex.GetType().Name);
            return false;
        }
    }
}
