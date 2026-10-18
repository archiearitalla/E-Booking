using BarberBook.Web.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace BarberBook.Web.Pages.Admin;

/// <summary>
/// Base for every back-office page. The business id comes from the signed-in user's claims and nowhere else;
/// a request can never name the tenant. A principal without that claim is refused.
/// </summary>
public abstract class AdminPageModel : FormPageModel
{
    protected Guid BusinessId => User.BusinessId()!.Value;

    protected BookingActor StaffActor => BookingActor.Staff(User.UserId() ?? Guid.Empty, User.DisplayName() ?? "Staff");

    /// <summary>One-shot messages that survive a redirect (post/redirect/get).</summary>
    [TempData] public string? Success { get; set; }
    [TempData] public string? Failure { get; set; }

    public override void OnPageHandlerExecuting(PageHandlerExecutingContext context)
    {
        if (User.BusinessId() is null) context.Result = Forbid();
    }
}
