using VeskaFlow.Web.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.RateLimiting;

namespace VeskaFlow.Web.Pages.Book;

/// <summary>
/// One message of the AI receptionist chat on the booking page, answered as JSON. The antiforgery token of the page's form is
/// required, messages are limited per client IP, and a shop without the add-on gets the same not-found as an unknown shop.
/// </summary>
[EnableRateLimiting(RateLimitPolicies.Chat)]
[RequestSizeLimit(4096)]
public class ChatModel(AiReceptionistService assistant, IConfiguration config, ICaptcha captcha) : PageModel
{
    public IActionResult OnGet() => NotFound();

    public async Task<IActionResult> OnPostAsync(string slug, [FromForm] Guid? chatId, [FromForm] string? message, CancellationToken ct)
    {
        // A new conversation needs the "are you a person" check (when Turnstile is on); the rest of that conversation does not, since
        // only the server hands out a chat id and a conversation is limited to 20 messages. A check is single-use, so the page asks
        // for a fresh one after a failure.
        if (chatId is null
            && !await captcha.VerifyAsync(Request.Form["cf-turnstile-response"], HttpContext.Connection.RemoteIpAddress?.ToString(), ct))
        {
            Response.Headers.CacheControl = "no-store";
            return new JsonResult(new { error = Text.CaptchaFailed }) { StatusCode = StatusCodes.Status400BadRequest };
        }

        var baseUrl = config["App:PublicBaseUrl"] is { Length: > 0 } configured ? configured.Trim().TrimEnd('/') : $"{Request.Scheme}://{Request.Host}";
        var result = await assistant.SendAsync(slug, chatId, message, baseUrl, ct);
        Response.Headers.CacheControl = "no-store";
        if (result.IsSuccess) return new JsonResult(new { chatId = result.Value!.ChatId, reply = result.Value.Text });
        return result.Failure switch
        {
            FailureKind.NotFound => NotFound(),
            FailureKind.Validation => new JsonResult(new { error = result.Message }) { StatusCode = StatusCodes.Status400BadRequest },
            _ => new JsonResult(new { error = result.Message }) { StatusCode = StatusCodes.Status409Conflict },
        };
    }
}
