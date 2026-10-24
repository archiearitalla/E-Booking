using System.Net;
using System.Text.RegularExpressions;
using BarberBook.Core.Domain;
using BarberBook.Web.Data;
using BarberBook.Web.Services;
using Microsoft.EntityFrameworkCore;
using static BarberBook.Web.Tests.HttpApp;

namespace BarberBook.Web.Tests;

/// <summary>The back office over HTTP: real app, real PostgreSQL, a signed-in owner per test.</summary>
[Collection("http")]
public class AdminWebTests
{
    private sealed record Owner(HttpClient Client, Guid BusinessId, Guid StaffId, Guid ServiceId, string Slug, TestDbFactory Db);

    private static readonly string[] AdminPages =
    [
        "/Admin", "/Admin/Services", "/Admin/Services/Edit", "/Admin/Staff", "/Admin/Staff/Edit",
        "/Admin/Closures", "/Admin/Settings", "/Admin/Audit", "/Admin/About",
    ];

    private static async Task<Owner> RegisterOwnerAsync(AppFactory factory)
    {
        var client = NewClient(factory);
        var slug = Unique("adm");
        var response = await PostFormAsync(client, "/Register", new()
        {
            ["Input.BusinessName"] = "Admin Test Shop", ["Input.Slug"] = slug, ["Input.OwnerName"] = "Ben Santos",
            ["Input.Email"] = $"{Unique("o")}@example.com", ["Input.Password"] = "correct-horse-1",
        });
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);

