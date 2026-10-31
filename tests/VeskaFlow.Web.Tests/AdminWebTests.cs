using System.Net;
using System.Text.RegularExpressions;
using VeskaFlow.Core.Domain;
using VeskaFlow.Web.Data;
using VeskaFlow.Web.Services;
using Microsoft.EntityFrameworkCore;
using static VeskaFlow.Web.Tests.HttpApp;

namespace VeskaFlow.Web.Tests;

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
            ["Input.Accept"] = "true", ["Input.BusinessName"] = "Admin Test Shop", ["Input.Slug"] = slug, ["Input.OwnerName"] = "Ben Santos",
            ["Input.Email"] = $"{Unique("o")}@example.com", ["Input.Password"] = "correct-horse-1",
        });
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);

        var db = new TestDbFactory(await TestDb.ConnectionStringAsync());
        await using var ctx = db.CreateDbContext();
        var businessId = await ctx.Businesses.Where(b => b.Slug == slug).Select(b => b.Id).SingleAsync();
        return new Owner(client, businessId,
            await ctx.StaffMembers.Where(s => s.BusinessId == businessId).Select(s => s.Id).SingleAsync(),
            await ctx.Services.Where(s => s.BusinessId == businessId && !s.IsOther).Select(s => s.Id).SingleAsync(), slug, db);
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
    public async Task A_signed_in_owner_sees_every_page_with_a_current_nav_item_and_the_role_only_on_the_about_page()
    {
        var (factory, _) = await HttpApp.StartAsync();
        using var _f = factory;
        var owner = await RegisterOwnerAsync(factory);

        foreach (var page in AdminPages)
        {
            var response = await owner.Client.GetAsync(page);
            Assert.True(response.StatusCode == HttpStatusCode.OK, $"{page} returned {(int)response.StatusCode}");
            var html = await response.Content.ReadAsStringAsync();
            // The name and role are kept off the working pages; they are on About only.
            var hasRole = html.Contains("class=\"badge\" title=\"Your role\">OWNER");
            Assert.Equal(page == "/Admin/About", hasRole);
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

        Assert.Contains(Text.DayEmptyConfirmed, await owner.Client.GetStringAsync("/Admin"));
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
            new() { ["open_1"] = "00:00", ["close_1"] = "23:00" });
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
        Assert.Contains(Text.DayEmptyConfirmed, day);
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
            new() { ["id"] = booking.Id.ToString(), ["version"] = (booking.Version + 7).ToString(), ["action"] = "cancel", ["reason"] = "Barber is sick", ["date"] = DateOf(booking) });

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
            new() { ["open_0"] = "09:00", ["close_0"] = "10:00" });
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
            new() { ["open_1"] = "09:00", ["close_1"] = "" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains(Text.HoursIncomplete, Dec(await response.Content.ReadAsStringAsync()));
        await using var ctx = owner.Db.CreateDbContext();
        Assert.Equal(6, await ctx.WorkingHours.CountAsync(h => h.StaffId == owner.StaffId));
    }

    [Fact]
    public async Task A_break_in_the_daily_hours_is_saved_as_two_windows_and_shown_again()
    {
        var (factory, _) = await HttpApp.StartAsync();
        using var _f = factory;
        var owner = await RegisterOwnerAsync(factory);
        var url = $"/Admin/Staff/Edit/{owner.StaffId}";

        var saved = await PostAsync(owner.Client, url, $"{url}?handler=Hours",
            new() { ["open_1"] = "09:00", ["close_1"] = "18:00", ["breakfrom_1"] = "12:00", ["breakto_1"] = "13:00" });
        Assert.Equal(HttpStatusCode.Redirect, saved.StatusCode);

        await using (var ctx = owner.Db.CreateDbContext())
        {
            var monday = await ctx.WorkingHours.Where(h => h.StaffId == owner.StaffId && h.DayOfWeek == 1).OrderBy(h => h.OpenTime).ToListAsync();
            Assert.Equal([(new TimeOnly(9, 0), new TimeOnly(12, 0)), (new TimeOnly(13, 0), new TimeOnly(18, 0))],
                monday.Select(h => (h.OpenTime, h.CloseTime)).ToArray());
        }
        var page = Dec(await owner.Client.GetStringAsync(url));
        Assert.Contains("<option value=\"12\" selected", page); // break from 12 PM
        Assert.Contains("<option value=\"PM\" selected", page);

        // A break outside the opening hours, or with only one time, is refused and nothing changes.
        var outside = await PostAsync(owner.Client, url, $"{url}?handler=Hours",
            new() { ["open_1"] = "09:00", ["close_1"] = "12:00", ["breakfrom_1"] = "12:30", ["breakto_1"] = "13:00" });
        Assert.Contains(Text.HoursBreakInvalid, Dec(await outside.Content.ReadAsStringAsync()));
        var half = await PostAsync(owner.Client, url, $"{url}?handler=Hours",
            new() { ["open_1"] = "09:00", ["close_1"] = "18:00", ["breakfrom_1"] = "12:00", ["breakto_1"] = "" });
        Assert.Contains(Text.HoursIncomplete, Dec(await half.Content.ReadAsStringAsync()));
    }

    [Fact]
    public async Task Hours_can_be_picked_with_hour_minute_and_AM_PM_and_are_shown_that_way()
    {
        var (factory, _) = await HttpApp.StartAsync();
        using var _f = factory;
        var owner = await RegisterOwnerAsync(factory);
        var url = $"/Admin/Staff/Edit/{owner.StaffId}";

        var saved = await PostAsync(owner.Client, url, $"{url}?handler=Hours", new()
        {
            ["open_2_h"] = "9", ["open_2_m"] = "00", ["open_2_ap"] = "AM",
            ["close_2_h"] = "6", ["close_2_m"] = "30", ["close_2_ap"] = "PM",
            ["breakfrom_2_h"] = "12", ["breakfrom_2_m"] = "00", ["breakfrom_2_ap"] = "PM",
            ["breakto_2_h"] = "1", ["breakto_2_m"] = "00", ["breakto_2_ap"] = "PM",
        });
        Assert.Equal(HttpStatusCode.Redirect, saved.StatusCode);

        await using var ctx = owner.Db.CreateDbContext();
        var tuesday = await ctx.WorkingHours.Where(h => h.StaffId == owner.StaffId && h.DayOfWeek == 2).OrderBy(h => h.OpenTime).ToListAsync();
        Assert.Equal([(new TimeOnly(9, 0), new TimeOnly(12, 0)), (new TimeOnly(13, 0), new TimeOnly(18, 30))],
            tuesday.Select(h => (h.OpenTime, h.CloseTime)).ToArray());

        var page = Dec(await owner.Client.GetStringAsync(url));
        Assert.Contains("<option value=\"PM\" selected", page);
        Assert.Contains("AM or PM", page);
    }

    [Fact]
    public async Task Settings_takes_one_opening_range_with_a_break_and_applies_it_to_all_days_and_staff()
    {
        var (factory, _) = await HttpApp.StartAsync();
        using var _f = factory;
        var owner = await RegisterOwnerAsync(factory);

        var saved = await PostAsync(owner.Client, "/Admin/Settings", "/Admin/Settings?handler=Hours", new()
        {
            ["open_all_h"] = "10", ["open_all_m"] = "00", ["open_all_ap"] = "AM",
            ["close_all_h"] = "6", ["close_all_m"] = "00", ["close_all_ap"] = "PM",
            ["breakfrom_all_h"] = "12", ["breakfrom_all_m"] = "00", ["breakfrom_all_ap"] = "PM",
            ["breakto_all_h"] = "1", ["breakto_all_m"] = "00", ["breakto_all_ap"] = "PM",
        });
        Assert.Equal(HttpStatusCode.Redirect, saved.StatusCode);

        await using var ctx = owner.Db.CreateDbContext();
        Assert.Equal(14, await ctx.BusinessHours.CountAsync(h => h.BusinessId == owner.BusinessId)); // 7 days x 2 windows
        Assert.Equal(14, await ctx.WorkingHours.CountAsync(h => h.StaffId == owner.StaffId));
        var page = Dec(await owner.Client.GetStringAsync("/Admin/Settings"));
        Assert.Contains("<option value=\"10\" selected", page);

        // Leaving it blank is refused and nothing changes.
        var blank = await PostAsync(owner.Client, "/Admin/Settings", "/Admin/Settings?handler=Hours", new());
        Assert.Contains(Text.HoursIncomplete, Dec(await blank.Content.ReadAsStringAsync()));
        Assert.Equal(14, await ctx.BusinessHours.CountAsync(h => h.BusinessId == owner.BusinessId));
    }

    [Fact]
    public async Task The_follow_ups_page_lists_a_follow_up_with_a_ready_message_and_marking_it_done_removes_it()
    {
        var (factory, _) = await HttpApp.StartAsync();
        using var _f = factory;
        var owner = await RegisterOwnerAsync(factory);

        var empty = Dec(await owner.Client.GetStringAsync("/Admin/FollowUps"));
        Assert.Contains(Text.FollowUpsEmpty, empty);

        var booking = await BookNextFreeAsync(owner);
        await using (var ctx = owner.Db.CreateDbContext())
        {
            var row = await ctx.Bookings.SingleAsync(x => x.Id == booking.Id);
            row.Status = BookingStatus.Completed;
            row.FollowUpOn = DateOnly.FromDateTime(DateTime.UtcNow.AddHours(8).AddDays(2)); // due soon (the shop is in Asia/Manila)
            row.FollowUpNote = "Check the wound";
            await ctx.SaveChangesAsync();
        }

        var page = Dec(await owner.Client.GetStringAsync("/Admin/FollowUps"));
        Assert.Contains("Check the wound", page);
        Assert.Contains("It is time for your follow-up", page);
        Assert.Contains("sms:", page);

        // Only a plain email address becomes a mailto: link: one with "?" or "&" could add recipients or text, so it is not linked.
        await using (var emailCtx = owner.Db.CreateDbContext())
        {
            var customer = await emailCtx.Customers.SingleAsync(c => c.Id == emailCtx.Bookings.Where(b => b.Id == booking.Id).Select(b => b.CustomerId).First());
            customer.Email = "eve?bcc=friend@other.com@example.com";
            await emailCtx.SaveChangesAsync();
            Assert.DoesNotContain("mailto:", Dec(await owner.Client.GetStringAsync("/Admin/FollowUps")));
            customer.Email = "juan.delacruz@example.com";
            await emailCtx.SaveChangesAsync();
            Assert.Contains("mailto:juan.delacruz@example.com?subject=", Dec(await owner.Client.GetStringAsync("/Admin/FollowUps")));
        }

        // Every way to reach the customer is on the page, and their contact card can be saved to the phone.
        Assert.Contains("Send as text", page);
        Assert.Contains(Text.SaveContact, page);
        var card = await owner.Client.GetAsync($"/Admin/FollowUps?handler=Contact&id={booking.Id}");
        Assert.Equal(HttpStatusCode.OK, card.StatusCode);
        Assert.Equal("text/vcard", card.Content.Headers.ContentType!.MediaType);
        var vcf = await card.Content.ReadAsStringAsync();
        Assert.Contains("FN:Juan Dela Cruz", vcf);
        Assert.Contains("TEL;TYPE=CELL:09170000001", vcf);
        Assert.Equal(HttpStatusCode.NotFound, (await owner.Client.GetAsync($"/Admin/FollowUps?handler=Contact&id={Guid.NewGuid()}")).StatusCode);

        // The same message tools are under a completed booking in the day view.
        var doneView = Dec(await owner.Client.GetStringAsync($"/Admin?date={DateOf(booking)}&status=done"));
        Assert.Contains(Text.AfterVisitOpen, doneView);
        Assert.Contains($"/Admin/AfterVisit/{booking.Id}", doneView);
        Assert.Contains("/Admin/History/", doneView);
        var card2 = await owner.Client.GetAsync($"/Admin?handler=Contact&id={booking.Id}");
        Assert.Equal(HttpStatusCode.OK, card2.StatusCode);

        // A follow-up due within 3 days is announced on every back-office page and in the bell.
        var bookings = Dec(await owner.Client.GetStringAsync("/Admin"));
        Assert.Contains("1 follow-up(s) due in the next 3 days", bookings);
        Assert.Contains("bell-badge end\">1<", bookings);

        var done = await PostAsync(owner.Client, "/Admin/FollowUps", "/Admin/FollowUps?handler=Done", new() { ["id"] = booking.Id.ToString() });
        Assert.Equal(HttpStatusCode.Redirect, done.StatusCode);
        Assert.Contains(Text.FollowUpsEmpty, Dec(await owner.Client.GetStringAsync("/Admin/FollowUps")));
    }

    [Fact]
    public async Task The_booking_page_does_not_offer_days_the_shop_is_closed()
    {
        var (factory, _) = await HttpApp.StartAsync();
        using var _f = factory;
        var owner = await RegisterOwnerAsync(factory);

        static List<DateOnly> Offered(string html) =>
            System.Text.RegularExpressions.Regex.Matches(html, "name=\"date\" value=\"(\\d{4}-\\d{2}-\\d{2})\"")
                .Select(m => DateOnly.ParseExact(m.Groups[1].Value, "yyyy-MM-dd")).ToList();

        // The starter hours are Monday to Saturday, so Sundays are not offered.
        var before = Offered(await owner.Client.GetStringAsync($"/b/{owner.Slug}"));
        Assert.NotEmpty(before);
        Assert.DoesNotContain(before, d => d.DayOfWeek == DayOfWeek.Sunday);
        Assert.Contains(before, d => d.DayOfWeek == DayOfWeek.Tuesday);

        // A whole-shop closure that repeats every Tuesday takes Tuesdays off the list; a lunch break does not close the day.
        await using (var ctx = owner.Db.CreateDbContext())
        {
            ctx.WeeklyClosures.Add(new WeeklyClosure { BusinessId = owner.BusinessId, DayOfWeek = (short)DayOfWeek.Tuesday });
            ctx.WeeklyClosures.Add(new WeeklyClosure { BusinessId = owner.BusinessId, DayOfWeek = (short)DayOfWeek.Wednesday, StartTime = new TimeOnly(12, 0), EndTime = new TimeOnly(13, 0) });
            await ctx.SaveChangesAsync();
        }
        var after = Offered(await owner.Client.GetStringAsync($"/b/{owner.Slug}"));
        Assert.DoesNotContain(after, d => d.DayOfWeek == DayOfWeek.Tuesday);
        Assert.Contains(after, d => d.DayOfWeek == DayOfWeek.Wednesday);
    }

    [Fact]
    public async Task The_after_visit_page_offers_the_message_tools_and_a_follow_up_right_after_completion()
    {
        var (factory, _) = await HttpApp.StartAsync();
        using var _f = factory;
        var owner = await RegisterOwnerAsync(factory);
        var booking = await BookNextFreeAsync(owner);

        // Not completed yet: nothing to do after the visit, so the page sends the staff member back to the bookings.
        var early = await owner.Client.GetAsync($"/Admin/AfterVisit/{booking.Id}");
        Assert.Equal(HttpStatusCode.Redirect, early.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await owner.Client.GetAsync($"/Admin/AfterVisit/{Guid.NewGuid()}")).StatusCode);

        await using (var ctx = owner.Db.CreateDbContext())
        {
            var row = await ctx.Bookings.SingleAsync(x => x.Id == booking.Id);
            row.Status = BookingStatus.Completed;
            await ctx.SaveChangesAsync();
        }

        // The medical record card belongs to clinics: a barbershop does not see it, and a blank follow-up date is fine (it is optional).
        var plainPage = Dec(await owner.Client.GetStringAsync($"/Admin/AfterVisit/{booking.Id}"));
        Assert.DoesNotContain(Text.VisitNotesTitle, plainPage);
        Assert.Contains(Text.FollowUpTitleOptional, plainPage);
        var blankUrl = $"/Admin/AfterVisit/{booking.Id}";
        var blank = await PostAsync(owner.Client, blankUrl, $"{blankUrl}?handler=FollowUp", new() { ["followUpOn"] = "", ["followUpNote"] = "" });
        Assert.Equal(HttpStatusCode.Redirect, blank.StatusCode);
        Assert.DoesNotContain(Text.FollowUpDateInvalid, Dec(await owner.Client.GetStringAsync(blankUrl)));
        await using (var ctxClinic = owner.Db.CreateDbContext())
            await ctxClinic.Businesses.Where(x => x.Id == owner.BusinessId).ExecuteUpdateAsync(u => u.SetProperty(x => x.Industry, "clinic"));

        var page = Dec(await owner.Client.GetStringAsync($"/Admin/AfterVisit/{booking.Id}"));
        Assert.Contains(Text.VisitNotesTitle, page);
        Assert.Contains("data-other-field", page);                                                  // "What was done" shows only for Other
        Assert.Contains("data-other=\"false\"", page);
        Assert.Contains(Text.AfterVisitSendTitle, page);
        Assert.Contains("thank you for visiting", page);
        Assert.Contains("Send as text", page);
        Assert.Contains(Text.SaveContact, page);
        Assert.Contains(Text.FollowUpTitle, page);
        Assert.Contains(Text.AfterVisitMessageHint, page); // no follow-up date yet
        Assert.DoesNotContain("Your follow-up visit is on", page);

        var url = $"/Admin/AfterVisit/{booking.Id}";
        var saved = await PostAsync(owner.Client, url, $"{url}?handler=FollowUp",
            new() { ["followUpOn"] = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(14)).ToString("yyyy-MM-dd"), ["followUpNote"] = "Check-up" });
        Assert.Equal(HttpStatusCode.Redirect, saved.StatusCode);
        Assert.Single(await new FollowUpService(owner.Db, TimeProvider.System).ListAsync(owner.BusinessId));

        // Once a follow-up date is set, the message tells the customer about it.
        var after = Dec(await owner.Client.GetStringAsync(url));
        Assert.Contains("Your follow-up is on", after);
        Assert.DoesNotContain(Text.AfterVisitMessageHint, after);

        // The optional medical record is saved on the visit, shown in the customer's history, and never put in the message.
        var recorded = await PostAsync(owner.Client, url, $"{url}?handler=Notes", new() { ["visitNotes"] = "Blood pressure 120/80. Allergic to penicillin." });
        Assert.Equal(HttpStatusCode.Redirect, recorded.StatusCode);
        var withRecord = Dec(await owner.Client.GetStringAsync(url));
        Assert.Contains("Allergic to penicillin.", withRecord);
        Assert.DoesNotContain("penicillin", withRecord[withRecord.IndexOf("Ready message", StringComparison.Ordinal)..]);
        await using var ctx2 = owner.Db.CreateDbContext();
        var customerId = (await ctx2.Bookings.SingleAsync(x => x.Id == booking.Id)).CustomerId;
        var history = Dec(await owner.Client.GetStringAsync($"/Admin/History/{customerId}"));
        Assert.Contains("Allergic to penicillin.", history);
        Assert.Equal(HttpStatusCode.NotFound, (await owner.Client.GetAsync($"/Admin/History/{Guid.NewGuid()}")).StatusCode);
    }

    [Fact]
    public async Task Staff_cannot_cancel_a_booking_without_a_reason_and_the_buttons_ask_first()
    {
        var (factory, _) = await HttpApp.StartAsync();
        using var _f = factory;
        var owner = await RegisterOwnerAsync(factory);
        var booking = await BookNextFreeAsync(owner);

        // The page asks "are you sure?" and makes the reason a required field.
        var page = Dec(await owner.Client.GetStringAsync($"/Admin?date={DateOf(booking)}"));
        Assert.Contains($"data-confirm=\"{Text.ConfirmCancelStaff}\"", page);
        Assert.Contains("name=\"reason\" maxlength=\"200\" required", page);

        // Before the scheduled time (here: a future day) the status buttons are not shown at all, only a note about when they appear.
        Assert.DoesNotContain("value=\"complete\"", page);
        Assert.DoesNotContain("value=\"noshow\"", page);
        Assert.Contains(Text.StatusOnlyToday, page);

        // A blank (or spaces-only) reason is refused on the server too, and the booking stays confirmed.
        foreach (var blank in new[] { "", "   " })
        {
            var refused = await PostAsync(owner.Client, $"/Admin?date={DateOf(booking)}", $"/Admin?handler=Transition",
                new() { ["id"] = booking.Id.ToString(), ["action"] = "cancel", ["reason"] = blank, ["date"] = DateOf(booking) });
            Assert.Equal(HttpStatusCode.Redirect, refused.StatusCode);
            Assert.Contains(Text.CancelReasonRequired, Dec(await owner.Client.GetStringAsync($"/Admin?date={DateOf(booking)}")));
            await using var ctx = owner.Db.CreateDbContext();
            Assert.Equal(BookingStatus.Confirmed, (await ctx.Bookings.SingleAsync(b => b.Id == booking.Id)).Status);
        }

        // With a reason it goes through.
        await PostAsync(owner.Client, $"/Admin?date={DateOf(booking)}", $"/Admin?handler=Transition",
            new() { ["id"] = booking.Id.ToString(), ["action"] = "cancel", ["reason"] = "Barber is sick", ["date"] = DateOf(booking) });
        await using var ctx2 = owner.Db.CreateDbContext();
        Assert.Equal(BookingStatus.Cancelled, (await ctx2.Bookings.SingleAsync(b => b.Id == booking.Id)).Status);
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
    public async Task The_bookings_page_lists_confirmed_by_default_and_has_a_switch_for_cancelled_and_done()
    {
        var (factory, _) = await HttpApp.StartAsync();
        using var _f = factory;
        var owner = await RegisterOwnerAsync(factory);
        var svc = new BookingService(owner.Db, TimeProvider.System);
        var confirmed = await BookNextFreeAsync(owner, "09170000011");
        var toCancel = await BookNextFreeAsync(owner, "09170000012");
        Assert.True((await svc.TransitionAsync(owner.BusinessId, toCancel.Id, BookingStatus.Cancelled,
            BookingActor.Staff(Guid.NewGuid(), "Owner"), "Changed plans")).IsSuccess);
        var day = DateOf(confirmed);
        Assert.Equal(day, DateOf(toCancel)); // both on the same day so the filter is what separates them

        // Default: only what is still confirmed, and the cancelled booking is not in the list.
        var byDefault = Dec(await owner.Client.GetStringAsync($"/Admin?date={day}"));
        Assert.Contains("09170000011", byDefault);
        Assert.DoesNotContain("09170000012", byDefault);
        Assert.Contains("aria-current=\"true\"", byDefault);
        Assert.Matches(@"Confirmed\s*<span class=""count"">1</span>", byDefault);
        Assert.Matches(@"Cancelled\s*<span class=""count"">1</span>", byDefault);

        // The switch: cancelled shows the cancelled one, with the reason's booking and no action buttons for it.
        var cancelled = Dec(await owner.Client.GetStringAsync($"/Admin?date={day}&status=cancelled"));
        Assert.Contains("09170000012", cancelled);
        Assert.DoesNotContain("09170000011", cancelled);

        // Done is empty until a booking is completed (it cannot be before it has started, so it stays empty here).
        var done = Dec(await owner.Client.GetStringAsync($"/Admin?date={day}&status=done"));
        Assert.Contains(Text.DayEmptyDone, done);

        // Unknown values fall back to confirmed, and the day links keep the chosen filter.
        var odd = Dec(await owner.Client.GetStringAsync($"/Admin?date={day}&status=nonsense"));
        Assert.Contains("09170000011", odd);
        Assert.Contains("status=cancelled", cancelled); // the previous and next day links carry the filter
    }

    [Fact]
    public async Task The_owner_adds_a_weekly_closure_switches_it_off_and_on_and_removes_it_on_the_one_closures_page()
    {
        var (factory, _) = await HttpApp.StartAsync();
        using var _f = factory;
        var owner = await RegisterOwnerAsync(factory);

        var add = await PostAsync(owner.Client, "/Admin/Closures", "/Admin/Closures?handler=Add",
            new() { ["Input.Repeat"] = "weekly", ["Input.Weekday"] = "0", ["Input.StaffId"] = "", ["Input.Reason"] = "Day off" });
        Assert.Equal(HttpStatusCode.Redirect, add.StatusCode);

        var page = Dec(await owner.Client.GetStringAsync("/Admin/Closures"));
        Assert.Contains("Every Sunday", page);
        Assert.Contains("Day off", page);
        Assert.Contains(Text.ClosureTurnOff, page);

        await using var ctx = owner.Db.CreateDbContext();
        var id = await ctx.WeeklyClosures.Where(c => c.BusinessId == owner.BusinessId).Select(c => c.Id).SingleAsync();

        await PostAsync(owner.Client, "/Admin/Closures", "/Admin/Closures?handler=ToggleWeekly", new() { ["id"] = id.ToString(), ["on"] = "false" });
        var off = Dec(await owner.Client.GetStringAsync("/Admin/Closures"));
        Assert.Contains(Text.ClosureTurnOn, off);
        Assert.Contains(">" + Text.ClosureWeeklyOff + "<", off);
        Assert.False(await ctx.WeeklyClosures.Where(c => c.Id == id).Select(c => c.IsActive).SingleAsync());

        await PostAsync(owner.Client, "/Admin/Closures", "/Admin/Closures?handler=ToggleWeekly", new() { ["id"] = id.ToString(), ["on"] = "true" });
        Assert.True(await ctx.WeeklyClosures.Where(c => c.Id == id).Select(c => c.IsActive).SingleAsync());

        // No weekday chosen: a clear error, nothing is created.
        var bad = await PostAsync(owner.Client, "/Admin/Closures", "/Admin/Closures?handler=Add",
            new() { ["Input.Repeat"] = "weekly", ["Input.Weekday"] = "", ["Input.StaffId"] = "" });
        Assert.Equal(HttpStatusCode.OK, bad.StatusCode);
        Assert.Contains(Text.ClosureWeekdayInvalid, Dec(await bad.Content.ReadAsStringAsync()));

        await PostAsync(owner.Client, "/Admin/Closures", "/Admin/Closures?handler=DeleteWeekly", new() { ["id"] = id.ToString() });
        Assert.Equal(0, await ctx.WeeklyClosures.CountAsync(c => c.BusinessId == owner.BusinessId));
    }

    [Fact]
    public async Task Clicking_a_card_of_the_day_at_a_glance_lists_its_bookings_with_their_details()
    {
        var (factory, _) = await HttpApp.StartAsync();
        using var _f = factory;
        var a = await RegisterOwnerAsync(factory);
        var b = await RegisterOwnerAsync(factory);
        var mine = await BookNextFreeAsync(a, "09175550111");
        await BookNextFreeAsync(b, "09175550222");
        var day = $"/Admin?date={DateOf(mine)}";

        var plain = Dec(await a.Client.GetStringAsync(day));
        Assert.DoesNotContain("id=\"kpi-details\"", plain);                                       // closed until a card is clicked
        Assert.Contains($"kpi=todo", plain);                                                      // the card is a link

        var todo = Dec(await a.Client.GetStringAsync(day + "&kpi=todo"));
        Assert.Contains("id=\"kpi-details\"", todo);
        Assert.Contains("Juan Dela Cruz", todo);
        Assert.Contains("09175550111", todo);
        Assert.DoesNotContain("09175550222", todo);                                               // another shop's booking never shows

        Assert.Contains(Text.KpiDetailsNone, Dec(await a.Client.GetStringAsync(day + "&kpi=cancelled")));
        Assert.DoesNotContain("id=\"kpi-details\"", Dec(await a.Client.GetStringAsync(day + "&kpi=<script>")));  // an unknown card opens nothing
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
