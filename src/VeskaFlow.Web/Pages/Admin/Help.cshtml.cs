using VeskaFlow.Web.Services;
using Microsoft.AspNetCore.Mvc;

namespace VeskaFlow.Web.Pages.Admin;

/// <summary>The in-app guide: every feature with simple steps. Shows the topics the signed-in person can actually use.</summary>
public class HelpModel : AdminPageModel
{
    [BindProperty(SupportsGet = true, Name = "q")] public string? Query { get; set; }

    /// <summary>The topic to open (from a "How does this work?" link).</summary>
    [BindProperty(SupportsGet = true, Name = "topic")] public string? Topic { get; set; }

    public IReadOnlyList<HelpTopic> Topics { get; private set; } = [];
    public IReadOnlyList<HelpTopic> AllForLevel { get; private set; } = [];

    public IActionResult OnGet()
    {
        AllForLevel = HelpTopics.For(Level);
        var q = (Query ?? "").Trim();
        Topics = q.Length == 0 ? AllForLevel : AllForLevel.Where(t =>
            t.Title.Contains(q, StringComparison.OrdinalIgnoreCase) || t.Summary.Contains(q, StringComparison.OrdinalIgnoreCase)
            || t.Steps.Any(s => s.Contains(q, StringComparison.OrdinalIgnoreCase)) || (t.Tip?.Contains(q, StringComparison.OrdinalIgnoreCase) ?? false)).ToList();
        return Page();
    }
}
