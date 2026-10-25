using VeskaFlow.Web.Services;
using Microsoft.AspNetCore.Mvc;

namespace VeskaFlow.Web.Pages.Admin.Staff;

public class EditModel(CatalogAdminService catalog) : AdminPageModel
{
    [BindProperty] public StaffForm Input { get; set; } = new();

    public bool IsNew { get; private set; }
    public bool HoursFormSubmitted { get; private set; }
    public Guid? StaffId { get; private set; }
    public string TimeZone { get; private set; } = "";
    public IReadOnlyList<ServiceRow> AllServices { get; private set; } = [];
    public Dictionary<short, DayFields> HourFields { get; private set; } = [];

    public async Task<IActionResult> OnGetAsync(Guid? id, CancellationToken ct)
    {
        IsNew = id is null;
        StaffId = id;
        if (id is null)
        {
            AllServices = await catalog.ListServicesAsync(BusinessId, ct);
            Input.ServiceIds = AllServices.Where(s => s.IsActive).Select(s => s.Id).ToList(); // new staff: offer every active service
            HourFields = HoursForm.Fill([]);
            return Page();
        }

        var view = await catalog.GetStaffAsync(BusinessId, id.Value, ct);
        if (view is null) return NotFound(); // unknown id and another business's id look the same
        ApplyView(view);
        return Page();
    }

    public async Task<IActionResult> OnPostSaveAsync(Guid? id, CancellationToken ct)
    {
        IsNew = id is null;
        StaffId = id;
        var result = await catalog.SaveStaffAsync(BusinessId,
            new StaffInput(id, Input.Name, Input.IsActive, Input.SortOrder, Input.ServiceIds), ct);

        if (result.Failure == FailureKind.NotFound) return NotFound();
        if (!result.IsSuccess)
        {
            Errors = result.FieldErrors;
            await ReloadAsync(id, ct);
            return Page();
        }

        var outcome = result.Value!;
        Success = outcome.AffectedBookings > 0
            ? string.Format(Text.StaffSavedAffected, outcome.AffectedBookings)
            : Text.StaffSaved;
        return RedirectToPage(new { id = outcome.Id });
    }

    public async Task<IActionResult> OnPostHoursAsync(Guid id, CancellationToken ct)
    {
        StaffId = id;
        HoursFormSubmitted = true;
        var read = HoursForm.Read(Request.Form);

        ServiceResult<SaveOutcome> result;
        if (read.Incomplete)
            result = ServiceResult<SaveOutcome>.Invalid(new Dictionary<string, string> { ["hours"] = Text.HoursIncomplete });
        else if (read.BadBreak)
            result = ServiceResult<SaveOutcome>.Invalid(new Dictionary<string, string> { ["hours"] = Text.HoursBreakInvalid });
        else
            result = await catalog.SaveHoursAsync(BusinessId, id, read.Windows, ct);

        if (result.Failure == FailureKind.NotFound) return NotFound();
        if (!result.IsSuccess)
        {
            Errors = result.FieldErrors;
            await ReloadAsync(id, ct, keepHoursFromForm: true);
            return Page();
        }

        var affected = result.Value!.AffectedBookings;
        Success = affected > 0 ? string.Format(Text.HoursSavedAffected, affected) : Text.HoursSaved;
        return Redirect($"/Admin/Staff/Edit/{id}#hours");
    }

    private async Task ReloadAsync(Guid? id, CancellationToken ct, bool keepHoursFromForm = false)
    {
        if (id is null)
        {
            AllServices = await catalog.ListServicesAsync(BusinessId, ct);
            HourFields = HoursForm.Fill([]);
            return;
        }
        var view = await catalog.GetStaffAsync(BusinessId, id.Value, ct);
        if (view is null) return;
        var submitted = Input;
        ApplyView(view);
        if (!keepHoursFromForm) Input = submitted; // keep what the person typed in the details form
        else HourFields = HoursForm.ReadFields(Request.Form);
    }

    private void ApplyView(StaffEditView view)
    {
        StaffId = view.Id;
        TimeZone = view.TimeZone;
        AllServices = view.AllServices;
        Input = new StaffForm { Name = view.Name, IsActive = view.IsActive, SortOrder = view.SortOrder, ServiceIds = view.ServiceIds.ToList() };
        HourFields = HoursForm.Fill(view.Hours);
    }

    public class StaffForm
    {
        public string? Name { get; set; }
        public int SortOrder { get; set; }
        public bool IsActive { get; set; } = true;
        public List<Guid> ServiceIds { get; set; } = [];
    }
}
