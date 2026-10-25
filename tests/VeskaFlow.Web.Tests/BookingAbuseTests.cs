using System.Net;
using System.Text.RegularExpressions;
using VeskaFlow.Web.Data;
using VeskaFlow.Web.Services;
using Microsoft.EntityFrameworkCore;
using static VeskaFlow.Web.Tests.HttpApp;
using static VeskaFlow.Web.Tests.Seed;

namespace VeskaFlow.Web.Tests;

/// <summary>The online booking page is open to everyone: one person cannot double-book or hoard time (BUSINESS_RULES 8.6).</summary>
public class BookingAbuseServiceTests
{
    private static string Id(string prefix) => prefix + Guid.NewGuid().ToString("N")[..10];

    private sealed record Setup(TestDbFactory Db, BookingService Bookings, RegisteredOwner Owner, Guid ServiceId, Guid StaffA, Guid StaffB);

    private static async Task<Setup> SetupAsync(int cap = 2)
    {
        var db = new TestDbFactory(await TestDb.ConnectionStringAsync());
        var clock = new TestClock(BaseNow);
        var owner = (await new OnboardingService(db, clock).RegisterAsync(
            new RegisterRequest("Clinic", Id("ab"), "Dr A", $"{Id("o")}@example.com", "correct-horse-1", Industry: "clinic", TrialDays: 30))).Value!;
        await using var ctx = db.CreateDbContext();
        var service = await ctx.Services.Where(s => s.BusinessId == owner.BusinessId && !s.IsOther).Select(s => s.Id).SingleAsync();
        var staffA = await ctx.StaffMembers.Where(s => s.BusinessId == owner.BusinessId).Select(s => s.Id).SingleAsync();
        var second = await new CatalogAdminService(db, clock).SaveStaffAsync(owner.BusinessId, new StaffInput(null, "Dr B", true, 1, [service]));
        Assert.True(second.IsSuccess, second.Message);
        return new Setup(db, new BookingService(db, clock, new BookingLimits(cap)), owner, service, staffA, second.Value!.Id);
    }

    private static CreateBookingRequest Req(Setup s, Guid? staff, DateTime start, string phone, BookingActor? actor = null) =>
        new(s.Owner.BusinessId, s.ServiceId, staff, start, "Maria", phone, null, Id("k"), actor ?? BookingActor.Customer("Maria"));

    [Fact]
    public async Task The_same_person_cannot_hold_two_bookings_at_the_same_time_even_with_different_doctors()
    {
        var s = await SetupAsync();
        Assert.True((await s.Bookings.CreateAsync(Req(s, s.StaffA, Manila(5, 10), "09171230001"))).IsSuccess);

        var second = await s.Bookings.CreateAsync(Req(s, s.StaffB, Manila(5, 10), "09171230001"));

        Assert.Equal(FailureKind.Validation, second.Failure);
        Assert.Contains("phone", second.FieldErrors.Keys);
        Assert.Equal(Text.AlreadyBookedThatTime, second.FieldErrors["phone"]);
    }

    [Fact]
    public async Task The_same_number_written_another_way_is_still_the_same_person()
    {
        var s = await SetupAsync();
        Assert.True((await s.Bookings.CreateAsync(Req(s, s.StaffA, Manila(5, 10), "0917 123 0002"))).IsSuccess);

        var plus63 = await s.Bookings.CreateAsync(Req(s, s.StaffB, Manila(5, 10), "+63 917 123 0002"));
        var bare = await s.Bookings.CreateAsync(Req(s, s.StaffB, Manila(5, 10), "(917) 123-0002"));

        Assert.Equal(FailureKind.Validation, plus63.Failure);
        Assert.Equal(FailureKind.Validation, bare.Failure);
        await using var ctx = s.Db.CreateDbContext();
        Assert.Equal(1, await ctx.Customers.CountAsync(c => c.BusinessId == s.Owner.BusinessId && c.Phone == "09171230002"));
    }

    [Fact]
    public async Task A_person_cannot_hold_more_than_the_cap_of_upcoming_bookings_and_can_book_again_after_cancelling()
    {
        var s = await SetupAsync(cap: 2);
        var first = await s.Bookings.CreateAsync(Req(s, s.StaffA, Manila(5, 10), "09171230003"));
        Assert.True((await s.Bookings.CreateAsync(Req(s, s.StaffA, Manila(5, 12), "09171230003"))).IsSuccess);

        var third = await s.Bookings.CreateAsync(Req(s, s.StaffA, Manila(5, 14), "09171230003"));
        Assert.Equal(FailureKind.Validation, third.Failure);
        Assert.Equal(string.Format(Text.TooManyActiveBookings, 2), third.FieldErrors["phone"]);

        // A different person is not affected.
        Assert.True((await s.Bookings.CreateAsync(Req(s, s.StaffA, Manila(5, 14), "09171230004"))).IsSuccess);

        // Cancelling one frees a place for the first person.
        Assert.True((await s.Bookings.CancelByTokenAsync(first.Value!.Booking.ManageToken)).IsSuccess);
        Assert.True((await s.Bookings.CreateAsync(Req(s, s.StaffB, Manila(5, 15), "09171230003"))).IsSuccess);
    }

