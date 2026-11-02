using Microsoft.EntityFrameworkCore;
using VeskaFlow.Core.Scheduling;
using VeskaFlow.Web.Data;

namespace VeskaFlow.Web.Services;

public sealed record JoinWaitlistRequest(Guid ServiceId, Guid? StaffId, DateOnly Day, string? Name, string? Phone, string? Email);

/// <summary>One person waiting, for the back office: who, for what, and which times are free right now (so staff can message them).</summary>
public sealed record WaitlistRow(
    Guid Id, string CustomerName, string CustomerPhone, string? CustomerEmail, DateOnly Day, Guid ServiceId, string ServiceName, Guid? StaffId, string? StaffName,
    DateTime CreatedAtUtc, DateTime? NotifiedAtUtc, IReadOnlyList<DateTime> FreeNow);

public sealed record WaitlistView(string ShopName, string Slug, string TimeZone, IReadOnlyList<WaitlistRow> Rows);

/// <summary>
/// The waitlist (BUSINESS_RULES 7.3): when a day is full a customer leaves a name and contact; the shop sees who is waiting and which times are free now,
/// and a customer who gave an email gets one automatic email when a time opens (within the daily email limit). A person can only join while the day has
/// no free time, so the list can not be used to dodge the booking rules. The shop always comes from the public page's slug or the signed-in claims.
/// </summary>
public sealed class WaitlistService(IDbContextFactory<AppDbContext> dbFactory, TimeProvider time, BookingService booking, CustomerMailer mailer, IConfiguration config)
{
    /// <summary>How many days ahead entries may be for one phone number in one shop.</summary>
    public const int MaxPerPhone = 3;

    /// <summary>The most entries one shop can hold at a time, so a flood can not fill the database.</summary>
    public const int MaxPerShop = 300;

    private DateTime Now => time.GetUtcNow().UtcDateTime;

    public async Task<ServiceResult<bool>> JoinAsync(PublicShop shop, JoinWaitlistRequest r, CancellationToken ct = default)
    {
        var errors = BookingService.ValidateContact(r.Name, r.Phone, r.Email, out var name, out var phone, out var email);
        if (errors.Count > 0) return ServiceResult<bool>.Invalid(errors);

        if (!shop.AcceptingBookings) return ServiceResult<bool>.Fail(FailureKind.NotFound, Text.NotFound);
        var service = shop.FindService(r.ServiceId);
        if (service is null || service.IsOther) return ServiceResult<bool>.Fail(FailureKind.NotFound, Text.NotFound); // "Other" needs a note: use the booking form
        PublicStaff? staff = null;
        if (r.StaffId is not null)
        {
            staff = shop.FindStaff(r.StaffId);
            if (staff is null || !staff.ServiceIds.Contains(service.Id)) return ServiceResult<bool>.Fail(FailureKind.NotFound, Text.NotFound);
        }
        if (r.Day < shop.Today || r.Day > shop.Today.AddDays(shop.HorizonDays))
            return ServiceResult<bool>.Fail(FailureKind.PolicyViolation, Text.WaitlistDayInvalid);

        // Only a day with no free time can be waited for: otherwise the customer should simply book.
        var free = await booking.GetFreeStartsAsync(shop.Id, service.Id, staff?.Id, r.Day, walkIn: false, ct);
        if (!free.IsSuccess) return ServiceResult<bool>.Fail(FailureKind.NotFound, Text.NotFound);
        if (free.Value!.Count > 0) return ServiceResult<bool>.Fail(FailureKind.PolicyViolation, Text.WaitlistSlotsFree);

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        await db.WaitlistEntries.Where(e => e.BusinessId == shop.Id && e.Day < shop.Today.AddDays(-1)).ExecuteDeleteAsync(ct);

        // The same person asking twice for the same thing is one entry.
        Guid? staffId = staff?.Id;
        var again = await db.WaitlistEntries.AnyAsync(e => e.BusinessId == shop.Id && e.CustomerPhone == phone && e.ServiceId == service.Id && e.Day == r.Day && e.StaffId == staffId, ct);
        if (again) return ServiceResult<bool>.Ok(true);

        if (await db.WaitlistEntries.CountAsync(e => e.BusinessId == shop.Id && e.CustomerPhone == phone && e.Day >= shop.Today, ct) >= MaxPerPhone)
            return ServiceResult<bool>.Fail(FailureKind.PolicyViolation, Text.WaitlistTooMany);
        if (await db.WaitlistEntries.CountAsync(e => e.BusinessId == shop.Id, ct) >= MaxPerShop)
            return ServiceResult<bool>.Fail(FailureKind.PolicyViolation, Text.WaitlistShopFull);

        db.WaitlistEntries.Add(new WaitlistEntry
        {
            BusinessId = shop.Id, ServiceId = service.Id, StaffId = staff?.Id, Day = r.Day,
            CustomerName = name, CustomerPhone = phone, CustomerEmail = email, CreatedAtUtc = Now,
        });
        await db.SaveChangesAsync(ct);
        return ServiceResult<bool>.Ok(true);
    }

