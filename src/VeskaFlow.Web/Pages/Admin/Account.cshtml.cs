using VeskaFlow.Web.Services;
using Microsoft.AspNetCore.Mvc;

namespace VeskaFlow.Web.Pages.Admin;

/// <summary>Every signed-in person can change their own password here.</summary>
public class AccountModel(AuthService auth) : AdminPageModel
{
    [BindProperty] public PasswordForm Input { get; set; } = new();

    public void OnGet() { }

    public async Task<IActionResult> OnPostAsync(CancellationToken ct)
    {
        if (Input.NewPassword != Input.Confirm)
        {
            Errors = new Dictionary<string, string> { ["confirm"] = Text.ResetMismatch };
            return Page();
        }
        var result = await auth.ChangePasswordAsync(User.UserId() ?? Guid.Empty, Input.Current, Input.NewPassword, ct);
        if (result.IsSuccess)
        {
            Success = Text.AccountPasswordChanged;
            return RedirectToPage();
        }
        if (result.Failure == FailureKind.NotFound) return NotFound();
        Errors = result.FieldErrors;
        return Page();
    }

    public class PasswordForm
    {
        public string? Current { get; set; }
        public string? NewPassword { get; set; }
        public string? Confirm { get; set; }
    }
}
