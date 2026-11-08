using VeskaFlow.Web.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace VeskaFlow.Web.Pages.Book;

/// <summary>
/// "Notify me if a time opens" on a full day (BUSINESS_RULES 7.3). The shop comes from the slug; the service, staff and day are checked against that shop.
/// Only a day with no free time can be waited for. Same protections as the booking form: a rate limit, a honeypot and the "are you a person" check.
/// </summary>
[EnableRateLimiting(RateLimitPolicies.Booking)]
public class WaitlistModel(ShopCatalogService catalog, WaitlistService waitlist, ICaptcha captcha) : FormPageModel
{
    [BindProperty] public WaitlistInput Input { get; set; } = new();

    public PublicShop? Shop { get; private set; }
    public PublicService? Service { get; private set; }
    public PublicStaff? Staff { get; private set; }
    public DateOnly Day { get; private set; }
    public bool Joined { get; private set; }

    public string DayText => Day.ToString("dddd, MMMM d", System.Globalization.CultureInfo.InvariantCulture);

    public string BackUrl => Url.Page("/Book/Index", new { slug = Shop!.Slug, serviceId = Service!.Id, staffId = Staff?.Id, date = DisplayFormat.DateParam(Day) })!;

    public async Task<IActionResult> OnGetAsync(string slug, Guid? serviceId, Guid? staffId, DateOnly? date, bool joined, CancellationToken ct)
    {
        if (!await LoadAsync(slug, serviceId, staffId, date, ct)) return NotFound();
        Joined = joined;
        Input = new WaitlistInput { ServiceId = Service!.Id, StaffId = Staff?.Id, Date = DisplayFormat.DateParam(Day) };
        return Page();
    }

    public async Task<IActionResult> OnPostAsync(string slug, CancellationToken ct)
    {
        DateOnly? date = DateOnly.TryParse(Input.Date, out var parsed) ? parsed : null;
        if (!await LoadAsync(slug, Input.ServiceId, Input.StaffId, date, ct)) return NotFound();

        // A bot filled the hidden field: answer as if nothing happened and keep nothing.
        if (!string.IsNullOrWhiteSpace(Input.Website)) return RedirectToPage(new { slug = Shop!.Slug, serviceId = Service!.Id, staffId = Staff?.Id, date = DisplayFormat.DateParam(Day), joined = true });

        if (!await captcha.VerifyAsync(Request.Form["cf-turnstile-response"], HttpContext.Connection.RemoteIpAddress?.ToString(), ct))
        {
            Errors = new Dictionary<string, string> { ["captcha"] = Text.CaptchaFailed };
            return Page();
        }

        var result = await waitlist.JoinAsync(Shop!, new JoinWaitlistRequest(Service!.Id, Staff?.Id, Day, Input.Name, Input.Phone, Input.Email), ct);
        if (result.IsSuccess)
            return RedirectToPage(new { slug = Shop!.Slug, serviceId = Service.Id, staffId = Staff?.Id, date = DisplayFormat.DateParam(Day), joined = true });

        switch (result.Failure)
        {
            case FailureKind.Validation:
                Errors = result.FieldErrors;
                return Page();
            case FailureKind.PolicyViolation:
                Errors = new Dictionary<string, string> { ["policy"] = result.Message ?? Text.NotAllowed };
                return Page();
            default:
                return NotFound();
        }
    }

    private async Task<bool> LoadAsync(string slug, Guid? serviceId, Guid? staffId, DateOnly? date, CancellationToken ct)
    {
        Shop = await catalog.GetBySlugAsync(slug, ct);
        if (Shop is null || !Shop.AcceptingBookings) return false;
        Service = Shop.FindService(serviceId);
        if (Service is null || Service.IsOther) return false; // "Other" needs words about what the customer wants: it books through the form
        if (staffId is not null)
        {
            Staff = Shop.FindStaff(staffId);
            if (Staff is null) return false;
        }
        if (date is null) return false;
        Day = date.Value;
        return true;
    }

    public class WaitlistInput
    {
        public Guid? ServiceId { get; set; }
        public Guid? StaffId { get; set; }
        public string? Date { get; set; }
        public string? Name { get; set; }
        public string? Phone { get; set; }
        public string? Email { get; set; }

        /// <summary>Honeypot: hidden from people, filled in by simple bots. Anything in it means nothing is kept.</summary>
        public string? Website { get; set; }
    }
}