    /// <summary>
    /// Who is waiting from today on, in date order. A staff login tied to one staff member sees the entries that name that person and the ones for "any staff".
    /// Each row carries the times that are free right now, worked out once per day, service and staff.
    /// </summary>
    public async Task<WaitlistView?> ListAsync(Guid businessId, Guid? scopeStaffId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var shop = await db.Businesses.AsNoTracking().Where(b => b.Id == businessId).Select(b => new { b.Name, b.Slug, b.TimeZone }).FirstOrDefaultAsync(ct);
        if (shop is null) return null;
        var today = LocalTime.LocalDateOf(Now, shop.TimeZone);
        await db.WaitlistEntries.Where(e => e.BusinessId == businessId && e.Day < today.AddDays(-1)).ExecuteDeleteAsync(ct);

        var entries = await (
            from e in db.WaitlistEntries.AsNoTracking()
            where e.BusinessId == businessId && e.Day >= today && (scopeStaffId == null || e.StaffId == scopeStaffId || e.StaffId == null)
            join s in db.Services.AsNoTracking() on e.ServiceId equals s.Id
            join st in db.StaffMembers.AsNoTracking() on e.StaffId equals st.Id into staffJoin
            from st in staffJoin.DefaultIfEmpty()
            orderby e.Day, e.CreatedAtUtc
            select new { e.Id, e.CustomerName, e.CustomerPhone, e.CustomerEmail, e.Day, e.ServiceId, ServiceName = s.Name, e.StaffId, StaffName = st == null ? null : st.Name, e.CreatedAtUtc, e.NotifiedAtUtc }
        ).Take(MaxPerShop).ToListAsync(ct);

        var cache = new Dictionary<(DateOnly, Guid, Guid?), IReadOnlyList<DateTime>>();
        var rows = new List<WaitlistRow>();
        foreach (var e in entries)
        {
            var key = (e.Day, e.ServiceId, e.StaffId);
            if (!cache.TryGetValue(key, out var times))
            {
                var free = await booking.GetFreeStartsAsync(businessId, e.ServiceId, e.StaffId, e.Day, walkIn: false, ct);
                times = free.IsSuccess ? free.Value! : [];
                cache[key] = times;
            }
            rows.Add(new WaitlistRow(e.Id, e.CustomerName, e.CustomerPhone, e.CustomerEmail, e.Day, e.ServiceId, e.ServiceName, e.StaffId, e.StaffName, e.CreatedAtUtc, e.NotifiedAtUtc, times));
        }
        return new WaitlistView(shop.Name, shop.Slug, shop.TimeZone, rows);
    }

    /// <summary>Removes one entry of this shop. An unknown id, another shop's entry, or one a limited staff login may not see is "not found".</summary>
    public async Task<ServiceResult<bool>> RemoveAsync(Guid businessId, Guid id, Guid? scopeStaffId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var removed = await db.WaitlistEntries
            .Where(e => e.Id == id && e.BusinessId == businessId && (scopeStaffId == null || e.StaffId == scopeStaffId || e.StaffId == null))
            .ExecuteDeleteAsync(ct);
        return removed == 0 ? ServiceResult<bool>.Fail(FailureKind.NotFound, Text.NotFound) : ServiceResult<bool>.Ok(true);
    }

