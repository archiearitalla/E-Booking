using VeskaFlow.Core.Domain;
using VeskaFlow.Web.Data;
using Microsoft.EntityFrameworkCore;

namespace VeskaFlow.Web.Services;

public sealed record ReminderRow(
    Guid BookingId, string CustomerName, string CustomerPhone, string? CustomerEmail, string ServiceName, string StaffName,
    DateTime StartsAtUtc, string ManageToken);

/// <summary>
/// Reminders for customers before their appointment. The shop sends them itself; the system lists who is due (a confirmed
/// appointment starting within the shop's reminder hours, not yet marked reminded) and remembers who was reminded.
/// Every query is scoped by the caller's business id.
/// </summary>
public sealed class ReminderService(IDbContextFactory<AppDbContext> dbFactory, TimeProvider time)
{
    /// <summary>Customers due a reminder, soonest appointment first.</summary>
    public async Task<IReadOnlyList<ReminderRow>> ListPendingAsync(Guid businessId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var hours = await db.Businesses.AsNoTracking().Where(b => b.Id == businessId).Select(b => (int?)b.ReminderHours).FirstOrDefaultAsync(ct);
        if (hours is null) return [];

        var now = time.GetUtcNow().UtcDateTime;
        var until = now.AddHours(hours.Value);
        return await db.Bookings.AsNoTracking()
            .Where(b => b.BusinessId == businessId && b.Status == BookingStatus.Confirmed && b.RemindedAtUtc == null
                        && b.StartsAtUtc > now && b.StartsAtUtc <= until)
            .OrderBy(b => b.StartsAtUtc)
            .Select(b => new ReminderRow(b.Id, b.Customer.Name, b.Customer.Phone, b.Customer.Email, b.ServiceNameSnapshot, b.Staff.Name,
                b.StartsAtUtc, b.ManageToken))
            .ToListAsync(ct);
    }

    /// <summary>Takes a customer off the reminder list. Another business's booking is "not found".</summary>
    public async Task<ServiceResult<bool>> MarkRemindedAsync(Guid businessId, Guid bookingId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var now = time.GetUtcNow().UtcDateTime;
        var changed = await db.Bookings.Where(b => b.Id == bookingId && b.BusinessId == businessId)
            .ExecuteUpdateAsync(s => s.SetProperty(b => b.RemindedAtUtc, now), ct);
        return changed == 0 ? ServiceResult<bool>.Fail(FailureKind.NotFound, Text.NotFound) : ServiceResult<bool>.Ok(true);
    }
}
