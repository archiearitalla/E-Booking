using System.Net.Http.Headers;
using System.Net.Http.Json;

namespace VeskaFlow.Web.Services;

/// <summary>Sends one email. Returns false when it could not be sent (never throws for a delivery problem).</summary>
public interface IEmailSender
{
    /// <summary>False when no email service is set up: the app then shows the link to the administrator instead of emailing it.</summary>
    bool IsConfigured { get; }

    /// <summary><c>htmlBody</c> is optional; the plain-text body is always sent.</summary>
    Task<bool> SendAsync(string to, string subject, string textBody, string? htmlBody = null, CancellationToken ct = default);
}

/// <summary>Used when no email service is configured.</summary>
public sealed class NoEmailSender : IEmailSender
{
    public bool IsConfigured => false;
    public Task<bool> SendAsync(string to, string subject, string textBody, string? htmlBody = null, CancellationToken ct = default) => Task.FromResult(false);
}

/// <summary>
/// Sends through the Brevo HTTP API (free tier, about 300 emails a day, no domain setup: a single validated sender address is
/// enough). This is the same service the veskacode.com contact form already uses. Configuration: <c>Email:BrevoApiKey</c>,
/// <c>Email:From</c> (a sender validated in Brevo) and optionally <c>Email:FromName</c>. The key is never logged.
/// </summary>
public sealed class BrevoEmailSender(HttpClient http, IConfiguration config, ILogger<BrevoEmailSender> log) : IEmailSender
{
    public bool IsConfigured => !string.IsNullOrWhiteSpace(config["Email:BrevoApiKey"]) && !string.IsNullOrWhiteSpace(config["Email:From"]);

    public async Task<bool> SendAsync(string to, string subject, string textBody, string? htmlBody = null, CancellationToken ct = default)
    {
        if (!IsConfigured) return false;
        try
        {
            var payload = new Dictionary<string, object?>
            {
                ["sender"] = new { name = config["Email:FromName"] is { Length: > 0 } n ? n : Text.AppName, email = config["Email:From"] },
                ["to"] = new[] { new { email = to } },
                ["subject"] = subject,
                ["textContent"] = textBody,
            };
            if (htmlBody is not null) payload["htmlContent"] = htmlBody;

            using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.brevo.com/v3/smtp/email") { Content = JsonContent.Create(payload) };
            request.Headers.Add("api-key", config["Email:BrevoApiKey"]);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            using var response = await http.SendAsync(request, ct);
            if (response.IsSuccessStatusCode) return true;
            log.LogWarning("Email was not accepted by Brevo (HTTP {Status}).", (int)response.StatusCode);
            return false;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            log.LogWarning("Email could not be sent through Brevo: {Reason}", ex.GetType().Name);
            return false;
        }
    }
}

/// <summary>
/// Sends through the Resend HTTP API (needs a verified domain). Configuration: <c>Email:ResendApiKey</c> and <c>Email:From</c>.
/// Used only when Brevo is not configured.
/// </summary>
public sealed class ResendEmailSender(HttpClient http, IConfiguration config, ILogger<ResendEmailSender> log) : IEmailSender
{
    public bool IsConfigured => !string.IsNullOrWhiteSpace(config["Email:ResendApiKey"]) && !string.IsNullOrWhiteSpace(config["Email:From"]);

    public async Task<bool> SendAsync(string to, string subject, string textBody, string? htmlBody = null, CancellationToken ct = default)
    {
        if (!IsConfigured) return false;
        try
        {
            var payload = new Dictionary<string, object?> { ["from"] = config["Email:From"], ["to"] = new[] { to }, ["subject"] = subject, ["text"] = textBody };
            if (htmlBody is not null) payload["html"] = htmlBody;

            using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.resend.com/emails") { Content = JsonContent.Create(payload) };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", config["Email:ResendApiKey"]);
            using var response = await http.SendAsync(request, ct);
            if (response.IsSuccessStatusCode) return true;
            log.LogWarning("Email was not accepted by Resend (HTTP {Status}).", (int)response.StatusCode);
            return false;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            log.LogWarning("Email could not be sent through Resend: {Reason}", ex.GetType().Name);
            return false;
        }
    }
}
