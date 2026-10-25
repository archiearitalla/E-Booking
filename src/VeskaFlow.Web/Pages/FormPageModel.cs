using Microsoft.AspNetCore.Mvc.RazorPages;

namespace VeskaFlow.Web.Pages;

/// <summary>Carries per-field validation messages produced by a service back to the form.</summary>
public abstract class FormPageModel : PageModel
{
    public IReadOnlyDictionary<string, string> Errors { get; protected set; } = new Dictionary<string, string>();

    public string? ErrorFor(string field) => Errors.TryGetValue(field, out var message) ? message : null;
}
