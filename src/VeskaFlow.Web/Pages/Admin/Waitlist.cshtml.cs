using VeskaFlow.Core.Scheduling;
using VeskaFlow.Web.Services;
using Microsoft.AspNetCore.Mvc;

namespace VeskaFlow.Web.Pages.Admin;

/// <summary>
/// Customers waiting for a time on a full day (BUSINESS_RULES 7.3), with the times that are free now and a ready message. The shop is the signed-in one; a staff
/// login tied to one staff member sees only the entries that name them or "any staff". Nothing is sent by this page: staff message from their own phone.
/// </summary>
public class WaitlistModel(WaitlistService waitlist, IConfiguration config) : AdminPageModel
{
    public WaitlistView? View { get; private set; }
    private string _baseUrl = "";

    public IReadOnlyList<IGrouping<DateOnly, WaitlistRow>> Days => View?.Rows.GroupBy(r => r.Day).ToList() ?? [];

    public async Task<IActionResult> OnGetAsync(CancellationToken ct)
    {
        View = await waitlist.ListAsync(BusinessId, ScopeStaffId, ct);
        if (View is null) return NotFound();
        _baseUrl = config["App:PublicBaseUrl"] is { Length: > 0 } configured ? configured.Trim().TrimEnd('/') : $"{Request.Scheme}://{Request.Host}";
        return Page();
    }

    public async Task<IActionResult> OnPostRemoveAsync(Guid id, CancellationToken ct)
    {
        var result = await waitlist.RemoveAsync(BusinessId, id, ScopeStaffId, ct);
        if (!result.IsSuccess) return NotFound();
        Success = Text.WaitlistRemoved;
        return RedirectToPage();
    }

    public async Task<IActionResult> OnGetContactAsync(Guid id, CancellationToken ct)
    {
        var contact = await waitlist.GetContactAsync(BusinessId, id, ScopeStaffId, ct);
        var shop = await waitlist.ListAsync(BusinessId, ScopeStaffId, ct);
        if (contact is null || shop is null) return NotFound();
        return File(System.Text.Encoding.UTF8.GetBytes(ContactFile.Vcf(contact.Value.Name, contact.Value.Phone, contact.Value.Email, shop.ShopName)),
            "text/vcard; charset=utf-8", ContactFile.FileName(contact.Value.Name));
    }

    public string DayText(DateOnly day) => day.ToString("dddd, MMMM d", System.Globalization.CultureInfo.InvariantCulture);

    public string TimesText(WaitlistRow row) => string.Join(", ", row.FreeNow.Take(8).Select(t => DisplayFormat.Time(t, View!.TimeZone)));

    /// <summary>The ready message for a row that has free times now: first name, the day, the times and the shop's booking link for that service and day.</summary>
    public string MessageFor(WaitlistRow row)
    {
        var first = row.CustomerName.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? row.CustomerName;
        var link = $"{_baseUrl}/b/{View!.Slug}?serviceId={row.ServiceId}{(row.StaffId is { } staff ? $"&staffId={staff}" : "")}&date={DisplayFormat.DateParam(row.Day)}";
        return Text.WaitlistMessage(first, View.ShopName, row.ServiceName, DayText(row.Day), TimesText(row), link);
    }
}