    /// <summary>The name, number and email of one waiting customer, to save in the phone's contacts. Scoped like the list; otherwise "not found".</summary>
    public async Task<(string Name, string Phone, string? Email)?> GetContactAsync(Guid businessId, Guid id, Guid? scopeStaffId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var row = await db.WaitlistEntries.AsNoTracking()
            .Where(e => e.Id == id && e.BusinessId == businessId && (scopeStaffId == null || e.StaffId == scopeStaffId || e.StaffId == null))
            .Select(e => new { e.CustomerName, e.CustomerPhone, e.CustomerEmail }).FirstOrDefaultAsync(ct);
        return row is null ? null : (row.CustomerName, row.CustomerPhone, row.CustomerEmail);
    }

    /// <summary>
    /// Emails each waiting customer once when their day has a free time. Only for a customer who gave an email, in a shop with the customer email switch on
    /// and room in the day's limit; the run stops when the whole day's customer emails are used up. Returns how many were sent.
    /// </summary>
    public async Task<int> NotifyFreedAsync(CancellationToken ct = default)
    {
        var baseUrl = config["App:PublicBaseUrl"] is { Length: > 0 } configured ? configured.Trim().TrimEnd('/') : "";
        if (baseUrl.Length == 0) return 0; // no address for the booking link

        var now = Now;
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var utcToday = DateOnly.FromDateTime(now);
        await db.WaitlistEntries.Where(e => e.Day < utcToday.AddDays(-2)).ExecuteDeleteAsync(ct);

        var waiting = await (
            from e in db.WaitlistEntries.AsNoTracking()
            where e.NotifiedAtUtc == null && e.CustomerEmail != null && e.Day >= utcToday.AddDays(-1)
            join b in db.Businesses.AsNoTracking() on e.BusinessId equals b.Id
            where b.IsEnabled && b.CustomerEmails
            orderby e.CreatedAtUtc
            select new { e.Id, e.BusinessId, e.ServiceId, e.StaffId, e.Day, b.TimeZone }
        ).Take(200).ToListAsync(ct);

        var sent = 0;
        var cache = new Dictionary<(Guid, DateOnly, Guid, Guid?), IReadOnlyList<DateTime>>();
        foreach (var w in waiting)
        {
            if (w.Day < LocalTime.LocalDateOf(now, w.TimeZone)) continue; // the day has passed
            var key = (w.BusinessId, w.Day, w.ServiceId, w.StaffId);
            if (!cache.TryGetValue(key, out var times))
            {
                var free = await booking.GetFreeStartsAsync(w.BusinessId, w.ServiceId, w.StaffId, w.Day, walkIn: false, ct);
                times = free.IsSuccess ? free.Value! : [];
                cache[key] = times;
            }
            if (times.Count == 0) continue;

            var result = await mailer.SendWaitlistAsync(w.BusinessId, w.Id, baseUrl, times, ct);
            if (result == MailResult.PoolLimit) break; // the day's customer emails are used up
            if (result != MailResult.Sent) continue;
            await db.WaitlistEntries.Where(e => e.Id == w.Id).ExecuteUpdateAsync(s => s.SetProperty(e => e.NotifiedAtUtc, now), ct);
            sent++;
        }
        return sent;
    }
}

/// <summary>Looks for waiting customers whose day has a free time, a couple of minutes after start and then every 5 minutes.</summary>
public sealed class WaitlistWorker(IServiceScopeFactory scopes, IConfiguration config, ILogger<WaitlistWorker> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!config.GetValue("Notices:Enabled", true)) return;
        try
        {
            await Task.Delay(TimeSpan.FromMinutes(2), stoppingToken);
            using var timer = new PeriodicTimer(TimeSpan.FromMinutes(5));
            do
            {
                try
                {
                    await using var scope = scopes.CreateAsyncScope();
                    var sent = await scope.ServiceProvider.GetRequiredService<WaitlistService>().NotifyFreedAsync(stoppingToken);
                    if (sent > 0) log.LogInformation("Told {Count} waiting customers that a time opened up.", sent);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    log.LogWarning("The waitlist emails could not run: {Reason}", ex.GetType().Name);
                }
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException) { }
    }
}
