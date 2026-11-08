using System.Security.Cryptography;
using System.Text;
using VeskaFlow.Web.Services;
using Microsoft.AspNetCore.Mvc;

namespace VeskaFlow.Web.Pages.Admin;

/// <summary>Where Facebook sends the owner back after they allowed (or refused) the connection. It never shows anything: it checks and goes to the Messenger page.</summary>
[AdminAccess(AdminLevel.Owner)]
public class MessengerReturnModel(MessengerConnectService connect, MessengerCipher cipher, TimeProvider time, IConfiguration config) : AdminPageModel
{
    public async Task<IActionResult> OnGetAsync(string? code, string? state, string? error, CancellationToken ct)
    {
        var expected = Request.Cookies[MessengerModel.StateCookie];
        Response.Cookies.Delete(MessengerModel.StateCookie, new CookieOptions { Path = "/Admin" }); // good for one try only
        if (error is not null) { Failure = Text.MessengerFacebookRefused; return RedirectToPage("/Admin/Messenger"); }
        if (string.IsNullOrEmpty(code) || string.IsNullOrEmpty(state) || string.IsNullOrEmpty(expected)
            || !CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(state), Encoding.UTF8.GetBytes(expected)))
        {
            Failure = Text.MessengerStateInvalid;
            return RedirectToPage("/Admin/Messenger");
        }

        var pages = await connect.PagesFromCodeAsync(code, MessengerModel.RedirectUri(Request, config), ct);
        if (!pages.IsSuccess) { Failure = pages.Message; return RedirectToPage("/Admin/Messenger"); }
        MessengerModel.WritePick(Response, cipher, BusinessId, pages.Value!, time);
        return RedirectToPage("/Admin/Messenger");
    }
}
