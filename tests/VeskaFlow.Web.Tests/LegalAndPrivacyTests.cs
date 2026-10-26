using System.Net;
using System.Text.Json;
using VeskaFlow.Core.Domain;
using VeskaFlow.Web.Services;
using Microsoft.EntityFrameworkCore;
using static VeskaFlow.Web.Tests.Seed;

namespace VeskaFlow.Web.Tests;

/// <summary>The Terms and Privacy Notice, agreeing to them at sign-up, and the shop's tools for a customer's right to see and erase their data.</summary>
[Collection("http")]
public class LegalAndPrivacyTests
{
    private static string Id(string prefix) => prefix + Guid.NewGuid().ToString("N")[..10];

    private static Dictionary<string, string> Form(string email, string slug, bool accept) => new()
    {
        ["Input.BusinessName"] = "Legal Clinic", ["Input.Slug"] = slug, ["Input.OwnerName"] = "Dr Legal", ["Input.Email"] = email,
        ["Input.Password"] = "correct-horse-1", ["Input.Industry"] = "clinic", ["Input.Accept"] = accept ? "true" : "false",
    };

    [Fact]
    public async Task The_privacy_notice_and_terms_are_public_and_linked_from_every_page()
    {
        var (factory, client) = await HttpApp.StartAsync();
        using var _f = factory;

        var privacy = WebUtility.HtmlDecode(await client.GetStringAsync("/Privacy"));
        Assert.Contains("personal information controller", privacy);
        Assert.Contains("personal information processor", privacy);
        Assert.Contains("National Privacy Commission", privacy);
        Assert.Contains("Render", privacy);
        Assert.Contains("Neon", privacy);
        Assert.Contains("Brevo", privacy);
        Assert.Contains($"Version {LegalInfo.Version}", privacy);

        var terms = WebUtility.HtmlDecode(await client.GetStringAsync("/Terms"));
        Assert.Contains("data processing", terms);
        Assert.Contains("laws of the Philippines", terms);
        Assert.Contains("/Privacy", terms);

        var home = await client.GetStringAsync("/");
        Assert.Contains("href=\"/Terms\"", home);
        Assert.Contains("href=\"/Privacy\"", home);
    }

    [Fact]
    public async Task A_new_shop_must_agree_to_the_terms_and_the_agreement_is_kept_with_the_shop()
    {
        var (factory, client) = await HttpApp.StartAsync();
        using var _f = factory;
        var slug = Id("lg");
        var email = $"{Id("o")}@example.com";

        var refused = await HttpApp.PostFormAsync(client, "/Register", Form(email, slug, accept: false), tokenPage: "/Register");
        Assert.Equal(HttpStatusCode.OK, refused.StatusCode);
        Assert.Contains(Text.AcceptTermsRequired, WebUtility.HtmlDecode(await refused.Content.ReadAsStringAsync()));
        await using var ctx = new TestDbFactory(await TestDb.ConnectionStringAsync()).CreateDbContext();
        Assert.False(await ctx.Businesses.AnyAsync(b => b.Slug == slug));

        var form = WebUtility.HtmlDecode(await client.GetStringAsync("/Register"));
        Assert.Contains("name=\"Input.Accept\"", form);

        var accepted = await HttpApp.PostFormAsync(client, "/Register", Form(email, slug, accept: true), tokenPage: "/Register");
        Assert.Equal(HttpStatusCode.Redirect, accepted.StatusCode);
        var shop = await ctx.Businesses.AsNoTracking().SingleAsync(b => b.Slug == slug);
        Assert.Equal(LegalInfo.Version, shop.TermsVersion);
        Assert.InRange((DateTime.UtcNow - shop.TermsAcceptedAtUtc!.Value).TotalMinutes, -1, 5);
    }

    // ------------------------------------------------------------ a customer's right to see and erase their data

    private sealed record Setup(TestDbFactory Db, TestClock Clock, BookingService Bookings, CustomerDataService Data, RegisteredOwner Owner, Guid ServiceId, Guid StaffId);

