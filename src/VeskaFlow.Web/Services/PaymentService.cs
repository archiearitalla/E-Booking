using Microsoft.EntityFrameworkCore;
using System.Text.RegularExpressions;
using VeskaFlow.Web.Data;

namespace VeskaFlow.Web.Services;

public sealed record PaymentRow(
    Guid Id, Guid BusinessId, string Shop, string Slug, string Kind, string Period, int Days, decimal Amount, string Reference, string? Note, string Status, bool ByHand,
    DateTime SubmittedAtUtc, string SubmittedBy, DateTime? DecidedAtUtc, string? DecidedBy, string? RejectReason, string? ReceiptNo);

public sealed record PaymentTotals(DateOnly Month, decimal Total, int Count, IReadOnlyDictionary<string, decimal> ByKind);

/// <summary>
/// Billing (BUSINESS_RULES 6.y22). A client says "I paid" with the payment's reference number; the platform owner checks it against the money received and
/// confirms it, which extends the access or the add-on and gives a receipt number, or rejects it. The amount is always the price from settings, never typed
/// by the client. The platform owner can also record a payment by hand (cash, Messenger, a discount). A payment is decided once. Every client query is scoped
/// by the caller's business id.
/// </summary>
public sealed partial class PaymentService(IDbContextFactory<AppDbContext> dbFactory, TimeProvider time, PricingInfo pricing)
{
    public const int MaxPending = 5;

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9 \-_/.]{2,59}$")]
    private static partial Regex ReferencePattern();

    private DateTime Now => time.GetUtcNow().UtcDateTime;

    private static PaymentRow Row(Payment p, string shop, string slug) => new(p.Id, p.BusinessId, shop, slug, p.Kind, p.Period, p.Days, p.Amount, p.Reference, p.Note, p.Status,
        p.ByHand, p.SubmittedAtUtc, p.SubmittedBy, p.DecidedAtUtc, p.DecidedBy, p.RejectReason, p.ReceiptNo);

    // ---------------------------------------------------------------- the client

    /// <summary>A client says it paid. Errors are keyed "option", "reference", "note" and "limit".</summary>
    public async Task<ServiceResult<Guid>> SubmitAsync(Guid businessId, string? optionKey, string? reference, string? note, string submittedBy, CancellationToken ct = default)
    {
        var errors = new Dictionary<string, string>();
        var option = pricing.Options.FirstOrDefault(o => o.Key == optionKey);
        if (option is null) errors["option"] = Text.PayOptionInvalid;
        var refText = (reference ?? "").Trim();
        if (!ReferencePattern().IsMatch(refText)) errors["reference"] = Text.PayReferenceInvalid;
        var noteText = string.IsNullOrWhiteSpace(note) ? null : note.Trim();
        if (noteText is { Length: > 200 }) errors["note"] = Text.PayNoteInvalid;
        if (errors.Count > 0) return ServiceResult<Guid>.Invalid(errors);

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        if (!await db.Businesses.AnyAsync(b => b.Id == businessId, ct)) return ServiceResult<Guid>.Fail(FailureKind.NotFound, Text.NotFound);
        if (await db.Payments.CountAsync(p => p.BusinessId == businessId && p.Status == "PENDING", ct) >= MaxPending)
            return ServiceResult<Guid>.Invalid(new Dictionary<string, string> { ["limit"] = Text.PayTooManyPending(MaxPending) });
        // One proof of payment pays once: a reference already used (and not rejected) is refused, for any shop.
        var key = refText.ToUpperInvariant();
        if (await db.Payments.AnyAsync(p => p.Status != "REJECTED" && p.Reference.ToUpper() == key, ct))
            return ServiceResult<Guid>.Invalid(new Dictionary<string, string> { ["reference"] = Text.PayReferenceUsed });

        var payment = new Payment
        {
            BusinessId = businessId, Kind = option!.Kind, Period = option.Period, Days = option.Days, Amount = option.Price, Reference = refText, Note = noteText,
            Status = "PENDING", SubmittedAtUtc = Now, SubmittedBy = submittedBy,
        };
        db.Payments.Add(payment);
        await db.SaveChangesAsync(ct);
        return ServiceResult<Guid>.Ok(payment.Id);
    }

    public async Task<IReadOnlyList<PaymentRow>> ListForShopAsync(Guid businessId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var rows = await db.Payments.AsNoTracking().Where(p => p.BusinessId == businessId).OrderByDescending(p => p.SubmittedAtUtc).Take(50).ToListAsync(ct);
        return rows.Select(p => Row(p, "", "")).ToList();
    }

    /// <summary>A confirmed payment of this shop, for its receipt. Null for an unknown id, another shop's payment, or one that is not confirmed.</summary>
    public async Task<PaymentRow?> GetReceiptAsync(Guid businessId, Guid paymentId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var p = await db.Payments.AsNoTracking().FirstOrDefaultAsync(x => x.Id == paymentId && x.BusinessId == businessId && x.Status == "CONFIRMED", ct);
        if (p is null) return null;
        var shop = await db.Businesses.AsNoTracking().Where(b => b.Id == businessId).Select(b => new { b.Name, b.Slug }).FirstAsync(ct);
        return Row(p, shop.Name, shop.Slug);
    }

    // ---------------------------------------------------------------- the platform owner

    public async Task<int> PendingCountAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.Payments.CountAsync(p => p.Status == "PENDING", ct);
    }

    public async Task<IReadOnlyList<PaymentRow>> ListAsync(string? status, int take, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var q = db.Payments.AsNoTracking().AsQueryable();
        if (status is not null) q = q.Where(p => p.Status == status);
        var rows = await q.OrderByDescending(p => p.SubmittedAtUtc).Take(take)
            .Join(db.Businesses, p => p.BusinessId, b => b.Id, (p, b) => new { p, b.Name, b.Slug }).ToListAsync(ct);
        return rows.Select(r => Row(r.p, r.Name, r.Slug)).ToList();
    }

    /// <summary>Confirms a pending payment: the access or the add-on is extended from the later of today and its current end, a receipt number is given, all in one step. A payment is decided once.</summary>
    public async Task<ServiceResult<string>> ConfirmAsync(Guid paymentId, string decidedBy, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var payment = await db.Payments.FromSqlInterpolated($"SELECT * FROM payment WHERE id = {paymentId} FOR UPDATE").FirstOrDefaultAsync(ct);
        if (payment is null) return ServiceResult<string>.Fail(FailureKind.NotFound, Text.NotFound);
        if (payment.Status != "PENDING") return ServiceResult<string>.Fail(FailureKind.PolicyViolation, Text.PayAlreadyDecided);
        var receipt = await ApplyAsync(db, payment, decidedBy);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return ServiceResult<string>.Ok(receipt);
    }

    public async Task<ServiceResult<bool>> RejectAsync(Guid paymentId, string? reason, string decidedBy, CancellationToken ct = default)
    {
        var why = string.IsNullOrWhiteSpace(reason) ? null : reason.Trim();
        if (why is { Length: > 200 }) return ServiceResult<bool>.Invalid(new Dictionary<string, string> { ["reason"] = Text.PayNoteInvalid });
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var now = Now;
        var changed = await db.Payments.Where(p => p.Id == paymentId && p.Status == "PENDING").ExecuteUpdateAsync(s => s
            .SetProperty(p => p.Status, "REJECTED").SetProperty(p => p.DecidedAtUtc, now).SetProperty(p => p.DecidedBy, decidedBy).SetProperty(p => p.RejectReason, why), ct);
        if (changed > 0) return ServiceResult<bool>.Ok(true);
        return await db.Payments.AnyAsync(p => p.Id == paymentId, ct)
            ? ServiceResult<bool>.Fail(FailureKind.PolicyViolation, Text.PayAlreadyDecided) : ServiceResult<bool>.Fail(FailureKind.NotFound, Text.NotFound);
    }

    /// <summary>The platform owner records a payment received another way (cash, a message). It is confirmed at once. The amount defaults to the price and may be lower (a discount).</summary>
    public async Task<ServiceResult<string>> RecordByHandAsync(Guid businessId, string? optionKey, decimal? amount, string? reference, string decidedBy, CancellationToken ct = default)
    {
        var errors = new Dictionary<string, string>();
        var option = pricing.Options.FirstOrDefault(o => o.Key == optionKey);
        if (option is null) errors["option"] = Text.PayOptionInvalid;
        var paid = amount ?? option?.Price ?? 0m;
        if (paid is < 0 or > 1_000_000) errors["amount"] = Text.PayAmountInvalid;
        var refText = string.IsNullOrWhiteSpace(reference) ? "By hand" : reference.Trim();
        if (refText.Length > 60) errors["reference"] = Text.PayReferenceInvalid;
        if (errors.Count > 0) return ServiceResult<string>.Invalid(errors);

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        if (!await db.Businesses.AnyAsync(b => b.Id == businessId, ct)) return ServiceResult<string>.Fail(FailureKind.NotFound, Text.NotFound);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var payment = new Payment
        {
            BusinessId = businessId, Kind = option!.Kind, Period = option.Period, Days = option.Days, Amount = Math.Round(paid, 2, MidpointRounding.AwayFromZero), Reference = refText,
            Status = "PENDING", ByHand = true, SubmittedAtUtc = Now, SubmittedBy = decidedBy,
        };
        db.Payments.Add(payment);
        var receipt = await ApplyAsync(db, payment, decidedBy);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return ServiceResult<string>.Ok(receipt);
    }

    /// <summary>Marks the payment confirmed with a receipt number and extends what it paid for. The caller saves.</summary>
    private async Task<string> ApplyAsync(AppDbContext db, Payment payment, string decidedBy)
    {
        var now = Now;
        var shop = await db.Businesses.FirstAsync(b => b.Id == payment.BusinessId);
        DateTime Extend(DateTime? current) => (current is { } c && DateTime.SpecifyKind(c, DateTimeKind.Utc) > now ? DateTime.SpecifyKind(c, DateTimeKind.Utc) : now).AddDays(payment.Days);
        switch (payment.Kind)
        {
            case PaymentKinds.Pro:
                // A shop with no end date has unlimited access already: only the plan changes.
                if (shop.TrialEndsAtUtc is not null) shop.TrialEndsAtUtc = Extend(shop.TrialEndsAtUtc);
                shop.Plan = "PRO";
                break;
            case PaymentKinds.Ai: shop.AiAddonUntilUtc = Extend(shop.AiAddonUntilUtc); break;
            case PaymentKinds.Email: shop.EmailAddonUntilUtc = Extend(shop.EmailAddonUntilUtc); break;
        }
        var number = await db.Database.SqlQuery<long>($"SELECT nextval('payment_receipt_seq') AS \"Value\"").SingleAsync();
        payment.Status = "CONFIRMED";
        payment.DecidedAtUtc = now;
        payment.DecidedBy = decidedBy;
        payment.ReceiptNo = $"VF-{now.Year}-{number:D6}";
        return payment.ReceiptNo;
    }

    public async Task<PaymentTotals> TotalsAsync(DateOnly month, CancellationToken ct = default)
    {
        var from = new DateTime(month.Year, month.Month, 1, 0, 0, 0, DateTimeKind.Utc);
        var to = from.AddMonths(1);
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var rows = await db.Payments.AsNoTracking().Where(p => p.Status == "CONFIRMED" && p.DecidedAtUtc >= from && p.DecidedAtUtc < to)
            .Select(p => new { p.Kind, p.Amount }).ToListAsync(ct);
        return new PaymentTotals(new DateOnly(month.Year, month.Month, 1), rows.Sum(r => r.Amount), rows.Count,
            rows.GroupBy(r => r.Kind).ToDictionary(g => g.Key, g => g.Sum(r => r.Amount)));
    }
}
