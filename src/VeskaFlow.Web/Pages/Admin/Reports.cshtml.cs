using System.Globalization;
using VeskaFlow.Web.Services;
using Microsoft.AspNetCore.Mvc;

namespace VeskaFlow.Web.Pages.Admin;

/// <summary>Bookings by outcome and revenue for a chosen period or exact dates, by service and by staff. Managers and owner.</summary>
[AdminAccess(AdminLevel.Manager)]
public class ReportsModel(ReportService reports) : AdminPageModel
{
    public Report? Report { get; private set; }
    public string PeriodKey { get; private set; } = "month";
    public string? FromText { get; private set; }
    public string? ToText { get; private set; }

    public IReadOnlyList<(string Value, string Label)> Periods { get; } =
    [
        ("month", Text.ReportsThisMonth), ("last", Text.ReportsLastMonth), ("30", Text.ReportsLast30), ("year", Text.ReportsThisYear),
    ];

    public async Task<IActionResult> OnGetAsync(string? p, string? from, string? to, CancellationToken ct)
    {
        // Exact dates win over the period when both are given.
        if (TryDate(from, out var fromDate) && TryDate(to, out var toDate))
        {
            Report = await reports.BuildRangeAsync(BusinessId, fromDate, toDate, ct);
            if (Report is null) return NotFound();
            FromText = Report.From.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            ToText = Report.To.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            PeriodKey = "";
            return Page();
        }

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

    private static bool TryDate(string? value, out DateOnly date) =>
        DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out date);
}