    private static async Task<Setup> SetupAsync()
    {
        var db = new TestDbFactory(await TestDb.ConnectionStringAsync());
        var clock = new TestClock(BaseNow);
        var owner = (await new OnboardingService(db, clock).RegisterAsync(
            new RegisterRequest("Clinic", Id("pv"), "Dr A", $"{Id("o")}@example.com", "correct-horse-1", Industry: "clinic", TrialDays: 30))).Value!;
        await using var ctx = db.CreateDbContext();
        var service = await ctx.Services.Where(s => s.BusinessId == owner.BusinessId && !s.IsOther).Select(s => s.Id).SingleAsync();
        var staff = await ctx.StaffMembers.Where(s => s.BusinessId == owner.BusinessId).Select(s => s.Id).SingleAsync();
        return new Setup(db, clock, new BookingService(db, clock), new CustomerDataService(db), owner, service, staff);
    }

    private static async Task<(Guid BookingId, Guid CustomerId)> BookCompletedAsync(Setup s, string name, string phone, int hour)
    {
        var created = await s.Bookings.CreateAsync(new CreateBookingRequest(s.Owner.BusinessId, s.ServiceId, s.StaffId, Manila(5, hour), name,
            phone, "x@example.com", Id("k"), BookingActor.Customer(name)));
        Assert.True(created.IsSuccess, created.Message + " " + string.Join(", ", created.FieldErrors.Select(e => e.Key + "=" + e.Value)));
        var booking = created.Value!.Booking;
        s.Clock.Now = new DateTimeOffset(Manila(5, hour, 10), TimeSpan.Zero);
        Assert.True((await s.Bookings.TransitionAsync(s.Owner.BusinessId, booking.Id, BookingStatus.Completed, BookingActor.Staff(s.Owner.AdminUserId, "Dr A"))).IsSuccess);
        await new VisitRecordService(s.Db).SetNotesAsync(s.Owner.BusinessId, booking.Id, "Allergic to penicillin");
        await using var ctx = s.Db.CreateDbContext();
        return (booking.Id, (await ctx.Bookings.SingleAsync(b => b.Id == booking.Id)).CustomerId);
    }

    [Fact]
    public async Task Export_gives_everything_kept_about_one_customer_and_nobody_elses()
    {
        var s = await SetupAsync();
        var maria = await BookCompletedAsync(s, "Maria Santos", "09175550201", 10);
        await BookCompletedAsync(s, "Pedro Cruz", "09175550202", 15);

        var json = await s.Data.ExportJsonAsync(s.Owner.BusinessId, maria.CustomerId);
        Assert.NotNull(json);
        using var doc = JsonDocument.Parse(json!);
        Assert.Equal("Maria Santos", doc.RootElement.GetProperty("customer").GetProperty("Name").GetString());
        Assert.Equal("09175550201", doc.RootElement.GetProperty("customer").GetProperty("Phone").GetString());
        var booking = doc.RootElement.GetProperty("bookings").EnumerateArray().Single();
        Assert.Equal("Allergic to penicillin", booking.GetProperty("VisitNotes").GetString());
        Assert.True(doc.RootElement.GetProperty("auditTrail").GetArrayLength() >= 1);
        Assert.DoesNotContain("Pedro", json);

        var other = await SetupAsync();
        Assert.Null(await other.Data.ExportJsonAsync(other.Owner.BusinessId, maria.CustomerId)); // another shop sees nothing
        Assert.Null(await s.Data.ExportJsonAsync(s.Owner.BusinessId, Guid.NewGuid()));
    }

