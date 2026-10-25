using System.Globalization;
using VeskaFlow.Web.Services;
using Microsoft.AspNetCore.Mvc;

namespace VeskaFlow.Web.Pages.Admin.Services;

public class EditModel(CatalogAdminService catalog) : AdminPageModel
{
    [BindProperty] public ServiceForm Input { get; set; } = new();

    public bool IsNew { get; private set; }

    public async Task<IActionResult> OnGetAsync(Guid? id, CancellationToken ct)
    {
        IsNew = id is null;
        if (id is null) return Page();

        var row = await catalog.GetServiceAsync(BusinessId, id.Value, ct);
        if (row is null) return NotFound(); // unknown id and another business's id look the same
        Input = new ServiceForm
        {
            Name = row.Name, DurationMinutes = row.DurationMinutes, BufferMinutes = row.BufferMinutes,
            Price = row.Price.ToString("0.00", CultureInfo.InvariantCulture), IsActive = row.IsActive, SortOrder = row.SortOrder,
        };
        return Page();
    }

    public async Task<IActionResult> OnPostAsync(Guid? id, CancellationToken ct)
    {
        IsNew = id is null;
        var errors = new Dictionary<string, string>();
        if (!decimal.TryParse(Input.Price, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var price))
        {
            price = -1; // makes the service report the price error with its own message
        }

        var result = await catalog.SaveServiceAsync(BusinessId,
            new ServiceInput(id, Input.Name, Input.DurationMinutes, Input.BufferMinutes, price, Input.IsActive, Input.SortOrder), ct);

        if (result.Failure == FailureKind.NotFound) return NotFound();
        if (!result.IsSuccess)
        {
            foreach (var (k, v) in result.FieldErrors) errors[k] = v;
            Errors = errors;
            return Page();
        }

        Success = Text.ServiceSaved;
        return Redirect("/Admin/Services");
    }

    public class ServiceForm
    {
        public string? Name { get; set; }
        public int DurationMinutes { get; set; } = 30;
        public int BufferMinutes { get; set; }
        public string? Price { get; set; } = "0.00";
        public bool IsActive { get; set; } = true;
        public int SortOrder { get; set; }
    }
}
