using System.Net;
using VeskaFlow.Core.Domain;
using VeskaFlow.Web.Services;
using Microsoft.EntityFrameworkCore;
using static VeskaFlow.Web.Tests.Seed;

namespace VeskaFlow.Web.Tests;

/// <summary>Reminders for customers before their appointment: who is due, a ready message, and marking them reminded.</summary>
[Collection("http")]
public class ReminderTests
{
    private static string Id(string prefix) => prefix + Guid.NewGuid().ToString("N")[..10];

    private sealed record Setup(TestDbFactory Db, TestClock Clock, BookingService Bookings, ReminderService Reminders, ShopSettingsService Settings,
        FollowUpService FollowUps, RegisteredOwner Owner, Guid ServiceId, Guid StaffId);

    private static async Task<Setup> SetupAsync()
    {
        var db = new TestDbFactory(await TestDb.ConnectionStringAsync());
        var clock = new TestClock(BaseNow);
        var owner = (await new OnboardingService(db, clock).RegisterAsync(
            new RegisterRequest("Clinic", Id("rm"), "Dr A", $"{Id("o")}@example.com", "correct-horse-1", Industry: "clinic", TrialDays: 30))).Value!;
        await using var ctx = db.CreateDbContext();
        var service = await ctx.Services.Where(s => s.BusinessId == owner.BusinessId && !s.IsOther).Select(s => s.Id).SingleAsync();
        var staff = await ctx.StaffMembers.Where(s => s.BusinessId == owner.BusinessId).Select(s => s.Id).SingleAsync();
        return new Setup(db, clock, new BookingService(db, clock), new ReminderService(db, clock), new ShopSettingsService(db, clock),
            new FollowUpService(db, clock), owner, service, staff);
    }

    private static async Task<BookingSummary> BookAsync(Setup s, int day, int hour, string phone) =>
        (await s.Bookings.CreateAsync(new CreateBookingRequest(s.Owner.BusinessId, s.ServiceId, s.StaffId, Manila(day, hour), "Maria Santos",
            phone, null, Id("k"), BookingActor.Customer("Maria")))).Value!.Booking;

    [Fact]
    public async Task A_customer_is_due_a_reminder_inside_the_reminder_hours_until_marked_reminded()
    {
        var s = await SetupAsync(); // BaseNow is the start of 2030-03-04 Manila time; reminder hours default to 24
        var tomorrow = await BookAsync(s, 5, 10, "09175550301");   // about 34 hours ahead
        var later = await BookAsync(s, 7, 10, "09175550302");      // days ahead

        Assert.Empty(await s.Reminders.ListPendingAsync(s.Owner.BusinessId));

        s.Clock.Now = new DateTimeOffset(Manila(4, 18), TimeSpan.Zero); // 16 hours before the first appointment
        var due = Assert.Single(await s.Reminders.ListPendingAsync(s.Owner.BusinessId));
        Assert.Equal(tomorrow.Id, due.BookingId);
        Assert.Equal(tomorrow.ManageToken, due.ManageToken);
        Assert.Equal(1, (await s.FollowUps.AlertAsync(s.Owner.BusinessId)).Reminders);

        Assert.True((await s.Reminders.MarkRemindedAsync(s.Owner.BusinessId, tomorrow.Id)).IsSuccess);
        Assert.Empty(await s.Reminders.ListPendingAsync(s.Owner.BusinessId));
        Assert.Equal(0, (await s.FollowUps.AlertAsync(s.Owner.BusinessId)).Reminders);

        // The other appointment becomes due when it is inside the window, and a cancelled booking never is.
        s.Clock.Now = new DateTimeOffset(Manila(6, 11), TimeSpan.Zero);
        Assert.Equal(later.Id, Assert.Single(await s.Reminders.ListPendingAsync(s.Owner.BusinessId)).BookingId);
        Assert.True((await s.Bookings.TransitionAsync(s.Owner.BusinessId, later.Id, BookingStatus.Cancelled,
            BookingActor.Staff(s.Owner.AdminUserId, "Dr A"), reason: "test")).IsSuccess);
        Assert.Empty(await s.Reminders.ListPendingAsync(s.Owner.BusinessId));

        // A started appointment is past due for a reminder.
        s.Clock.Now = new DateTimeOffset(Manila(5, 11), TimeSpan.Zero);
        Assert.Empty(await s.Reminders.ListPendingAsync(s.Owner.BusinessId));
    }

