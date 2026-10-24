using BarberBook.Web.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace BarberBook.Web.Pages.Book;

public class IndexModel(ShopCatalogService catalog, BookingService booking) : PageModel
{
    [BindProperty(SupportsGet = true)] public Guid? ServiceId { get; set; }
    [BindProperty(SupportsGet = true)] public Guid? StaffId { get; set; }
    [BindProperty(SupportsGet = true)] public DateOnly? Date { get; set; }
    [BindProperty(SupportsGet = true)] public bool Taken { get; set; }

    public PublicShop? Shop { get; private set; }
    public bool Searched { get; private set; }
    public string? Notice { get; private set; }
    public IReadOnlyList<DateTime> Times { get; private set; } = [];

    /// <summary>A date typed in "Pick another date" beyond the strip wins over the strip.</summary>
    [BindProperty(SupportsGet = true)] public DateOnly? Other { get; set; }

    public DateOnly SelectedDate => Other ?? Date ?? Shop!.Today;
    public string DateText => SelectedDate.ToString("dddd, MMMM d", System.Globalization.CultureInfo.InvariantCulture);

    public async Task<IActionResult> OnGetAsync(string slug, CancellationToken ct)
    {
        Shop = await catalog.GetBySlugAsync(slug, ct);
        if (Shop is null) return NotFound();
        if (!Shop.AcceptingBookings) return Page(); // trial ended: the page says so and offers no times

        var service = Shop.FindService(ServiceId);
        if (service is null) return Page(); // nothing chosen yet (or an id from another shop, which shows nothing)

        Searched = true;
        var result = await booking.GetFreeStartsAsync(Shop.Id, service.Id, StaffId, SelectedDate, walkIn: false, ct);
        if (result.IsSuccess) Times = result.Value!;
        else Notice = Text.BookStaffMismatch;
        return Page();
    }
}
