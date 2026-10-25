using VeskaFlow.Web.Services;
using Microsoft.AspNetCore.Mvc;

namespace VeskaFlow.Web.Pages.Admin;

/// <summary>Customers who should come back (follow-up visits), with a ready-made message for each. Nothing is sent automatically.</summary>
public class FollowUpsModel(FollowUpService followUps, ShopSettingsService settings, IConfiguration config) : AdminPageModel
{
    public IReadOnlyList<FollowUpRow> Rows { get; private set; } = [];
    public string ShopName { get; private set; } = "";
    public string BookingUrl { get; private set; } = "";

    public async Task<IActionResult> OnGetAsync(CancellationToken ct)
    {
        var shop = await settings.GetSettingsAsync(BusinessId, ct);
        if (shop is null) return NotFound();
        ShopName = shop.Name;
        BookingUrl = QrCodes.BookingUrl(config["App:PublicBaseUrl"], Request.Scheme, Request.Host.Value, shop.Slug);
        Rows = await followUps.ListAsync(BusinessId, ct);
        return Page();
    }

    public async Task<IActionResult> OnPostDoneAsync(Guid id, CancellationToken ct)
    {
        var result = await followUps.MarkDoneAsync(BusinessId, id, ct);
        if (!result.IsSuccess) return NotFound();
        Success = Text.FollowUpDoneSaved;
        return RedirectToPage();
    }

    /// <summary>The message staff can copy or send as a text. The first name only, so it reads naturally.</summary>
    public string MessageFor(FollowUpRow row)
    {
        var first = row.CustomerName.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? row.CustomerName;
        return Text.FollowUpMessage(first, ShopName, row.ServiceName, row.FollowUpOn.ToString("MMMM d", System.Globalization.CultureInfo.InvariantCulture), BookingUrl);
    }
}
