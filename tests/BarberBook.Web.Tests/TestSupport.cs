using BarberBook.Core.Domain;
using BarberBook.Web.Data;
using BarberBook.Web.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace BarberBook.Web.Tests;

/// <summary>Controllable clock so lead time, cutoff and grace period can be tested exactly.</summary>
public sealed class TestClock(DateTimeOffset now) : TimeProvider
{
    public DateTimeOffset Now { get; set; } = now;
    public override DateTimeOffset GetUtcNow() => Now;
}

public sealed class TestDbFactory(string connectionString) : IDbContextFactory<AppDbContext>
{
    public AppDbContext CreateDbContext() =>
        new(new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(connectionString).Options);
}

/// <summary>
/// Real PostgreSQL, never a mock. Set ConnectionStrings__Test to a DISPOSABLE database whose name ends in "_test";
/// the public schema is wiped once per test run and the migrations re-applied.
/// </summary>
public static class TestDb
{
    private static readonly Lazy<Task<string>> Init = new(InitAsync);

    public static Task<string> ConnectionStringAsync() => Init.Value;

    private static async Task<string> InitAsync()
    {
        var cs = Environment.GetEnvironmentVariable("ConnectionStrings__Test");
        if (string.IsNullOrWhiteSpace(cs))
            throw new InvalidOperationException("Set ConnectionStrings__Test to a disposable PostgreSQL database (name ending in _test).");
        var name = new NpgsqlConnectionStringBuilder(cs).Database ?? "";
        if (!name.EndsWith("_test", StringComparison.Ordinal))
            throw new InvalidOperationException($"Refusing to wipe database '{name}': its name must end with _test.");

        await using (var conn = new NpgsqlConnection(cs))
        {
            await conn.OpenAsync();
            await using var cmd = new NpgsqlCommand("DROP SCHEMA public CASCADE; CREATE SCHEMA public;", conn);
            await cmd.ExecuteNonQueryAsync();
        }
        await SqlMigrator.ApplyAsync(cs, NullLogger.Instance);
        return cs;
    }
}

public sealed record Shop(Guid BusinessId, Guid ServiceId, Guid[] StaffIds, string ConnectionString);

public static class Seed
{
    /// <summary>Monday 08:00 in Manila. Shops are open 09:00-18:00 every day (Asia/Manila, UTC+8, no DST).</summary>
    public static readonly DateTimeOffset BaseNow = new(2030, 3, 4, 0, 0, 0, TimeSpan.Zero);

    /// <summary>Local Manila wall-clock time on 2030-03-<paramref name="day"/> as a UTC instant.</summary>
    public static DateTime Manila(int day, int hour, int minute = 0) =>
        new DateTime(2030, 3, day, hour, minute, 0, DateTimeKind.Utc).AddHours(-8);

    public static async Task<Shop> CreateShopAsync(int staffCount = 1, int bufferMinutes = 0, int cutoffHours = 2)
    {
        var cs = await TestDb.ConnectionStringAsync();
        await using var db = new TestDbFactory(cs).CreateDbContext();

        var business = new Business { Slug = "t" + Guid.NewGuid().ToString("N")[..12], Name = "Test Shop", CancelCutoffHours = cutoffHours };
        var service = new Service { BusinessId = business.Id, Name = "Haircut", DurationMinutes = 30, BufferMinutes = bufferMinutes, Price = 150 };
        db.AddRange(business, service);

        var staffIds = new List<Guid>();
        for (var i = 0; i < staffCount; i++)
        {
            var staff = new Staff { BusinessId = business.Id, Name = $"Staff {i + 1}", SortOrder = i };
            staff.StaffServices.Add(new StaffService { ServiceId = service.Id, BusinessId = business.Id });
            for (short d = 0; d < 7; d++)
                staff.WorkingHours.Add(new WorkingHour { DayOfWeek = d, OpenTime = new TimeOnly(9, 0), CloseTime = new TimeOnly(18, 0) });
            db.Add(staff);
            staffIds.Add(staff.Id);
        }
        await db.SaveChangesAsync();
        return new Shop(business.Id, service.Id, staffIds.ToArray(), cs);
    }

    public static BookingService ServiceFor(Shop shop, TestClock clock) => new(new TestDbFactory(shop.ConnectionString), clock);

    public static CreateBookingRequest Request(
        Shop shop, DateTime startUtc, string? phone = null, string? key = null, Guid? staffId = null, BookingActor? actor = null) =>
        new(shop.BusinessId, shop.ServiceId, staffId, startUtc, "Juan Dela Cruz",
            phone ?? "0917" + Random.Shared.Next(1_000_000, 9_999_999), null,
            key ?? Guid.NewGuid().ToString("N"), actor ?? BookingActor.Customer("Juan Dela Cruz"));
}
