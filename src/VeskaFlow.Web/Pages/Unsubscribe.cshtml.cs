using VeskaFlow.Web.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace VeskaFlow.Web.Pages;

/// <summary>The link at the end of every promo email: a customer stops promo messages from that shop. The secret in the address says who; nothing else is shown but the shop's name.</summary>
public class UnsubscribeModel(PromoEmailService emails) : PageModel
{
    public string? ShopName { get; private set; }
    public bool Done { get; private set; }

    public async Task<IActionResult> OnGetAsync(string token, CancellationToken ct)
    {
        var found = await emails.LookupAsync(token, ct);
        ShopName = found?.Shop;
        return Page();
    }

    public async Task<IActionResult> OnPostAsync(string token, CancellationToken ct)
    {
        Done = await emails.UnsubscribeAsync(token, ct);
        if (Done) ShopName = (await emails.LookupAsync(token, ct))?.Shop;
        return Page();
    }
}
