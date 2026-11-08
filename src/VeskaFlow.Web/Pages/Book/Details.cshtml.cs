using VeskaFlow.Core.Scheduling;
using VeskaFlow.Web.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace VeskaFlow.Web.Pages.Book;

[EnableRateLimiting(RateLimitPolicies.Booking)]
public class DetailsModel(ShopCatalogService catalog, BookingService booking, CustomerMailer mailer, IConfiguration config, ILogger<DetailsModel> log, ICaptcha captcha) : FormPageModel
{
    [BindProperty] public DetailsInput Input { get; set; } = new();

    public PublicShop? Shop { get; private set; }
    public PublicService? Service { get; private set; }
    public PublicStaff? Staff { get; private set; }
    public DateTime StartUtc { get; private set; }

    public string ChangeTimeUrl => Url.Page("/Book/Index", new
    {
        slug = Shop!.Slug, serviceId = Service!.Id, staffId = Staff?.Id, serviceNote = Input.ServiceNote,
        date = DisplayFormat.DateParam(LocalTime.LocalDateOf(StartUtc, Shop.TimeZone)),
    })!;

    /// <summary>The service name as the customer will see it: for "Other" it includes what they typed.</summary>
    public string ServiceTitle => Service!.IsOther ? $"{Service.Name}: {Input.ServiceNote}" : Service.Name;

    public async Task<IActionResult> OnGetAsync(string slug, Guid? serviceId, Guid? staffId, string? start, string? serviceNote, CancellationToken ct)
    {
        if (!await LoadAsync(slug, serviceId, staffId, start, ct)) return NotFound();
        if (!Shop!.AcceptingBookings) return RedirectToPage("/Book/Index", new { slug = Shop.Slug });

        // "Other" always comes with the words the customer typed; without them go back and ask.
        var note = serviceNote?.Trim();
        if (Service!.IsOther && string.IsNullOrEmpty(note))
            return RedirectToPage("/Book/Index", new { slug = Shop.Slug, serviceId = Service.Id, staffId = Staff?.Id });

        // Show the form only for a time that is still free; otherwise go back to the time list.
        var localDate = LocalTime.LocalDateOf(StartUtc, Shop!.TimeZone);
        var free = await booking.GetFreeStartsAsync(Shop.Id, Service!.Id, Staff?.Id, localDate, walkIn: false, ct);
        if (!free.IsSuccess || !free.Value!.Contains(StartUtc)) return BackToTimes(localDate);

        Input = new DetailsInput { IdempotencyKey = Guid.NewGuid().ToString("N"), ServiceNote = Service.IsOther ? note : null }; // one key per form, reused on resubmit
        return Page();
    }

    public async Task<IActionResult> OnPostAsync(string slug, CancellationToken ct)
    {
        if (!await LoadAsync(slug, Input.ServiceId, Input.StaffId, Input.Start, ct)) return NotFound();

        // A bot filled the hidden field: answer as if nothing happened and create nothing.
        if (!string.IsNullOrWhiteSpace(Input.Website)) return RedirectToPage("/Book/Index", new { slug = Shop!.Slug });

        // "Are you a person" (Cloudflare Turnstile) when the keys are set; the form is shown again with a fresh check if it fails.
        if (!await captcha.VerifyAsync(Request.Form["cf-turnstile-response"], HttpContext.Connection.RemoteIpAddress?.ToString(), ct))
        {
            Errors = new Dictionary<string, string> { ["captcha"] = Text.CaptchaFailed };
            return Page();
        }

        // The business comes from the slug, never from the form. Price, duration and staff are derived by the server.
        var result = await booking.CreateAsync(new CreateBookingRequest(
            Shop!.Id, Service!.Id, Staff?.Id, StartUtc,
            Input.Name ?? "", Input.Phone ?? "", Input.Email, Input.IdempotencyKey ?? "",
            BookingActor.Customer((Input.Name ?? "Customer").Trim()), Input.ServiceNote, Input.MarketingOk), ct);

        if (result.IsSuccess && result.Value!.EmailConfirmToken is { } confirmToken)
        {
            // This shop wants the email confirmed: the booking is held until the customer presses the emailed link. If the email cannot
            // be sent the time is given back and nothing stays booked.
            var root = config["App:PublicBaseUrl"] is { Length: > 0 } configuredRoot ? configuredRoot.Trim().TrimEnd('/') : $"{Request.Scheme}://{Request.Host}";
            var sent = MailResult.NotSent;
            using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                timeout.CancelAfter(TimeSpan.FromSeconds(8));
                try { sent = await mailer.SendConfirmLinkAsync(Shop!.Id, result.Value.Booking.Id, root, confirmToken, timeout.Token); }
                catch (Exception ex) when (ex is OperationCanceledException or HttpRequestException)
                {
                    log.LogWarning("The confirmation link email took too long or failed: {Reason}", ex.GetType().Name);
                }
            }
            if (sent != MailResult.Sent)
            {
                await booking.ReleaseHeldAsync(Shop!.Id, result.Value.Booking.Id, Text.ConfirmEmailNotSent, CancellationToken.None);
                Input.IdempotencyKey = Guid.NewGuid().ToString("N"); // the released booking keeps the old key
                Errors = new Dictionary<string, string> { ["email"] = Text.ConfirmEmailNotSent };
                return Page();
            }
            return RedirectToPage("/Booking/Manage", new { token = result.Value.Booking.ManageToken });
        }

        if (result.IsSuccess)
        {
            // A new booking with an email address gets a confirmation email (within the daily limit). It never delays or breaks the booking.
            if (!result.Value!.AlreadyExisted && !string.IsNullOrWhiteSpace(Input.Email))
            {
                var baseUrl = config["App:PublicBaseUrl"] is { Length: > 0 } configured ? configured.Trim().TrimEnd('/') : $"{Request.Scheme}://{Request.Host}";
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(TimeSpan.FromSeconds(4));
                try { await mailer.SendConfirmationAsync(Shop.Id, result.Value.Booking.Id, baseUrl, timeout.Token); }
                catch (Exception ex) when (ex is OperationCanceledException or HttpRequestException)
                {
                    log.LogWarning("The confirmation email took too long or failed: {Reason}", ex.GetType().Name);
                }
            }
            return RedirectToPage("/Booking/Manage", new { token = result.Value!.Booking.ManageToken, confirmed = 1 });
        }

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
            slug = Shop!.Slug, serviceId = Service!.Id, staffId = Staff?.Id, serviceNote = Input.ServiceNote,
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

        /// <summary>The customer ticked "send me promos". Unticked sends nothing, which is "no".</summary>
        public bool MarketingOk { get; set; }

        /// <summary>What the customer needs, for the "Other" service.</summary>
        public string? ServiceNote { get; set; }

        /// <summary>Honeypot: hidden from people, filled in by simple bots. Anything in it means no booking.</summary>
        public string? Website { get; set; }
    }
}
