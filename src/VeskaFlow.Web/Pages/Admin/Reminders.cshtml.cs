using VeskaFlow.Core.Scheduling;
using VeskaFlow.Web.Services;
using Microsoft.AspNetCore.Mvc;

namespace VeskaFlow.Web.Pages.Admin;

/// <summary>Customers due a reminder before their appointment, each with a ready message. Nothing is sent by the system.</summary>
public class RemindersModel(ReminderService reminders, ShopSettingsService settings, IConfiguration config, EmailQuota quota, IEmailSender email) : AdminPageModel
{
    public IReadOnlyList<ReminderRow> Rows { get; private set; } = [];
    public string ShopName { get; private set; } = "";

    /// <summary>Set when automatic emails are on for this shop but stopped for today because a daily limit was reached.</summary>
    public EmailRoom EmailRoomNow { get; private set; } = EmailRoom.Ok;
    public string TimeZone { get; private set; } = "";
    private string _baseUrl = "";

    public async Task<IActionResult> OnGetAsync(CancellationToken ct)
    {
        var shop = await settings.GetSettingsAsync(BusinessId, ct);
        if (shop is null) return NotFound();
        ShopName = shop.Name;
        TimeZone = shop.TimeZone;
        _baseUrl = config["App:PublicBaseUrl"] is { Length: > 0 } configured ? configured.Trim().TrimEnd('/') : $"{Request.Scheme}://{Request.Host}";
        Rows = await reminders.ListPendingAsync(BusinessId, ct, ScopeStaffId);
        if (shop.CustomerEmails && email.IsConfigured) EmailRoomNow = await quota.CheckCustomerAsync(BusinessId, ct);
        return Page();
    }

    public async Task<IActionResult> OnPostRemindedAsync(Guid id, CancellationToken ct)
    {
        var result = await reminders.MarkRemindedAsync(BusinessId, id, ct, ScopeStaffId);
        if (!result.IsSuccess) return NotFound();
        Success = Text.ReminderMarked;
        return RedirectToPage();
    }

    /// <summary>When the appointment is, in the shop's time zone: "Tuesday, October 27 at 10:00 AM".</summary>
    public string WhenText(ReminderRow row) =>
        $"{LocalTime.ToLocal(row.StartsAtUtc, TimeZone).ToString("dddd, MMMM d", System.Globalization.CultureInfo.InvariantCulture)} at {DisplayFormat.Time(row.StartsAtUtc, TimeZone)}";

    /// <summary>The ready message: first name only, the appointment, and the customer's own link to see or cancel it.</summary>
    public string MessageFor(ReminderRow row)
    {
        var first = row.CustomerName.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? row.CustomerName;
        return Text.ReminderMessage(first, ShopName, row.ServiceName, WhenText(row), $"{_baseUrl}/booking/{row.ManageToken}");
    }
}
