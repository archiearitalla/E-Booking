using VeskaFlow.Web.Data;
using Microsoft.EntityFrameworkCore;

namespace VeskaFlow.Web.Services;

public sealed record PromoRecipient(Guid Id, string Name, string Phone, string? Email, DateTime? SentAtUtc);

/// <summary>The promo, and the customers who said yes to promo messages, for the page where staff send it.</summary>
public sealed record PromoSendView(PromoBannerView? Promo, int Eligible, int Sent, int Matching, IReadOnlyList<PromoRecipient> Recipients);

/// <summary>
/// Sending a promo to customers (BUSINESS_RULES 6.y21). The system sends nothing itself: it prepares the message from the shop's promo and lists the customers
/// who <b>said yes</b> to promo messages and can be reached (a real mobile number or an email address), and staff send each one from their own phone.
/// Staff mark who was sent it, so nobody is sent the same promo twice by mistake. Consent is given by the customer (a box on the booking form) or recorded
/// by staff when a customer asks; it is never switched on by the shop on its own. Every query is scoped by the caller's business id.
/// </summary>
public sealed class PromoMessageService(IDbContextFactory<AppDbContext> dbFactory, PromoBannerService promo, TimeProvider time)
{
    public const int MaxListed = 200;

    private static IQueryable<Customer> Reachable(AppDbContext db, Guid businessId) =>
        db.Customers.Where(c => c.BusinessId == businessId && c.MarketingOk
            && (!c.Phone.StartsWith(DisplayFormat.WalkInPhonePrefix) || (c.Email != null && c.Email != "")));

    public async Task<PromoSendView> GetViewAsync(Guid businessId, string timeZone, string? query, CancellationToken ct = default)
    {
        var view = await promo.GetForPageAsync(businessId, timeZone, ct);
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var all = Reachable(db, businessId);
        var eligible = await all.CountAsync(ct);
        var sent = await all.CountAsync(c => c.PromoSentAtUtc != null, ct);

        var q = (query ?? "").Trim();
        var matching = q.Length == 0 ? all : all.Where(c => EF.Functions.ILike(c.Name, "%" + Escape(q) + "%") || EF.Functions.ILike(c.Phone, "%" + Escape(q) + "%") || (c.Email != null && EF.Functions.ILike(c.Email, "%" + Escape(q) + "%")));
        var total = await matching.CountAsync(ct);
        var rows = await matching.OrderBy(c => c.PromoSentAtUtc != null).ThenBy(c => c.Name).Take(MaxListed)
            .Select(c => new PromoRecipient(c.Id, c.Name, c.Phone, c.Email, c.PromoSentAtUtc)).ToListAsync(ct);
        return new PromoSendView(view, eligible, sent, total, rows);
    }

    private static string Escape(string s) => s.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");

    /// <summary>Marks the customer as sent (or not sent) the current promo. Only a customer who said yes can be marked.</summary>
    public async Task<ServiceResult<bool>> MarkSentAsync(Guid businessId, Guid customerId, bool sent, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var now = time.GetUtcNow().UtcDateTime;
        var changed = await db.Customers.Where(c => c.Id == customerId && c.BusinessId == businessId && c.MarketingOk)
            .ExecuteUpdateAsync(s => s.SetProperty(c => c.PromoSentAtUtc, sent ? now : (DateTime?)null), ct);
        return changed == 0 ? ServiceResult<bool>.Fail(FailureKind.NotFound, Text.NotFound) : ServiceResult<bool>.Ok(sent);
    }

    /// <summary>Clears every "sent" mark of this shop, for a new promo.</summary>
    public async Task<int> ResetSentAsync(Guid businessId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.Customers.Where(c => c.BusinessId == businessId && c.PromoSentAtUtc != null)
            .ExecuteUpdateAsync(s => s.SetProperty(c => c.PromoSentAtUtc, (DateTime?)null), ct);
    }

    public async Task<(bool Allowed, DateTime? AtUtc)?> GetConsentAsync(Guid businessId, Guid customerId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var row = await db.Customers.AsNoTracking().Where(c => c.Id == customerId && c.BusinessId == businessId)
            .Select(c => new { c.MarketingOk, c.MarketingOkAtUtc }).FirstOrDefaultAsync(ct);
        return row is null ? null : (row.MarketingOk, row.MarketingOkAtUtc);
    }

    /// <summary>
    /// Records that the customer said yes (or asked to stop) when they told the shop in person or by message. Written to the audit log
    /// with who recorded it. Stopping also clears the "sent" mark.
    /// </summary>
    public async Task<ServiceResult<bool>> SetConsentAsync(Guid businessId, Guid customerId, bool allow, BookingActor actor, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var customer = await db.Customers.FirstOrDefaultAsync(c => c.Id == customerId && c.BusinessId == businessId, ct);
        if (customer is null) return ServiceResult<bool>.Fail(FailureKind.NotFound, Text.NotFound);
        if (customer.MarketingOk == allow) return ServiceResult<bool>.Ok(allow);
        var now = time.GetUtcNow().UtcDateTime;
        customer.MarketingOk = allow;
        customer.MarketingOkAtUtc = allow ? now : customer.MarketingOkAtUtc;
        if (!allow) customer.PromoSentAtUtc = null;
        db.AuditLogs.Add(new AuditLog
        {
            BusinessId = businessId, ActorType = actor.TypeCode, ActorId = actor.Id, ActorLabel = actor.Label, AtUtc = now,
            Action = allow ? "PROMO_CONSENT_ON" : "PROMO_CONSENT_OFF", Reason = $"{customer.Name}: promo messages {(allow ? "allowed" : "stopped")}",
        });
        await db.SaveChangesAsync(ct);
        return ServiceResult<bool>.Ok(allow);
    }
}
