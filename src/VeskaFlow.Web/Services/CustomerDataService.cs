using System.Text.Json;
using VeskaFlow.Web.Data;
using Microsoft.EntityFrameworkCore;

namespace VeskaFlow.Web.Services;

/// <summary>
/// A customer's right to see and to erase their data, carried out by the shop (Data Privacy Act of 2012, RA 10173).
/// Export gives everything the shop keeps about one customer; delete removes that customer with their bookings, records,
/// follow-ups, audit rows and chats with the AI assistant. Every query is scoped by the caller's business id: another shop's
/// customer is "not found".
/// </summary>
public sealed class CustomerDataService(IDbContextFactory<AppDbContext> dbFactory)
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    /// <summary>
    /// The pattern that finds this customer's chats with the AI assistant. A chat has no link to a customer, so it is found by
    /// the mobile number typed in it (digits only, the last 10, so "0917...", "+63 917..." and "917..." all match). Null when
    /// the customer has no real number (a walk-in placeholder), because then nothing can be matched safely.
    /// </summary>
    private static string? ChatPattern(string phone)
    {
        if (phone.StartsWith("walkin-", StringComparison.Ordinal)) return null;
        var digits = new string(phone.Where(char.IsAsciiDigit).ToArray());
        if (digits.Length < 7) return null;
        return "%" + (digits.Length > 10 ? digits[^10..] : digits) + "%";
    }

    /// <summary>The customer's data as JSON text, or null for an unknown id or another shop's customer.</summary>
    public async Task<string?> ExportJsonAsync(Guid businessId, Guid customerId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var customer = await db.Customers.AsNoTracking().FirstOrDefaultAsync(c => c.Id == customerId && c.BusinessId == businessId, ct);
        if (customer is null) return null;

        var bookings = await db.Bookings.AsNoTracking()
            .Where(b => b.BusinessId == businessId && b.CustomerId == customerId)
            .OrderBy(b => b.StartsAtUtc)
            .Select(b => new
            {
                b.Id, Service = b.ServiceNameSnapshot, Staff = b.Staff.Name, StartsAtUtc = b.StartsAtUtc, EndsAtUtc = b.EndsAtUtc,
                Status = b.Status.ToString(), Price = b.PriceSnapshot, b.VisitNotes, b.FollowUpOn, b.FollowUpNote, b.FollowUpService,
                b.CancelledBy, b.CancelReason, b.CreatedAtUtc,
            }).ToListAsync(ct);
        var ids = bookings.Select(b => b.Id).ToList();
        var audit = await db.AuditLogs.AsNoTracking()
            .Where(a => a.BusinessId == businessId && a.BookingId != null && ids.Contains(a.BookingId.Value))
            .OrderBy(a => a.AtUtc)
            .Select(a => new { a.AtUtc, a.Action, a.FromStatus, a.ToStatus, a.Reason }).ToListAsync(ct);

        var chats = new List<object>();
        if (ChatPattern(customer.Phone) is { } pattern)
        {
            var rows = await db.AiChats.FromSqlInterpolated($"""
                SELECT * FROM ai_chat WHERE business_id = {businessId}
                AND regexp_replace(transcript, '[^0-9]', '', 'g') LIKE {pattern}
                """).AsNoTracking().OrderBy(c => c.CreatedAtUtc).ToListAsync(ct);
            foreach (var row in rows)
            {
                object messages;
                try { messages = JsonSerializer.Deserialize<JsonElement>(row.Transcript); }
                catch (JsonException) { messages = row.Transcript; }
                chats.Add(new { startedAtUtc = row.CreatedAtUtc, messages });
            }
        }

        return JsonSerializer.Serialize(new
        {
            exportedAtUtc = DateTime.UtcNow,
            customer = new { customer.Name, customer.Phone, customer.Email, customer.CreatedAtUtc },
            bookings,
            auditTrail = audit,
            assistantChats = chats,
        }, Json);
    }

    /// <summary>
    /// Permanently deletes the customer and everything tied to them. The shop must type the customer's name; a wrong name
    /// deletes nothing.
    /// </summary>
    public async Task<ServiceResult<bool>> DeleteAsync(Guid businessId, Guid customerId, string? typedName, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var found = await db.Customers.AsNoTracking().Where(c => c.Id == customerId && c.BusinessId == businessId)
            .Select(c => new { c.Name, c.Phone }).FirstOrDefaultAsync(ct);
        if (found is null) return ServiceResult<bool>.Fail(FailureKind.NotFound, Text.NotFound);
        if (!string.Equals((typedName ?? "").Trim(), found.Name.Trim(), StringComparison.OrdinalIgnoreCase))
            return ServiceResult<bool>.Invalid(new Dictionary<string, string> { ["confirm"] = Text.CustomerDataMismatch });

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var bookingIds = await db.Bookings.Where(b => b.BusinessId == businessId && b.CustomerId == customerId).Select(b => b.Id).ToListAsync(ct);
        await db.AuditLogs.Where(a => a.BusinessId == businessId && a.BookingId != null && bookingIds.Contains(a.BookingId.Value)).ExecuteDeleteAsync(ct);
        await db.Bookings.Where(b => b.BusinessId == businessId && b.CustomerId == customerId).ExecuteDeleteAsync(ct);
        await db.Customers.Where(c => c.Id == customerId && c.BusinessId == businessId).ExecuteDeleteAsync(ct);
        if (ChatPattern(found.Phone) is { } pattern)
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                DELETE FROM ai_chat WHERE business_id = {businessId}
                AND regexp_replace(transcript, '[^0-9]', '', 'g') LIKE {pattern}
                """, ct);
        await tx.CommitAsync(ct);
        return ServiceResult<bool>.Ok(true);
    }
}
