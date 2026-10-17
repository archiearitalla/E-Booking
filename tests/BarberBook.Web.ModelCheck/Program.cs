// Verifies the EF model matches the SQL schema: applies the migrations to the database named by
// ConnectionStrings__Default (run twice: the second run must apply nothing), then touches every mapped
// table so a wrong table or column name fails. Use a disposable database.
using BarberBook.Web.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

var cs = Environment.GetEnvironmentVariable("ConnectionStrings__Default");
if (string.IsNullOrWhiteSpace(cs)) { Console.WriteLine("Set ConnectionStrings__Default."); return 2; }

using var loggers = LoggerFactory.Create(b => b.AddSimpleConsole());
var logger = loggers.CreateLogger("ModelCheck");

await SqlMigrator.ApplyAsync(cs, logger);
await SqlMigrator.ApplyAsync(cs, logger); // idempotent: must log nothing the second time

var options = new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(cs).Options;
await using var db = new AppDbContext(options);

var failures = 0;
async Task Check(string name, Func<Task> query)
{
    try { await query(); Console.WriteLine($"  ok   {name}"); }
    catch (Exception e) { failures++; Console.WriteLine($"  FAIL {name}: {e.GetBaseException().Message}"); }
}

await Check("business", () => db.Businesses.Take(1).ToListAsync());
await Check("admin_user", () => db.AdminUsers.Take(1).ToListAsync());
await Check("staff", () => db.StaffMembers.Take(1).ToListAsync());
await Check("service", () => db.Services.Take(1).ToListAsync());
await Check("staff_service", () => db.StaffServices.Take(1).ToListAsync());
await Check("working_hour", () => db.WorkingHours.Take(1).ToListAsync());
await Check("blackout", () => db.Blackouts.Take(1).ToListAsync());
await Check("customer", () => db.Customers.Take(1).ToListAsync());
await Check("booking (incl. xmin)", () => db.Bookings.Take(1).ToListAsync());
await Check("booking with Staff/Service/Customer", () => db.Bookings.Include(b => b.Staff).Include(b => b.Service).Include(b => b.Customer).Take(1).ToListAsync());
await Check("audit_log", () => db.AuditLogs.Take(1).ToListAsync());

// Round trip: write every entity the way the services will, read back, and let the DB constraints judge the mapping.
await Check("insert and read back a booking through EF", async () =>
{
    var biz = new Business { Slug = "modelcheck", Name = "Model Check" };
    var staff = new Staff { BusinessId = biz.Id, Name = "Ben" };
    var service = new Service { BusinessId = biz.Id, Name = "Haircut", DurationMinutes = 30, Price = 150 };
    var customer = new Customer { BusinessId = biz.Id, Name = "Juan", Phone = "0917" };
    var start = new DateTime(2030, 1, 1, 2, 0, 0, DateTimeKind.Utc);
    var booking = new Booking
    {
        BusinessId = biz.Id, CustomerId = customer.Id, StaffId = staff.Id, ServiceId = service.Id,
        StartsAtUtc = start, EndsAtUtc = start.AddMinutes(30), OccupiedUntilUtc = start.AddMinutes(30),
        LocationTimeZone = "Asia/Manila", Status = BarberBook.Core.Domain.BookingStatus.Confirmed,
        PriceSnapshot = 150, ServiceNameSnapshot = "Haircut", ManageToken = Guid.NewGuid().ToString("N"),
    };
    db.AddRange(biz, staff, service, customer, booking);
    await db.SaveChangesAsync();
    db.ChangeTracker.Clear();
    var back = await db.Bookings.SingleAsync(b => b.Id == booking.Id);
    if (back.Status != BarberBook.Core.Domain.BookingStatus.Confirmed) throw new Exception("status did not round-trip");
    // clean up our rows (this check owns them)
    await db.Database.ExecuteSqlRawAsync("DELETE FROM booking WHERE id = {0}", booking.Id);
    await db.Database.ExecuteSqlRawAsync("DELETE FROM customer WHERE id = {0}", customer.Id);
    await db.Database.ExecuteSqlRawAsync("DELETE FROM service WHERE id = {0}", service.Id);
    await db.Database.ExecuteSqlRawAsync("DELETE FROM staff WHERE id = {0}", staff.Id);
    await db.Database.ExecuteSqlRawAsync("DELETE FROM business WHERE id = {0}", biz.Id);
});

Console.WriteLine(failures == 0 ? "\nModel matches schema." : $"\n{failures} MODEL CHECK(S) FAILED.");
return failures == 0 ? 0 : 1;
