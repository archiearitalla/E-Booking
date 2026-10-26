using VeskaFlow.Web.Services;
using Microsoft.AspNetCore.Mvc;

namespace VeskaFlow.Web.Pages.Admin;

/// <summary>Bookings by outcome and revenue for a chosen period, by service and by staff. Staff only.</summary>
public class ReportsModel(ReportService reports) : AdminPageModel
{
    public Report? Report { get; private set; }
    public string PeriodKey { get; private set; } = "month";

    public IReadOnlyList<(string Value, string Label)> Periods { get; } =
    [
        ("month", Text.ReportsThisMonth), ("last", Text.ReportsLastMonth), ("30", Text.ReportsLast30), ("year", Text.ReportsThisYear),
    ];

    public async Task<IActionResult> OnGetAsync(string? p, CancellationToken ct)
    {
        var (key, period) = p switch
        {
            "last" => ("last", ReportService.Period.LastMonth),
            "30" => ("30", ReportService.Period.Last30Days),
            "year" => ("year", ReportService.Period.ThisYear),
            _ => ("month", ReportService.Period.ThisMonth),
        };
        PeriodKey = key;
        Report = await reports.BuildAsync(BusinessId, period, ct);
        return Report is null ? NotFound() : Page();
    }
}
