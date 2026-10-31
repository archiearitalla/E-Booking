using VeskaFlow.Web.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace VeskaFlow.Web.Pages.Platform;

/// <summary>The platform owner's billing: payments waiting to be confirmed, a payment recorded by hand, and the confirmed ledger with this month's total. Not found for anyone else.</summary>
public class PaymentsModel(AccountControlService accounts, PaymentService payments, PricingInfo pricing) : PageModel
{
    public IReadOnlyList<PaymentRow> Pending { get; private set; } = [];
    public IReadOnlyList<PaymentRow> Confirmed { get; private set; } = [];
    public IReadOnlyList<ShopRow> Shops { get; private set; } = [];
    public PaymentTotals Totals { get; private set; } = null!;
    public PricingInfo Pricing => pricing;
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

    private string Who => User.DisplayName() ?? "Platform owner";

    public async Task<IActionResult> OnGetAsync(CancellationToken ct)
    {
        Pending = (await payments.ListAsync("PENDING", 100, ct)).OrderBy(p => p.SubmittedAtUtc).ToList();
        Confirmed = await payments.ListAsync("CONFIRMED", 50, ct);
        Shops = await accounts.ListShopsAsync(ct);
        var now = DateTime.UtcNow;
        Totals = await payments.TotalsAsync(new DateOnly(now.Year, now.Month, 1), ct);
        return Page();
    }

    public async Task<IActionResult> OnPostConfirmAsync(Guid id, CancellationToken ct)
    {
        var result = await payments.ConfirmAsync(id, Who, ct);
        if (result.Failure == FailureKind.NotFound) return NotFound();
        if (result.IsSuccess) Success = Text.PaymentsConfirmed + " " + result.Value; else Failure = result.Message;
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostRejectAsync(Guid id, string? reason, CancellationToken ct)
    {
        var result = await payments.RejectAsync(id, reason, Who, ct);
        if (result.Failure == FailureKind.NotFound) return NotFound();
        if (result.IsSuccess) Success = Text.PaymentsRejected; else Failure = result.FieldErrors.Values.FirstOrDefault() ?? result.Message;
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostByHandAsync(Guid shopId, string? option, string? amount, string? reference, CancellationToken ct)
    {
        decimal? paid = null;
        var text = (amount ?? "").Replace(",", "").Replace("₱", "").Trim();
        if (text.Length > 0)
        {
            if (!decimal.TryParse(text, System.Globalization.NumberStyles.AllowDecimalPoint, System.Globalization.CultureInfo.InvariantCulture, out var parsed))
            {
                Failure = Text.PayAmountInvalid;
                return RedirectToPage();
            }
            paid = parsed;
        }
        var result = await payments.RecordByHandAsync(shopId, option, paid, reference, Who, ct);
        if (result.Failure == FailureKind.NotFound) return NotFound();
        if (result.IsSuccess) Success = Text.PaymentsRecorded + " " + result.Value; else Failure = result.FieldErrors.Values.FirstOrDefault() ?? result.Message;
        return RedirectToPage();
    }
}
