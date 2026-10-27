using VeskaFlow.Web.Services;
using Microsoft.AspNetCore.Mvc;

namespace VeskaFlow.Web.Pages.Admin;

/// <summary>A customer's completed visits with their optional medical records, and the tools to export or erase their data. Staff only.</summary>
public class HistoryModel(VisitRecordService records, CustomerDataService customerData, AdminDayService days) : AdminPageModel
{
    public CustomerHistory? History { get; private set; }

    public async Task<IActionResult> OnGetAsync(Guid id, CancellationToken ct)
    {
        if (!await days.CustomerInScopeAsync(BusinessId, id, ScopeStaffId, ct)) return NotFound(); // only customers this person has served
        History = await records.GetHistoryAsync(BusinessId, id, ct, ScopeStaffId);
        return History is null ? NotFound() : Page(); // unknown id and another business's customer look the same
    }

    /// <summary>Everything the shop keeps about this customer, as a JSON file.</summary>
    public async Task<IActionResult> OnGetExportAsync(Guid id, CancellationToken ct)
    {
        if (Level < AdminLevel.Owner) return NotFound(); // only the owner exports or erases customer data
        var json = await customerData.ExportJsonAsync(BusinessId, id, ct);
        if (json is null) return NotFound();
        return File(System.Text.Encoding.UTF8.GetBytes(json), "application/json; charset=utf-8", "customer-data.json");
    }

    /// <summary>Erases the customer and all their bookings, records and follow-ups for good. The customer's name must be typed.</summary>
    public async Task<IActionResult> OnPostDeleteAsync(Guid id, string? confirm, CancellationToken ct)
    {
        if (Level < AdminLevel.Owner) return NotFound();
        var result = await customerData.DeleteAsync(BusinessId, id, confirm, ct);
        if (result.Failure == FailureKind.NotFound) return NotFound();
        if (!result.IsSuccess)
        {
            Failure = result.FieldErrors.GetValueOrDefault("confirm") ?? result.Message;
            return RedirectToPage(new { id });
        }
        Success = Text.CustomerDataDeleted;
        return Redirect("/Admin");
    }
}
