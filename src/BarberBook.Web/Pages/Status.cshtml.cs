using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace BarberBook.Web.Pages;

/// <summary>
/// One friendly page per HTTP status. Used for unknown slugs, tokens and URLs alike, so a missing shop and a
/// missing booking are indistinguishable (BUSINESS_RULES 8.4, not_found).
/// </summary>
public class StatusModel : PageModel
{
    public string Title { get; private set; } = Text.ErrorTitle;
    public string Body { get; private set; } = Text.ErrorBody;

    public void OnGet(int code)
    {
        (Title, Body) = code switch
        {
            404 => (Text.NotFoundTitle, Text.NotFoundBody),
            429 => (Text.TooManyRequestsTitle, Text.TooManyRequestsBody),
            400 => (Text.BadRequestTitle, Text.BadRequestBody),
            _ => (Text.ErrorTitle, Text.ErrorBody),
        };
    }
}