    [Fact]
    public async Task Staff_are_exempt_and_a_cap_of_zero_turns_the_limit_off()
    {
        var s = await SetupAsync(cap: 2);
        var staff = BookingActor.Staff(s.Owner.AdminUserId, "Dr A");
        for (var h = 10; h <= 13; h++)
            Assert.True((await s.Bookings.CreateAsync(Req(s, s.StaffA, Manila(5, h), "09171230005", staff))).IsSuccess);

        var open = await SetupAsync(cap: 0);
        for (var h = 10; h <= 13; h++)
            Assert.True((await open.Bookings.CreateAsync(Req(open, open.StaffA, Manila(5, h), "09171230006"))).IsSuccess);
    }

    [Fact]
    public async Task Parallel_requests_from_one_person_cannot_slip_past_the_cap()
    {
        var s = await SetupAsync(cap: 2);

        var results = await Task.WhenAll(Enumerable.Range(0, 6).Select(i =>
            Task.Run(() => s.Bookings.CreateAsync(Req(s, s.StaffA, Manila(5, 10 + i), "09171230007")))));

        Assert.Equal(2, results.Count(r => r.IsSuccess));
        await using var ctx = s.Db.CreateDbContext();
        Assert.Equal(2, await ctx.Bookings.CountAsync(b => b.BusinessId == s.Owner.BusinessId));
    }

    [Fact]
    public async Task A_retry_with_the_same_key_still_returns_the_same_booking_when_the_person_is_at_the_cap()
    {
        var s = await SetupAsync(cap: 1);
        var req = Req(s, s.StaffA, Manila(5, 10), "09171230008");
        var first = await s.Bookings.CreateAsync(req);
        Assert.True(first.IsSuccess);

        var retry = await s.Bookings.CreateAsync(req); // same idempotency key: a double click, not a second booking

        Assert.True(retry.IsSuccess);
        Assert.Equal(first.Value!.Booking.Id, retry.Value!.Booking.Id);
    }
}

[Collection("http")]
public class BookingHoneypotWebTests
{
    [Fact]
    public async Task A_filled_hidden_field_creates_no_booking_while_the_same_form_without_it_does()
    {
        var (factory, client) = await StartAsync();
        using var _f = factory;
        var db = new TestDbFactory(await TestDb.ConnectionStringAsync());
        var slug = Unique("hp");
        var owner = (await new OnboardingService(db).RegisterAsync(
            new RegisterRequest("Honeypot Clinic", slug, "Dr A", $"{Unique("o")}@example.com", "correct-horse-1", Industry: "clinic"))).Value!;
        await using var ctx = db.CreateDbContext();
        var serviceId = await ctx.Services.Where(s => s.BusinessId == owner.BusinessId && !s.IsOther).Select(s => s.Id).SingleAsync();

        var svc = new BookingService(db, TimeProvider.System);
        var today = DateOnly.FromDateTime(DateTime.UtcNow.AddHours(8));
        DateTime? start = null;
        for (var d = 1; d < 14 && start is null; d++)
        {
            var free = await svc.GetFreeStartsAsync(owner.BusinessId, serviceId, null, today.AddDays(d));
            if (free.Value!.Count > 0) start = free.Value[0];
        }
        Assert.NotNull(start);
        var url = $"/b/{slug}/confirm?serviceId={serviceId}&start={Uri.EscapeDataString(DisplayFormat.StartParam(start!.Value))}";

        async Task<HttpResponseMessage> PostAsync(string? trap)
        {
            var page = await client.GetStringAsync(url);
            var form = Regex.Match(page, "<form method=\"post\".*?</form>", RegexOptions.Singleline).Value;
            var fields = new Dictionary<string, string>();
            foreach (Match tag in Regex.Matches(form, "<input[^>]*type=\"hidden\"[^>]*>"))
                fields[Regex.Match(tag.Value, "name=\"([^\"]*)\"").Groups[1].Value] =
                    WebUtility.HtmlDecode(Regex.Match(tag.Value, "value=\"([^\"]*)\"").Groups[1].Value);
            fields["Input.Name"] = "Maria Santos";
            fields["Input.Phone"] = "09175550999";
            if (trap is not null) fields["Input.Website"] = trap;
            return await client.PostAsync(url, new FormUrlEncodedContent(fields));
        }

        var bot = await PostAsync("http://spam.example");
        Assert.Equal(HttpStatusCode.Redirect, bot.StatusCode);
        Assert.StartsWith($"/b/{slug}", bot.Headers.Location!.OriginalString);
        Assert.Equal(0, await ctx.Bookings.CountAsync(b => b.BusinessId == owner.BusinessId));

        var person = await PostAsync(null);
        Assert.Equal(HttpStatusCode.Redirect, person.StatusCode);
        Assert.StartsWith("/booking/", person.Headers.Location!.OriginalString);
        Assert.Equal(1, await ctx.Bookings.CountAsync(b => b.BusinessId == owner.BusinessId));
    }
}
