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
public class ChatModel(AiReceptionistService assistant, IConfiguration config) : PageModel
{
    public IActionResult OnGet() => NotFound();

    public async Task<IActionResult> OnPostAsync(string slug, [FromForm] Guid? chatId, [FromForm] string? message, CancellationToken ct)
    {
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
