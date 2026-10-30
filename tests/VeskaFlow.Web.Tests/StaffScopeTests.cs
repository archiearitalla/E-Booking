using System.Net;
using Microsoft.EntityFrameworkCore;
using VeskaFlow.Core.Domain;
using VeskaFlow.Core.Scheduling;
using VeskaFlow.Web.Data;
using VeskaFlow.Web.Services;

namespace VeskaFlow.Web.Tests;

/// <summary>A Staff login tied to one staff member sees and changes only that person's bookings.</summary>
[Collection("http")]
public class StaffScopeTests
{
    private static string Id(string prefix) => prefix + Guid.NewGuid().ToString("N")[..10];

    private static async Task<HttpClient> SignInAsync(AppFactory factory, string email, string password)
    {
        var client = HttpApp.NewClient(factory);
        var response = await HttpApp.PostFormAsync(client, "/Login", new() { ["Input.Email"] = email, ["Input.Password"] = password });
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        return client;
    }

    [Fact]
    public async Task A_tied_staff_login_sees_and_reaches_only_its_own_staff_members_work()
    {
        var (factory, _) = await HttpApp.StartAsync();
        using var _f = factory;
        var db = new TestDbFactory(await TestDb.ConnectionStringAsync());
        var owner = (await new OnboardingService(db, TimeProvider.System).RegisterAsync(
            new RegisterRequest("Scope Shop", Id("sc"), "Owner", $"{Id("o")}@example.com", "correct-horse-1", TrialDays: 30))).Value!;

        Guid serviceId, staffA, staffB;
        await using (var ctx = db.CreateDbContext())
        {
            serviceId = await ctx.Services.Where(s => s.BusinessId == owner.BusinessId && !s.IsOther).Select(s => s.Id).SingleAsync();
            staffA = await ctx.StaffMembers.Where(s => s.BusinessId == owner.BusinessId).Select(s => s.Id).SingleAsync();
            var hours = await ctx.WorkingHours.Where(h => h.StaffId == staffA).ToListAsync();
            var second = new Staff { BusinessId = owner.BusinessId, Name = "Second Barber", SortOrder = 1 };
            second.StaffServices.Add(new StaffService { ServiceId = serviceId, BusinessId = owner.BusinessId });
            foreach (var h in hours) second.WorkingHours.Add(new WorkingHour { DayOfWeek = h.DayOfWeek, OpenTime = h.OpenTime, CloseTime = h.CloseTime });
            ctx.Add(second);
            await ctx.SaveChangesAsync();
            staffB = second.Id;
        }

        // One booking for each staff member, tomorrow in the shop's time.
        var bookings = new BookingService(db, TimeProvider.System);
        var day = Seed.NextOpenDay();
        async Task<BookingSummary> Book(Guid staff, string name, string phone)
        {
            var free = (await bookings.GetFreeStartsAsync(owner.BusinessId, serviceId, staff, day)).Value!;
            Assert.NotEmpty(free);
            return (await bookings.CreateAsync(new CreateBookingRequest(owner.BusinessId, serviceId, staff, free[0], name, phone, null,
                Guid.NewGuid().ToString("N"), BookingActor.Customer(name)))).Value!.Booking;
        }
        var mine = await Book(staffA, "Alpha Customer", "09175550601");
        var theirs = await Book(staffB, "Bravo Customer", "09175550602");
        Guid customerA, customerB;
        await using (var ctx = db.CreateDbContext())
        {
            customerA = await ctx.Bookings.Where(b => b.Id == mine.Id).Select(b => b.CustomerId).SingleAsync();
            customerB = await ctx.Bookings.Where(b => b.Id == theirs.Id).Select(b => b.CustomerId).SingleAsync();
        }

        var team = new TeamService(db, TimeProvider.System);
        var tiedEmail = $"{Id("t")}@example.com";
        var openEmail = $"{Id("u")}@example.com";
        var managerEmail = $"{Id("m")}@example.com";
        Assert.True((await team.AddAsync(owner.BusinessId, "Tied Staff", tiedEmail, "STAFF", "staff-password-1", staffId: staffA)).IsSuccess);
        Assert.True((await team.AddAsync(owner.BusinessId, "Open Staff", openEmail, "STAFF", "staff-password-1")).IsSuccess);
        Assert.True((await team.AddAsync(owner.BusinessId, "Manager", managerEmail, "MANAGER", "manager-password-1", staffId: staffA)).IsSuccess); // a manager is never tied

        var tied = await SignInAsync(factory, tiedEmail, "staff-password-1");
        var open = await SignInAsync(factory, openEmail, "staff-password-1");
        var manager = await SignInAsync(factory, managerEmail, "manager-password-1");
        var path = $"/Admin?date={DisplayFormat.DateParam(day)}";

        var tiedDay = await tied.GetStringAsync(path);
        Assert.Contains("Alpha Customer", tiedDay);
        Assert.DoesNotContain("Bravo Customer", tiedDay);
        foreach (var client in new[] { open, manager })
        {
            var all = await client.GetStringAsync(path);
            Assert.Contains("Alpha Customer", all);
            Assert.Contains("Bravo Customer", all);
        }

        // The other staff member's booking and customer are "not found" for the tied login, whatever page is tried.
        Assert.Equal(HttpStatusCode.NotFound, (await tied.GetAsync($"/Admin?handler=Contact&id={theirs.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await tied.GetAsync($"/Admin?handler=Contact&id={mine.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await tied.GetAsync($"/Admin/Reschedule/{theirs.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await tied.GetAsync($"/Admin/Reschedule/{mine.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await tied.GetAsync($"/Admin/AfterVisit/{theirs.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await tied.GetAsync($"/Admin/History/{customerB}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await tied.GetAsync($"/Admin/History/{customerA}")).StatusCode);
        var cancel = await HttpApp.PostFormAsync(tied, "/Admin?handler=Transition",
            new() { ["id"] = theirs.Id.ToString(), ["action"] = "cancel", ["reason"] = "not mine" }, tokenPage: "/Admin");
        Assert.Equal(HttpStatusCode.NotFound, cancel.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await open.GetAsync($"/Admin/Reschedule/{theirs.Id}")).StatusCode); // an untied login still sees the whole shop

        // The tied login's walk-in page has no staff picker and books for its own staff member.
        var walkIn = await tied.GetStringAsync("/Admin/WalkIn");
        Assert.DoesNotContain("id=\"staffId\"", walkIn);
        Assert.Contains("id=\"staffId\"", await open.GetStringAsync("/Admin/WalkIn"));

        await using var check = db.CreateDbContext();
        Assert.Equal(BookingStatus.Confirmed, (await check.Bookings.AsNoTracking().SingleAsync(b => b.Id == theirs.Id)).Status);
    }

    [Fact]
    public async Task Reminders_follow_ups_and_the_team_link_respect_the_staff_member()
    {
        var shop = await Seed.CreateShopAsync(staffCount: 2);
        var db = new TestDbFactory(shop.ConnectionString);
        var clock = new TestClock(Seed.BaseNow);
        var bookings = new BookingService(db, clock);
        var a = (await bookings.CreateAsync(Seed.Request(shop, Seed.Manila(5, 10), staffId: shop.StaffIds[0]))).Value!.Booking;
        var b = (await bookings.CreateAsync(Seed.Request(shop, Seed.Manila(5, 11), staffId: shop.StaffIds[1]))).Value!.Booking;

        clock.Now = new DateTimeOffset(Seed.Manila(4, 20), TimeSpan.Zero); // both inside the reminder window
        var reminders = new ReminderService(db, clock);
        Assert.Equal(2, (await reminders.ListPendingAsync(shop.BusinessId)).Count);
        var only = Assert.Single(await reminders.ListPendingAsync(shop.BusinessId, onlyStaffId: shop.StaffIds[0]));
        Assert.Equal(a.Id, only.BookingId);
        Assert.Equal(FailureKind.NotFound, (await reminders.MarkRemindedAsync(shop.BusinessId, b.Id, onlyStaffId: shop.StaffIds[0])).Failure);
        Assert.True((await reminders.MarkRemindedAsync(shop.BusinessId, a.Id, onlyStaffId: shop.StaffIds[0])).IsSuccess);

        var followUps = new FollowUpService(db, clock);
        Assert.Equal(1, (await followUps.AlertAsync(shop.BusinessId, onlyStaffId: shop.StaffIds[1])).Reminders); // b is still due
        Assert.Equal(0, (await followUps.AlertAsync(shop.BusinessId, onlyStaffId: shop.StaffIds[0])).Reminders);

        // A staff member from another shop cannot be linked, and only a Staff login can be tied.
        var other = await Seed.CreateShopAsync();
        await using var ctx = db.CreateDbContext();
        var owner = new AdminUser { BusinessId = shop.BusinessId, Email = $"{Id("ow")}@example.com", DisplayName = "Owner", PasswordHash = "x", Role = "OWNER", EmailConfirmedAtUtc = DateTime.UtcNow };
        ctx.Add(owner);
        await ctx.SaveChangesAsync();
        var team = new TeamService(db, clock);
        Assert.Contains("staffId", (await team.AddAsync(shop.BusinessId, "Sam", $"{Id("s")}@example.com", "STAFF", "staff-password-1", staffId: other.StaffIds[0])).FieldErrors.Keys);
        var sam = (await team.AddAsync(shop.BusinessId, "Sam", $"{Id("s")}@example.com", "STAFF", "staff-password-1", staffId: shop.StaffIds[0])).Value!;
        Assert.Equal(shop.StaffIds[0], (await team.GetAccessAsync(sam.Id, shop.BusinessId))!.StaffId);
        Assert.True((await team.SetStaffLinkAsync(shop.BusinessId, sam.Id, null)).IsSuccess);
        Assert.Null((await team.GetAccessAsync(sam.Id, shop.BusinessId))!.StaffId);
        Assert.Equal(FailureKind.PolicyViolation, (await team.SetStaffLinkAsync(shop.BusinessId, owner.Id, shop.StaffIds[0])).Failure);
        Assert.Equal(FailureKind.NotFound, (await team.SetStaffLinkAsync(other.BusinessId, sam.Id, other.StaffIds[0])).Failure); // another shop's person
        Assert.Contains("staffId", (await team.SetStaffLinkAsync(shop.BusinessId, sam.Id, other.StaffIds[0])).FieldErrors.Keys);
    }
}
