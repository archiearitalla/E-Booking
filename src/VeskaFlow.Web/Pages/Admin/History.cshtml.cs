using VeskaFlow.Web.Services;
using Microsoft.AspNetCore.Mvc;

namespace VeskaFlow.Web.Pages.Admin;

/// <summary>A customer's completed visits with their optional medical records. Staff only.</summary>
public class HistoryModel(VisitRecordService records) : AdminPageModel
{
    public CustomerHistory? History { get; private set; }

    public async Task<IActionResult> OnGetAsync(Guid id, CancellationToken ct)
    {
        History = await records.GetHistoryAsync(BusinessId, id, ct);
        return History is null ? NotFound() : Page(); // unknown id and another business's customer look the same
    }
}
