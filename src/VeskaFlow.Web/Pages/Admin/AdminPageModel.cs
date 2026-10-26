using VeskaFlow.Web.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace VeskaFlow.Web.Pages.Admin;

/// <summary>
/// Base for every back-office page. The business id comes from the signed-in user's claims and nowhere else;
/// a request can never name the tenant. A principal without that claim is refused.
/// </summary>
public abstract class AdminPageModel : FormPageModel
{
    protected Guid BusinessId => User.BusinessId()!.Value;

    /// <summary>What the signed-in person may do, read from the database on every request.</summary>
    public AdminLevel Level { get; private set; } = AdminLevel.Staff;

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
        if (User.BusinessId() is { } id)
        {
            // A shop switched off by the platform owner: end this session now, whatever the cookie still says.
            var accounts = context.HttpContext.RequestServices.GetRequiredService<AccountControlService>();
            if (!await accounts.IsEnabledAsync(id, context.HttpContext.RequestAborted))
            {
                await context.HttpContext.SignOutAsync();
                context.Result = Redirect("/Login?disabled=true");
                return;
            }

            // The person's role and state come from the database, so switching someone off or changing their role works at once.
            var team = context.HttpContext.RequestServices.GetRequiredService<TeamService>();
            var access = User.UserId() is { } userId ? await team.GetAccessAsync(userId, id, context.HttpContext.RequestAborted) : null;
            if (access is null || !access.IsActive)
            {
                await context.HttpContext.SignOutAsync();
                context.Result = Redirect("/Login");
                return;
            }
            Level = AdminRoles.LevelOf(access.Role);
            context.HttpContext.Items[AdminRoles.LevelItemKey] = Level;
            var needed = GetType().GetCustomAttributes(typeof(AdminAccessAttribute), true).OfType<AdminAccessAttribute>().FirstOrDefault()?.Minimum ?? AdminLevel.Staff;
            if (Level < needed)
            {
                context.Result = NotFound(); // the page does not exist for this person
                return;
            }

            var settings = context.HttpContext.RequestServices.GetRequiredService<ShopSettingsService>();
            var ends = await settings.TrialEndsAtAsync(id, context.HttpContext.RequestAborted);
            // The header bell shows what the database says now, not the date frozen in the cookie at sign-in.
            context.HttpContext.Items[UserClaims.TrialEndsItemKey] = ends;
            if (settings.IsEnded(ends))
            {
                context.Result = RedirectToPage("/Admin/TrialEnded");
                return;
            }
        }
        await base.OnPageHandlerExecutionAsync(context, next);
    }
}