        var db = new TestDbFactory(await TestDb.ConnectionStringAsync());
        await using var ctx = db.CreateDbContext();
        var businessId = await ctx.Businesses.Where(b => b.Slug == slug).Select(b => b.Id).SingleAsync();
        return new Owner(client, businessId,
            await ctx.StaffMembers.Where(s => s.BusinessId == businessId).Select(s => s.Id).SingleAsync(),
            await ctx.Services.Where(s => s.BusinessId == businessId).Select(s => s.Id).SingleAsync(), slug, db);
    }

    private static Dictionary<string, string> Hidden(string html)
    {
        var fields = new Dictionary<string, string>();
        foreach (Match tag in Regex.Matches(html, "<input[^>]*>"))
        {
            if (!Regex.IsMatch(tag.Value, "type=\"hidden\"")) continue;
            fields[Regex.Match(tag.Value, "name=\"([^\"]*)\"").Groups[1].Value] =
                WebUtility.HtmlDecode(Regex.Match(tag.Value, "value=\"([^\"]*)\"").Groups[1].Value);
        }
        return fields;
    }

    /// <summary>A post as the browser would send it: the page's hidden fields (antiforgery token) plus the given ones.</summary>
    private static async Task<HttpResponseMessage> PostAsync(HttpClient client, string tokenPage, string url, Dictionary<string, string> fields)
    {
        var all = Hidden(await client.GetStringAsync(tokenPage));
        foreach (var (k, v) in fields) all[k] = v;
        return await client.PostAsync(url, new FormUrlEncodedContent(all));
    }

    private static async Task<BookingSummary> BookNextFreeAsync(Owner owner, string phone = "09170000001")
    {
        var svc = new BookingService(owner.Db, TimeProvider.System);
        var today = DateOnly.FromDateTime(DateTime.UtcNow.AddHours(8));
        for (var d = 1; d < 14; d++)
        {
            var free = await svc.GetFreeStartsAsync(owner.BusinessId, owner.ServiceId, owner.StaffId, today.AddDays(d));
            if (free.Value!.Count == 0) continue;
            var r = await svc.CreateAsync(new CreateBookingRequest(owner.BusinessId, owner.ServiceId, owner.StaffId, free.Value[0],
                "Juan Dela Cruz", phone, null, Unique("k"), BookingActor.Customer("Juan Dela Cruz")));
            return r.Value!.Booking;
        }
        throw new InvalidOperationException("No free slot in the next two weeks.");
    }

    private static string Dec(string html) => WebUtility.HtmlDecode(html);

    private static string DateOf(BookingSummary b) =>
        DateOnly.FromDateTime(b.StartsAtUtc.AddHours(8)).ToString("yyyy-MM-dd");

    // ------------------------------------------------------------ access

    [Fact]
    public async Task Every_admin_page_requires_sign_in()
    {
        var (factory, anonymous) = await HttpApp.StartAsync();
        using var _ = factory;

        foreach (var page in AdminPages)
        {
            var response = await anonymous.GetAsync(page);
            Assert.True(response.StatusCode == HttpStatusCode.Redirect, $"{page} returned {(int)response.StatusCode}");
            Assert.Contains("/Login", response.Headers.Location!.OriginalString);
        }
    }

    [Fact]
    public async Task A_signed_in_owner_sees_every_page_with_the_role_badge_and_a_current_nav_item()
    {
        var (factory, _) = await HttpApp.StartAsync();
        using var _f = factory;
        var owner = await RegisterOwnerAsync(factory);

        foreach (var page in AdminPages)
        {
            var response = await owner.Client.GetAsync(page);
            Assert.True(response.StatusCode == HttpStatusCode.OK, $"{page} returned {(int)response.StatusCode}");
            var html = await response.Content.ReadAsStringAsync();
            Assert.Contains("class=\"badge\" title=\"Your role\">OWNER", html); // role badge
            Assert.Contains("aria-label=\"Back office\"", html);               // landmark for keyboard and screen reader users
            Assert.Contains("class=\"skip-link\"", html);                      // skip to content
            Assert.Contains("aria-current=\"page\"", html);                    // current section announced
        }
    }

    [Fact]
    public async Task Empty_states_are_explained_and_success_and_error_states_are_shown()
    {
        var (factory, _) = await HttpApp.StartAsync();
        using var _f = factory;
        var owner = await RegisterOwnerAsync(factory);

        Assert.Contains(Text.DayEmpty, await owner.Client.GetStringAsync("/Admin"));
        Assert.Contains(Text.ClosuresEmpty, await owner.Client.GetStringAsync("/Admin/Closures"));
        Assert.Contains(Text.AuditEmpty, await owner.Client.GetStringAsync("/Admin/Audit"));

        // error state: invalid service
        var bad = await PostAsync(owner.Client, "/Admin/Services/Edit", "/Admin/Services/Edit",
            new() { ["Input.Name"] = "", ["Input.DurationMinutes"] = "1", ["Input.BufferMinutes"] = "0", ["Input.Price"] = "abc", ["Input.SortOrder"] = "0" });
        Assert.Equal(HttpStatusCode.OK, bad.StatusCode);
        var badHtml = Dec(await bad.Content.ReadAsStringAsync());
        Assert.Contains(Text.ServiceNameInvalid, badHtml);
        Assert.Contains(Text.PriceInvalid, badHtml);
        Assert.Contains("role=\"alert\"", badHtml);

        // success state: a valid service, shown after the redirect
        var ok = await PostAsync(owner.Client, "/Admin/Services/Edit", "/Admin/Services/Edit",
            new() { ["Input.Name"] = "Beard trim", ["Input.DurationMinutes"] = "20", ["Input.BufferMinutes"] = "5", ["Input.Price"] = "80.50", ["Input.SortOrder"] = "1", ["Input.IsActive"] = "true" });
        Assert.Equal(HttpStatusCode.Redirect, ok.StatusCode);
        var list = Dec(await owner.Client.GetStringAsync("/Admin/Services"));
        Assert.Contains(Text.ServiceSaved, list);
        Assert.Contains("Beard trim", list);
        Assert.Contains("role=\"status\"", list);
    }

    // ------------------------------------------------------------ tenant isolation

    [Fact]
    public async Task A_forged_id_from_another_business_is_not_found_and_changes_nothing()
    {
        var (factory, _) = await HttpApp.StartAsync();
        using var _f = factory;
        var a = await RegisterOwnerAsync(factory);
        var b = await RegisterOwnerAsync(factory);
        var victimBooking = await BookNextFreeAsync(b);
        var victimClosure = (await new ShopSettingsService(b.Db, TimeProvider.System).AddClosureAsync(b.BusinessId,
            new ClosureInput(null, DateOnly.FromDateTime(DateTime.UtcNow.AddDays(40)), DateOnly.FromDateTime(DateTime.UtcNow.AddDays(41)), "Away"))).Value!;

        // Owner A tries to read and change owner B's records by id.
        Assert.Equal(HttpStatusCode.NotFound, (await a.Client.GetAsync($"/Admin/Services/Edit/{b.ServiceId}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await a.Client.GetAsync($"/Admin/Staff/Edit/{b.StaffId}")).StatusCode);

        var editService = await PostAsync(a.Client, "/Admin/Services/Edit", $"/Admin/Services/Edit/{b.ServiceId}",
            new() { ["Input.Name"] = "Hacked", ["Input.DurationMinutes"] = "30", ["Input.BufferMinutes"] = "0", ["Input.Price"] = "1", ["Input.SortOrder"] = "0", ["Input.IsActive"] = "true" });
        Assert.Equal(HttpStatusCode.NotFound, editService.StatusCode);

        var editStaff = await PostAsync(a.Client, "/Admin/Staff/Edit", $"/Admin/Staff/Edit/{b.StaffId}?handler=Save",
            new() { ["Input.Name"] = "Hacked", ["Input.SortOrder"] = "0", ["Input.IsActive"] = "true" });
        Assert.Equal(HttpStatusCode.NotFound, editStaff.StatusCode);

        var hours = await PostAsync(a.Client, "/Admin/Staff/Edit", $"/Admin/Staff/Edit/{b.StaffId}?handler=Hours",
            new() { ["open_1_0"] = "00:00", ["close_1_0"] = "23:00" });
        Assert.Equal(HttpStatusCode.NotFound, hours.StatusCode);

        var transition = await PostAsync(a.Client, "/Admin", "/Admin?handler=Transition",
            new() { ["id"] = victimBooking.Id.ToString(), ["action"] = "cancel", ["reason"] = "x", ["date"] = DateOf(victimBooking) });
        Assert.Equal(HttpStatusCode.NotFound, transition.StatusCode);

        var deleteClosure = await PostAsync(a.Client, "/Admin/Closures", "/Admin/Closures?handler=Delete", new() { ["id"] = victimClosure.Id.ToString() });
        Assert.Equal(HttpStatusCode.NotFound, deleteClosure.StatusCode);

        var foreignStaffClosure = await PostAsync(a.Client, "/Admin/Closures", "/Admin/Closures?handler=Add",
            new() { ["Input.StaffId"] = b.StaffId.ToString(), ["Input.FromDate"] = "2031-01-01", ["Input.ToDate"] = "2031-01-02" });
        Assert.Equal(HttpStatusCode.NotFound, foreignStaffClosure.StatusCode);

        // Nothing of B's changed.
        await using var ctx = b.Db.CreateDbContext();
        Assert.Equal("Haircut", (await ctx.Services.SingleAsync(s => s.Id == b.ServiceId)).Name);
        Assert.Equal("Ben Santos", (await ctx.StaffMembers.SingleAsync(s => s.Id == b.StaffId)).Name);
        Assert.Equal(6, await ctx.WorkingHours.CountAsync(h => h.StaffId == b.StaffId));
        Assert.Equal(BookingStatus.Confirmed, (await ctx.Bookings.SingleAsync(x => x.Id == victimBooking.Id)).Status);
        Assert.Equal(1, await ctx.Blackouts.CountAsync(x => x.BusinessId == b.BusinessId));
        Assert.Equal(0, await ctx.Blackouts.CountAsync(x => x.BusinessId == a.BusinessId));
    }

    [Fact]
    public async Task The_day_view_and_audit_page_never_show_another_businesss_bookings()
    {
        var (factory, _) = await HttpApp.StartAsync();
        using var _f = factory;
        var a = await RegisterOwnerAsync(factory);
        var b = await RegisterOwnerAsync(factory);
        var theirs = await BookNextFreeAsync(b, "09175550199");

        var day = await a.Client.GetStringAsync($"/Admin?date={DateOf(theirs)}");
        Assert.Contains(Text.DayEmpty, day);
        Assert.DoesNotContain("09175550199", day);
        Assert.DoesNotContain("09175550199", await a.Client.GetStringAsync("/Admin/Audit"));
    }

    // ------------------------------------------------------------ bookings, closures, audit

    [Fact]
    public async Task Staff_can_cancel_a_booking_with_a_reason_and_it_appears_in_the_audit_log()
    {
        var (factory, _) = await HttpApp.StartAsync();
        using var _f = factory;
        var owner = await RegisterOwnerAsync(factory);
        var booking = await BookNextFreeAsync(owner);

        var day = await owner.Client.GetStringAsync($"/Admin?date={DateOf(booking)}");
        Assert.Contains("Juan Dela Cruz", day);
        Assert.Contains(Text.ActionCancel, day);

        var cancel = await PostAsync(owner.Client, $"/Admin?date={DateOf(booking)}", "/Admin?handler=Transition",
            new() { ["id"] = booking.Id.ToString(), ["version"] = booking.Version.ToString(), ["action"] = "cancel", ["reason"] = "Barber is sick", ["date"] = DateOf(booking) });
        Assert.Equal(HttpStatusCode.Redirect, cancel.StatusCode);

        Assert.Contains(Text.BookingUpdated, Dec(await owner.Client.GetStringAsync($"/Admin?date={DateOf(booking)}")));
        await using var ctx = owner.Db.CreateDbContext();
        var row = await ctx.Bookings.SingleAsync(x => x.Id == booking.Id);
        Assert.Equal((BookingStatus.Cancelled, "STAFF", "Barber is sick"), (row.Status, row.CancelledBy, row.CancelReason));

        var audit = Dec(await owner.Client.GetStringAsync("/Admin/Audit"));
        Assert.Contains("Barber is sick", audit);
        Assert.Contains("CANCELLED", audit);
        Assert.Contains("Ben Santos", audit);
    }

    [Fact]
    public async Task A_stale_cancel_form_is_refused_with_a_refresh_message()
    {
        var (factory, _) = await HttpApp.StartAsync();
        using var _f = factory;
        var owner = await RegisterOwnerAsync(factory);
        var booking = await BookNextFreeAsync(owner);

        var response = await PostAsync(owner.Client, $"/Admin?date={DateOf(booking)}", "/Admin?handler=Transition",
            new() { ["id"] = booking.Id.ToString(), ["version"] = (booking.Version + 7).ToString(), ["action"] = "cancel", ["date"] = DateOf(booking) });

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Contains(Text.BookingChanged, Dec(await owner.Client.GetStringAsync($"/Admin?date={DateOf(booking)}")));
        await using var ctx = owner.Db.CreateDbContext();
        Assert.Equal(BookingStatus.Confirmed, (await ctx.Bookings.SingleAsync(x => x.Id == booking.Id)).Status);
    }

    [Fact]
    public async Task Closing_a_day_with_a_booking_keeps_the_booking_and_shows_how_many_are_affected()
    {
        var (factory, _) = await HttpApp.StartAsync();
        using var _f = factory;
        var owner = await RegisterOwnerAsync(factory);
        var booking = await BookNextFreeAsync(owner);
        var date = DateOf(booking);

        var add = await PostAsync(owner.Client, "/Admin/Closures", "/Admin/Closures?handler=Add",
            new() { ["Input.FromDate"] = date, ["Input.ToDate"] = date, ["Input.Reason"] = "Public holiday" });
        Assert.Equal(HttpStatusCode.Redirect, add.StatusCode);

        var page = Dec(await owner.Client.GetStringAsync("/Admin/Closures"));
        Assert.Contains(string.Format(Text.ClosureSavedAffected, 1), page);   // the one-time message after saving
        Assert.Contains(string.Format(Text.ClosureAffected, 1), page);         // and the standing warning on the list
        Assert.Contains("Public holiday", page);

        await using var ctx = owner.Db.CreateDbContext();
        Assert.Equal(BookingStatus.Confirmed, (await ctx.Bookings.SingleAsync(x => x.Id == booking.Id)).Status);

        // The public page offers no times that day.
        var times = await owner.Client.GetStringAsync($"/b/{owner.Slug}?serviceId={owner.ServiceId}&date={date}");
        Assert.DoesNotContain("class=\"slot\"", times);
    }

    [Fact]
    public async Task Changing_hours_over_a_booking_warns_and_keeps_the_booking()
    {
        var (factory, _) = await HttpApp.StartAsync();
        using var _f = factory;
        var owner = await RegisterOwnerAsync(factory);
        var booking = await BookNextFreeAsync(owner);

        // Work Sundays only: the starter hours were Monday to Saturday, so the booking is now outside the hours.
        var response = await PostAsync(owner.Client, $"/Admin/Staff/Edit/{owner.StaffId}", $"/Admin/Staff/Edit/{owner.StaffId}?handler=Hours",
            new() { ["open_0_0"] = "09:00", ["close_0_0"] = "10:00" });
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);

        var page = Dec(await owner.Client.GetStringAsync($"/Admin/Staff/Edit/{owner.StaffId}"));
        Assert.Contains("not cancelled", page);
        await using var ctx = owner.Db.CreateDbContext();
        Assert.Equal(BookingStatus.Confirmed, (await ctx.Bookings.SingleAsync(x => x.Id == booking.Id)).Status);
        Assert.Equal(1, await ctx.WorkingHours.CountAsync(h => h.StaffId == owner.StaffId));
    }

    [Fact]
    public async Task Half_filled_hours_are_refused_with_a_message_and_nothing_changes()
    {
        var (factory, _) = await HttpApp.StartAsync();
        using var _f = factory;
        var owner = await RegisterOwnerAsync(factory);

        var response = await PostAsync(owner.Client, $"/Admin/Staff/Edit/{owner.StaffId}", $"/Admin/Staff/Edit/{owner.StaffId}?handler=Hours",
            new() { ["open_1_0"] = "09:00", ["close_1_0"] = "" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains(Text.HoursIncomplete, Dec(await response.Content.ReadAsStringAsync()));
        await using var ctx = owner.Db.CreateDbContext();
        Assert.Equal(6, await ctx.WorkingHours.CountAsync(h => h.StaffId == owner.StaffId));
    }

    // ------------------------------------------------------------ staff, settings, about

    [Fact]
    public async Task The_staff_page_enforces_the_free_plan_limit_for_a_business_with_no_trial()
    {
        var (factory, _) = await HttpApp.StartAsync();
        using var _f = factory;
        var owner = await RegisterOwnerAsync(factory);
        await using (var setup = owner.Db.CreateDbContext()) // a trial has no staff limit, so remove the trial to test the FREE plan
            await setup.Businesses.Where(b => b.Id == owner.BusinessId).ExecuteUpdateAsync(s => s.SetProperty(b => b.TrialEndsAtUtc, (DateTime?)null));

        var response = await PostAsync(owner.Client, "/Admin/Staff/Edit", "/Admin/Staff/Edit?handler=Save",
            new() { ["Input.Name"] = "Carl", ["Input.SortOrder"] = "1", ["Input.IsActive"] = "true", ["Input.ServiceIds"] = owner.ServiceId.ToString() });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains(string.Format(Text.StaffLimit, 1), Dec(await response.Content.ReadAsStringAsync()));
        await using var ctx = owner.Db.CreateDbContext();
        Assert.Equal(1, await ctx.StaffMembers.CountAsync(s => s.BusinessId == owner.BusinessId));
    }

    [Fact]
    public async Task Settings_can_be_saved_and_invalid_values_are_refused()
    {
        var (factory, _) = await HttpApp.StartAsync();
        using var _f = factory;
        var owner = await RegisterOwnerAsync(factory);

        var bad = await PostAsync(owner.Client, "/Admin/Settings", "/Admin/Settings",
            new() { ["Input.Name"] = "", ["Input.TimeZone"] = "Nowhere/Land", ["Input.SlotMinutes"] = "7", ["Input.LeadTimeMinutes"] = "-1", ["Input.HorizonDays"] = "0", ["Input.CancelCutoffHours"] = "-3" });
        Assert.Equal(HttpStatusCode.OK, bad.StatusCode);
        var badHtml = Dec(await bad.Content.ReadAsStringAsync());
        Assert.Contains(Text.TimeZoneInvalid, badHtml);
        Assert.Contains(Text.HorizonInvalid, badHtml);

        var ok = await PostAsync(owner.Client, "/Admin/Settings", "/Admin/Settings",
            new() { ["Input.Name"] = "Renamed Shop", ["Input.TimeZone"] = "Asia/Manila", ["Input.SlotMinutes"] = "15", ["Input.LeadTimeMinutes"] = "30", ["Input.HorizonDays"] = "45", ["Input.CancelCutoffHours"] = "6" });
        Assert.Equal(HttpStatusCode.Redirect, ok.StatusCode);
        Assert.Contains(Text.SettingsSaved, Dec(await owner.Client.GetStringAsync("/Admin/Settings")));

        await using var ctx = owner.Db.CreateDbContext();
        var business = await ctx.Businesses.SingleAsync(x => x.Id == owner.BusinessId);
        Assert.Equal(("Renamed Shop", 15, 30, 45, 6), (business.Name, business.SlotMinutes, business.LeadTimeMinutes, business.HorizonDays, business.CancelCutoffHours));
    }

    [Fact]
    public async Task The_about_page_shows_version_and_build()
    {
        var (factory, _) = await HttpApp.StartAsync();
        using var _f = factory;
        var owner = await RegisterOwnerAsync(factory);

        var html = await owner.Client.GetStringAsync("/Admin/About");

        Assert.Contains(AppInfo.Version, html);
        Assert.Contains(AppInfo.Build, html);
        Assert.Contains(AppInfo.Name, html);
    }
}