    [Fact]
    public async Task Delete_removes_the_customer_and_everything_tied_to_them_only_with_the_typed_name()
    {
        var s = await SetupAsync();
        var maria = await BookCompletedAsync(s, "Maria Santos", "09175550203", 10);
        var pedro = await BookCompletedAsync(s, "Pedro Cruz", "09175550204", 15);
        var other = await SetupAsync();

        // Refused: wrong or empty name, another shop's customer. Nothing is removed.
        Assert.Contains("confirm", (await s.Data.DeleteAsync(s.Owner.BusinessId, maria.CustomerId, "Somebody Else")).FieldErrors.Keys);
        Assert.Contains("confirm", (await s.Data.DeleteAsync(s.Owner.BusinessId, maria.CustomerId, "")).FieldErrors.Keys);
        Assert.Equal(FailureKind.NotFound, (await other.Data.DeleteAsync(other.Owner.BusinessId, maria.CustomerId, "Maria Santos")).Failure);
        await using (var ctx = s.Db.CreateDbContext())
            Assert.True(await ctx.Customers.AnyAsync(c => c.Id == maria.CustomerId));

        // Allowed: the typed name (any letter case).
        Assert.True((await s.Data.DeleteAsync(s.Owner.BusinessId, maria.CustomerId, " maria santos ")).IsSuccess);

        await using var after = s.Db.CreateDbContext();
        Assert.False(await after.Customers.AnyAsync(c => c.Id == maria.CustomerId));
        Assert.False(await after.Bookings.AnyAsync(b => b.Id == maria.BookingId));
        Assert.False(await after.AuditLogs.AnyAsync(a => a.BookingId == maria.BookingId));
        Assert.True(await after.Customers.AnyAsync(c => c.Id == pedro.CustomerId)); // the other customer is untouched
        Assert.True(await after.Bookings.AnyAsync(b => b.Id == pedro.BookingId));
        Assert.True(await after.AuditLogs.AnyAsync(a => a.BookingId == pedro.BookingId));
    }

    [Fact]
    public async Task The_history_page_offers_export_and_a_delete_that_asks_for_the_name()
    {
        var (factory, _) = await HttpApp.StartAsync();
        using var _f = factory;
        var client = HttpApp.NewClient(factory);
        var slug = Id("hp");
        await HttpApp.PostFormAsync(client, "/Register", Form($"{Id("o")}@example.com", slug, accept: true), tokenPage: "/Register");

        await using var ctx = new TestDbFactory(await TestDb.ConnectionStringAsync()).CreateDbContext();
        var shopId = await ctx.Businesses.Where(b => b.Slug == slug).Select(b => b.Id).SingleAsync();
        var customer = new VeskaFlow.Web.Data.Customer { BusinessId = shopId, Name = "Eve Reyes", Phone = "09175550205" };
        ctx.Customers.Add(customer);
        await ctx.SaveChangesAsync();

        var page = WebUtility.HtmlDecode(await client.GetStringAsync($"/Admin/History/{customer.Id}"));
        Assert.Contains(Text.CustomerDataExport, page);
        Assert.Contains($"data-confirm=\"{Text.CustomerDataDeleteAsk}\"", page);

        var export = await client.GetAsync($"/Admin/History/{customer.Id}?handler=Export");
        Assert.Equal(HttpStatusCode.OK, export.StatusCode);
        Assert.Equal("application/json", export.Content.Headers.ContentType!.MediaType);
        Assert.Contains("Eve Reyes", await export.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/Admin/History/{Guid.NewGuid()}?handler=Export")).StatusCode);

        var wrong = await HttpApp.PostFormAsync(client, $"/Admin/History/{customer.Id}?handler=Delete", new() { ["confirm"] = "nope" }, tokenPage: $"/Admin/History/{customer.Id}");
        Assert.Equal(HttpStatusCode.Redirect, wrong.StatusCode);
        Assert.True(await ctx.Customers.AsNoTracking().AnyAsync(c => c.Id == customer.Id));

        var right = await HttpApp.PostFormAsync(client, $"/Admin/History/{customer.Id}?handler=Delete", new() { ["confirm"] = "Eve Reyes" }, tokenPage: $"/Admin/History/{customer.Id}");
        Assert.Equal(HttpStatusCode.Redirect, right.StatusCode);
        Assert.False(await ctx.Customers.AsNoTracking().AnyAsync(c => c.Id == customer.Id));
    }
}
