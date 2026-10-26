using VeskaFlow.Web.Services;
using Microsoft.AspNetCore.Mvc;

namespace VeskaFlow.Web.Pages.Admin;

/// <summary>The people who can sign in: the owner adds managers and staff, switches them off, and sets a new password. Owner only.</summary>
[AdminAccess(AdminLevel.Owner)]
public class TeamModel(TeamService team) : AdminPageModel
{
    public IReadOnlyList<TeamMember> Members { get; private set; } = [];
    [BindProperty] public NewMemberForm Input { get; set; } = new();

    public async Task<IActionResult> OnGetAsync(CancellationToken ct)
    {
        Members = await team.ListAsync(BusinessId, ct);
        return Page();
    }

    public async Task<IActionResult> OnPostAddAsync(CancellationToken ct)
    {
        var result = await team.AddAsync(BusinessId, Input.Name, Input.Email, Input.Role, Input.Password, ct);
        if (result.IsSuccess)
        {
            Success = string.Format(Text.TeamAdded, result.Value!.DisplayName);
            return RedirectToPage();
        }
        if (result.Failure == FailureKind.Validation) Errors = result.FieldErrors;
        else Failure = result.Message;
        Input.Password = null; // never send a password back to the page
        Members = await team.ListAsync(BusinessId, ct);
        return Page();
    }

    public async Task<IActionResult> OnPostActiveAsync(Guid id, bool on, CancellationToken ct)
    {
        if (id == User.UserId()) { Failure = Text.TeamOwnerLocked; return RedirectToPage(); }
        var result = await team.SetActiveAsync(BusinessId, id, on, ct);
        if (result.Failure == FailureKind.NotFound) return NotFound();
        if (result.IsSuccess) Success = on ? Text.TeamSwitchedOn : Text.TeamSwitchedOff;
        else Failure = result.Message;
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostPasswordAsync(Guid id, string? password, CancellationToken ct)
    {
        var result = await team.SetPasswordAsync(BusinessId, id, password, ct);
        if (result.Failure == FailureKind.NotFound) return NotFound();
        if (result.IsSuccess) Success = Text.TeamPasswordSet;
        else Failure = result.FieldErrors.GetValueOrDefault("password") ?? result.Message;
        return RedirectToPage();
    }

    public class NewMemberForm
    {
        public string? Name { get; set; }
        public string? Email { get; set; }
        public string? Role { get; set; } = AdminRoles.Staff;
        public string? Password { get; set; }
    }
}
