using VeskaFlow.Core.Scheduling;
using VeskaFlow.Web.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace VeskaFlow.Web.Pages.Book;

[EnableRateLimiting(RateLimitPolicies.Booking)]
public class DetailsModel(ShopCatalogService catalog, BookingService booking) : FormPageModel
{
    [BindProperty] public DetailsInput Input { get; set; } = new();

    public PublicShop? Shop { get; private set; }
    public PublicService? Service { get; private set; }
    public PublicStaff? Staff { get; private set; }
    public DateTime StartUtc { get; private set; }

    public string ChangeTimeUrl => Url.Page("/Book/Index", new
    {
        slug = Shop!.Slug, serviceId = Service!.Id, staffId = Staff?.Id,
        date = DisplayFormat.DateParam(LocalTime.LocalDateOf(StartUtc, Shop.TimeZone)),
    })!;

    public async Task<IActionResult> OnGetAsync(string slug, Guid? serviceId, Guid? staffId, string? start, CancellationToken ct)
    {
        if (!await LoadAsync(slug, serviceId, staffId, start, ct)) return NotFound();
        if (!Shop!.AcceptingBookings) return RedirectToPage("/Book/Index", new { slug = Shop.Slug });

        // Show the form only for a time that is still free; otherwise go back to the time list.
        var localDate = LocalTime.LocalDateOf(StartUtc, Shop!.TimeZone);
        var free = await booking.GetFreeStartsAsync(Shop.Id, Service!.Id, Staff?.Id, localDate, walkIn: false, ct);
        if (!free.IsSuccess || !free.Value!.Contains(StartUtc)) return BackToTimes(localDate);

        Input = new DetailsInput { IdempotencyKey = Guid.NewGuid().ToString("N") }; // one key per form, reused on resubmit
        return Page();
    }

    public async Task<IActionResult> OnPostAsync(string slug, CancellationToken ct)
    {
        if (!await LoadAsync(slug, Input.ServiceId, Input.StaffId, Input.Start, ct)) return NotFound();

        // The business comes from the slug, never from the form. Price, duration and staff are derived by the server.
        var result = await booking.CreateAsync(new CreateBookingRequest(
            Shop!.Id, Service!.Id, Staff?.Id, StartUtc,
            Input.Name ?? "", Input.Phone ?? "", Input.Email, Input.IdempotencyKey ?? "",
            BookingActor.Customer((Input.Name ?? "Customer").Trim())), ct);

        if (result.IsSuccess)
            return RedirectToPage("/Booking/Manage", new { token = result.Value!.Booking.ManageToken, confirmed = 1 });

        switch (result.Failure)
        {
            case FailureKind.SlotTaken:
                return BackToTimes(LocalTime.LocalDateOf(StartUtc, Shop.TimeZone));
            case FailureKind.Validation:
                Errors = result.FieldErrors;
                return Page();
            case FailureKind.PolicyViolation:
                return RedirectToPage("/Book/Index", new { slug = Shop.Slug });
            default:
                return NotFound();
        }
    }

    private IActionResult BackToTimes(DateOnly date) =>
        RedirectToPage("/Book/Index", new
        {
            slug = Shop!.Slug, serviceId = Service!.Id, staffId = Staff?.Id,
            date = DisplayFormat.DateParam(date), taken = true,
        });

    private async Task<bool> LoadAsync(string slug, Guid? serviceId, Guid? staffId, string? start, CancellationToken ct)
    {
        Shop = await catalog.GetBySlugAsync(slug, ct);
        if (Shop is null) return false;

        Service = Shop.FindService(serviceId);
        if (Service is null) return false;

        if (staffId is not null)
        {
            Staff = Shop.FindStaff(staffId);
            if (Staff is null) return false;
        }

        if (!DisplayFormat.TryParseStart(start, out var parsed)) return false;
        StartUtc = parsed;
        return true;
    }

    public class DetailsInput
    {
        public Guid? ServiceId { get; set; }
        public Guid? StaffId { get; set; }
        public string? Start { get; set; }
        public string? Name { get; set; }
        public string? Phone { get; set; }
        public string? Email { get; set; }
        public string? IdempotencyKey { get; set; }
    }
}
