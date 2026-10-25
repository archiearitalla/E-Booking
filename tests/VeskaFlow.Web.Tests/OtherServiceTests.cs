using System.Net;
using VeskaFlow.Web.Services;
using Microsoft.EntityFrameworkCore;
using static VeskaFlow.Web.Tests.Seed;

namespace VeskaFlow.Web.Tests;

/// <summary>"Other": a service for visits whose service is not in the shop's list (online booking and walk-in).</summary>
[Collection("http")]
public class OtherServiceTests
{
    private static string Id(string prefix) => prefix + Guid.NewGuid().ToString("N")[..10];

    private sealed record Setup(TestDbFactory Db, BookingService Bookings, RegisteredOwner Owner, Guid OtherId, Guid StaffId);

    private static async Task<Setup> SetupAsync()
    {
        var db = new TestDbFactory(await TestDb.ConnectionStringAsync());
        var clock = new TestClock(BaseNow);
        var owner = (await new OnboardingService(db, clock).RegisterAsync(
            new RegisterRequest("Clinic", Id("ot"), "Dr A", $"{Id("o")}@example.com", "correct-horse-1", Industry: "clinic"))).Value!;
        await using var ctx = db.CreateDbContext();
        var other = await ctx.Services.Where(s => s.BusinessId == owner.BusinessId && s.IsOther).Select(s => s.Id).SingleAsync();
        var staff = await ctx.StaffMembers.Where(s => s.BusinessId == owner.BusinessId).Select(s => s.Id).SingleAsync();
        return new Setup(db, new BookingService(db, clock), owner, other, staff);
    }

    private static Task<ServiceResult<CreateOutcome>> BookAsync(Setup s, string? note, BookingActor actor, string phone = "09175550091") =>
        s.Bookings.CreateAsync(new CreateBookingRequest(s.Owner.BusinessId, s.OtherId, s.StaffId, Manila(5, 10), "Maria Santos",
            phone, null, Id("k"), actor, note));

    [Fact]
    public async Task Other_needs_a_description_and_the_booking_keeps_it_as_the_service_name()
    {
        var s = await SetupAsync();

        foreach (var missing in new[] { null, "", "   ", new string('x', 61) })
        {
            var refused = await BookAsync(s, missing, BookingActor.Customer("Maria"));
            Assert.Equal(FailureKind.Validation, refused.Failure);
            Assert.Contains("serviceNote", refused.FieldErrors.Keys);
        }

        var booked = await BookAsync(s, "  Teeth cleaning for my child ", BookingActor.Customer("Maria"));
        Assert.True(booked.IsSuccess, booked.Message);
        Assert.Equal("Other: Teeth cleaning for my child", booked.Value!.Booking.ServiceName);
        Assert.Equal(0m, booked.Value.Booking.Price);
    }

    [Fact]
    public async Task A_walk_in_can_book_Other_with_a_description_too()
    {
        var s = await SetupAsync();
        var staff = BookingActor.Staff(s.Owner.AdminUserId, "Dr A");

        Assert.Contains("serviceNote", (await BookAsync(s, null, staff, "")).FieldErrors.Keys);
        var booked = await BookAsync(s, "Wound dressing", staff, "");
        Assert.True(booked.IsSuccess, booked.Message);
        Assert.Equal("Other: Wound dressing", booked.Value!.Booking.ServiceName);
    }

    [Fact]
    public async Task A_note_on_an_ordinary_service_is_ignored()
    {
        var s = await SetupAsync();
        await using var ctx = s.Db.CreateDbContext();
        var haircut = await ctx.Services.Where(x => x.BusinessId == s.Owner.BusinessId && !x.IsOther).Select(x => x.Id).SingleAsync();

        var booked = await s.Bookings.CreateAsync(new CreateBookingRequest(s.Owner.BusinessId, haircut, s.StaffId, Manila(5, 10), "Maria",
            "09175550092", null, Id("k"), BookingActor.Customer("Maria"), "ignored text"));

        Assert.True(booked.IsSuccess, booked.Message);
        Assert.Equal("Consultation", booked.Value!.Booking.ServiceName); // the clinic's starter service, not "Other: ..."
    }

    [Fact]
    public async Task The_booking_page_and_the_walk_in_page_offer_Other_and_ask_what_it_is()
    {
        var (factory, _) = await HttpApp.StartAsync();
        using var _f = factory;
        var client = HttpApp.NewClient(factory);
        var slug = Id("os");
        var email = $"{Id("o")}@example.com";
        await HttpApp.PostFormAsync(client, "/Register", new()
        {
            ["Input.BusinessName"] = "Other Clinic", ["Input.Slug"] = slug, ["Input.OwnerName"] = "Dr A", ["Input.Email"] = email,
            ["Input.Password"] = "correct-horse-1", ["Input.Industry"] = "clinic",
        });

        await using var ctx = new TestDbFactory(await TestDb.ConnectionStringAsync()).CreateDbContext();
        var shopId = await ctx.Businesses.Where(b => b.Slug == slug).Select(b => b.Id).SingleAsync();
        var otherId = await ctx.Services.Where(x => x.BusinessId == shopId && x.IsOther).Select(x => x.Id).SingleAsync();

        // Customer page: Other is in the list (without a price), with a box to say what is needed.
        var page = WebUtility.HtmlDecode(await client.GetStringAsync($"/b/{slug}"));
        Assert.Contains($"<option value=\"{otherId}\"", page);
        Assert.Contains("name=\"serviceNote\"", page);
        Assert.Contains(Text.OtherServiceHint, page);

        // Choosing Other without saying what it is: no times, a clear message.
        var noNote = WebUtility.HtmlDecode(await client.GetStringAsync($"/b/{slug}?serviceId={otherId}"));
        Assert.Contains(Text.OtherServiceRequired, noNote);
        Assert.DoesNotContain("class=\"slot\"", noNote);

        // With the words, the times appear and the link carries them to the confirmation page.
        var day = DateOnly.FromDateTime(DateTime.UtcNow.AddHours(8).AddDays(1)).ToString("yyyy-MM-dd");
        var withNote = WebUtility.HtmlDecode(await client.GetStringAsync($"/b/{slug}?serviceId={otherId}&serviceNote=Wound+dressing&date={day}"));
        Assert.Contains("class=\"slot\"", withNote);
        Assert.Contains("serviceNote=Wound%20dressing", withNote.Replace("+", "%20"));

        // Walk-in page (signed in as the owner): same option and box.
        var walkIn = WebUtility.HtmlDecode(await client.GetStringAsync("/Admin/WalkIn"));
        Assert.Contains($"value=\"{otherId}\"", walkIn);
        Assert.Contains(Text.WalkInOtherLabel, walkIn);
        var walkInNoNote = WebUtility.HtmlDecode(await client.GetStringAsync($"/Admin/WalkIn?serviceId={otherId}"));
        Assert.Contains(Text.OtherServiceRequired, walkInNoNote);

        // The Services page explains the Other row.
        Assert.Contains(Text.OtherServiceIsOther, WebUtility.HtmlDecode(await client.GetStringAsync("/Admin/Services")));
    }
}
