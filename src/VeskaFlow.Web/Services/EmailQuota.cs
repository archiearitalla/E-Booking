using VeskaFlow.Web.Data;
using Microsoft.EntityFrameworkCore;

namespace VeskaFlow.Web.Services;

/// <summary>Why an email to a customer may not go out right now.</summary>
public enum EmailRoom
{
    Ok,
    /// <summary>A shop without the add-on used its share for today.</summary>
    ShopLimit,
    /// <summary>The day's emails are used up for customers (the rest is kept for the system's own emails).</summary>
    PoolLimit,
}

/// <summary>
/// Keeps the emails sent per day under the email service's daily limit. Everything sent is counted (the system's own emails
/// to shop owners under the all-zero id, a shop's customer emails under the shop). Customer emails stop first, so sign-up,
/// password reset and access reminders still go out: the day's limit minus an owner reserve is the most customers can get,
/// and a shop without the paid add-on also stops at its own share and at the free pool (the limit minus the owner reserve
/// and a reserve for shops with the add-on). The day is the UTC day. All numbers come from settings.
/// </summary>
public sealed class EmailQuota(IDbContextFactory<AppDbContext> dbFactory, TimeProvider time, IConfiguration config)
{
    public static readonly Guid SystemId = Guid.Empty;

    /// <summary>Emails the email service allows per day (Brevo free: 300). Raise it when the plan is upgraded.</summary>
    public int DailyLimit => Math.Max(0, config.GetValue("Email:DailyLimit", 300));

    /// <summary>Kept for the system's own emails to owners.</summary>
    public int OwnerReserve => Math.Max(0, config.GetValue("Email:OwnerReserve", 40));

    /// <summary>Kept for shops with the paid add-on, so shops without it can never use up the day.</summary>
    public int PaidReserve => Math.Max(0, config.GetValue("Email:PaidReserve", 100));

    /// <summary>The most customer emails one shop without the add-on gets per day.</summary>
    public int FreeShopMax => Math.Max(0, config.GetValue("Email:FreeShopDailyMax", 30));

    public int CustomerCap => Math.Max(0, DailyLimit - OwnerReserve);
    public int FreePoolCap => Math.Max(0, CustomerCap - PaidReserve);

    private DateOnly Today => DateOnly.FromDateTime(time.GetUtcNow().UtcDateTime);

    /// <summary>Counts one email that was accepted by the email service.</summary>
    public async Task CountAsync(Guid businessId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var day = Today;
        await db.Database.ExecuteSqlAsync($"""
            INSERT INTO email_daily (day, business_id, sent) VALUES ({day}, {businessId}, 1)
            ON CONFLICT (day, business_id) DO UPDATE SET sent = email_daily.sent + 1
            """, ct);
    }

    /// <summary>How many emails went out today, all together.</summary>
    public async Task<int> TodayTotalAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var day = Today;
        return await db.EmailDailies.AsNoTracking().Where(e => e.Day == day).SumAsync(e => (int?)e.Sent, ct) ?? 0;
    }

    /// <summary>Whether an email to one of this shop's customers may go out now.</summary>
    public async Task<EmailRoom> CheckCustomerAsync(Guid businessId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var day = Today;
        var now = time.GetUtcNow().UtcDateTime;
        var counts = await db.EmailDailies.AsNoTracking().Where(e => e.Day == day).Select(e => new { e.BusinessId, e.Sent }).ToListAsync(ct);
        var total = counts.Sum(c => c.Sent);
        var shopSent = counts.Where(c => c.BusinessId == businessId).Sum(c => c.Sent);
        var addon = await db.Businesses.AsNoTracking().Where(b => b.Id == businessId)
            .Select(b => b.EmailAddonUntilUtc).FirstOrDefaultAsync(ct);

        if (addon is { } until && DateTime.SpecifyKind(until, DateTimeKind.Utc) > now)
            return total < CustomerCap ? EmailRoom.Ok : EmailRoom.PoolLimit;

        if (shopSent >= FreeShopMax) return EmailRoom.ShopLimit;
        return total < FreePoolCap ? EmailRoom.Ok : EmailRoom.PoolLimit;
    }
}

/// <summary>Wraps the real email service so every email it accepts is counted (these are the system's own emails to shop owners).</summary>
public sealed class CountedEmailSender(IEmailSender inner, EmailQuota quota) : IEmailSender
{
    public bool IsConfigured => inner.IsConfigured;

    public async Task<bool> SendAsync(string to, string subject, string textBody, string? htmlBody = null, CancellationToken ct = default)
    {
        var ok = await inner.SendAsync(to, subject, textBody, htmlBody, ct);
        if (ok) await quota.CountAsync(EmailQuota.SystemId, ct);
        return ok;
    }
}
