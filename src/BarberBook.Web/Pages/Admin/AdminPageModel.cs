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

    /// <summary>
    /// A business whose trial has ended is locked out of the back office: every admin page sends it to the
    /// "trial ended" page. Checked against the database on each request. Nothing is deleted.
    /// </summary>
    public override async Task OnPageHandlerExecutionAsync(PageHandlerExecutingContext context, PageHandlerExecutionDelegate next)
    {
        if (User.BusinessId() is { } id
            && await context.HttpContext.RequestServices.GetRequiredService<ShopSettingsService>()
                .TrialEndedAsync(id, context.HttpContext.RequestAborted))
        {
            context.Result = RedirectToPage("/Admin/TrialEnded");
            return;
        }
        await base.OnPageHandlerExecutionAsync(context, next);
    }
}