    [Fact]
    public async Task The_shop_chooses_how_many_hours_before_and_another_shop_never_sees_or_marks_a_reminder()
    {
        var s = await SetupAsync();
        var other = await SetupAsync();
        var booking = await BookAsync(s, 6, 10, "09175550303"); // about 82 hours ahead of BaseNow
        Assert.Empty(await s.Reminders.ListPendingAsync(s.Owner.BusinessId));

        async Task<ServiceResult<bool>> Hours(int hours)
        {
            var current = (await s.Settings.GetSettingsAsync(s.Owner.BusinessId))!;
            return await s.Settings.SaveSettingsAsync(s.Owner.BusinessId, new SettingsInput(current.Name, current.TimeZone, current.SlotMinutes,
                current.LeadTimeMinutes, current.HorizonDays, current.CancelCutoffHours, null, null, hours));
        }

        Assert.True((await Hours(96)).IsSuccess);
        Assert.Single(await s.Reminders.ListPendingAsync(s.Owner.BusinessId));
        Assert.Contains("reminderHours", (await Hours(0)).FieldErrors.Keys);
        Assert.Contains("reminderHours", (await Hours(169)).FieldErrors.Keys);

        Assert.Empty(await other.Reminders.ListPendingAsync(other.Owner.BusinessId));
        Assert.Equal(FailureKind.NotFound, (await other.Reminders.MarkRemindedAsync(other.Owner.BusinessId, booking.Id)).Failure);
        Assert.Single(await s.Reminders.ListPendingAsync(s.Owner.BusinessId)); // not marked by the other shop
    }

    [Fact]
    public async Task The_reminders_page_shows_a_ready_message_with_the_cancel_link_and_the_header_announces_it()
    {
        var (factory, _) = await HttpApp.StartAsync();
        using var _f = factory;
        var client = HttpApp.NewClient(factory);
        var slug = Id("rp");
        await HttpApp.PostFormAsync(client, "/Register", new()
        {
            ["Input.Accept"] = "true", ["Input.BusinessName"] = "Reminder Clinic", ["Input.Slug"] = slug, ["Input.OwnerName"] = "Dr R",
            ["Input.Email"] = $"{Id("o")}@example.com", ["Input.Password"] = "correct-horse-1", ["Input.Industry"] = "clinic",
        });
        var db = new TestDbFactory(await TestDb.ConnectionStringAsync());
        await using var ctx = db.CreateDbContext();
        var shopId = await ctx.Businesses.Where(b => b.Slug == slug).Select(b => b.Id).SingleAsync();
        var serviceId = await ctx.Services.Where(x => x.BusinessId == shopId && !x.IsOther).Select(x => x.Id).SingleAsync();
        var staffId = await ctx.StaffMembers.Where(x => x.BusinessId == shopId).Select(x => x.Id).SingleAsync();

        Assert.Contains(Text.RemindersEmpty, WebUtility.HtmlDecode(await client.GetStringAsync("/Admin/Reminders")));

        // A confirmed appointment about 5 hours from now, found by looking for a free time today or tomorrow.
        var svc = new BookingService(db, TimeProvider.System);
        BookingSummary? booked = null;
        for (var d = 0; d < 3 && booked is null; d++)
        {
            var day = DateOnly.FromDateTime(DateTime.UtcNow.AddHours(8).AddDays(d));
            var free = (await svc.GetFreeStartsAsync(shopId, serviceId, staffId, day)).Value!;
            var start = free.FirstOrDefault(t => t > DateTime.UtcNow.AddHours(1) && t < DateTime.UtcNow.AddHours(23));
            if (start == default) continue;
            booked = (await svc.CreateAsync(new CreateBookingRequest(shopId, serviceId, staffId, start, "Maria Santos", "09175550304", null, Id("k"),
                BookingActor.Customer("Maria")))).Value!.Booking;
        }
        Assert.NotNull(booked);

        var page = WebUtility.HtmlDecode(await client.GetStringAsync("/Admin/Reminders"));
        Assert.Contains("this is a reminder of your", page);
        Assert.Contains($"/booking/{booked!.ManageToken}", page);
        Assert.Contains("Send as text", page);
        Assert.Contains($"data-confirm=\"{Text.ConfirmReminded}\"", page);

        var admin = WebUtility.HtmlDecode(await client.GetStringAsync("/Admin"));
        Assert.Contains(Text.ReminderAlertText(1), admin);

        var marked = await HttpApp.PostFormAsync(client, "/Admin/Reminders?handler=Reminded", new() { ["id"] = booked.Id.ToString() }, tokenPage: "/Admin/Reminders");
        Assert.Equal(HttpStatusCode.Redirect, marked.StatusCode);
        Assert.Contains(Text.RemindersEmpty, WebUtility.HtmlDecode(await client.GetStringAsync("/Admin/Reminders")));
    }
}
