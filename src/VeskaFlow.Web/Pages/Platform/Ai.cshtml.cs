using System.Globalization;
using VeskaFlow.Web.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace VeskaFlow.Web.Pages.Platform;

/// <summary>The platform owner's AI summary: one line per shop with this month's conversations, tokens and estimated cost, and its limits. Not found for anyone else.</summary>
public class AiModel(AccountControlService accounts, AiAdminService admin) : PageModel
{
    [BindProperty(SupportsGet = true, Name = "q")] public string? Query { get; set; }
    [BindProperty(SupportsGet = true, Name = "month")] public string? MonthText { get; set; }

    public AiUsageReport Report { get; private set; } = null!;
    public AiAdminService Admin => admin;
    public DateOnly Month { get; private set; }
    [TempData] public string? Success { get; set; }
    [TempData] public string? Failure { get; set; }

    public override async Task OnPageHandlerExecutionAsync(PageHandlerExecutingContext context, PageHandlerExecutionDelegate next)
    {
        if (!await accounts.IsPlatformAdminAsync(User.UserId(), context.HttpContext.RequestAborted))
        {
            context.Result = NotFound();
            return;
        }
        await next();
    }

    public async Task<IActionResult> OnGetAsync(CancellationToken ct)
    {
        Month = DateOnly.TryParseExact(MonthText + "-01", "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var m) ? m : admin.CurrentMonth;
        Report = await admin.ReportAsync(Month, Query, ct);
        return Page();
    }

    public async Task<IActionResult> OnPostLimitsAsync(Guid id, string? daily, string? monthly, string? tokens, string? q, string? month, CancellationToken ct)
    {
        static bool Parse<T>(string? text, out T? value) where T : struct, IParsable<T>
        {
            value = null;
            var t = (text ?? "").Replace(",", "").Trim();
            if (t.Length == 0) return true;
            if (!T.TryParse(t, CultureInfo.InvariantCulture, out var v)) return false;
            value = v; return true;
        }
        if (!Parse<int>(daily, out var d) | !Parse<int>(monthly, out var mo) | !Parse<long>(tokens, out var tk))
        {
            Failure = Text.AiLimitInvalid;
            return RedirectToPage(new { q, month });
        }
        var result = await admin.SetLimitsAsync(id, d, mo, tk, ct);
        if (result.Failure == FailureKind.NotFound) return NotFound();
        if (result.IsSuccess) Success = Text.AiLimitsSaved; else Failure = result.FieldErrors.Values.FirstOrDefault() ?? result.Message;
        return RedirectToPage(new { q, month });
    }
}
