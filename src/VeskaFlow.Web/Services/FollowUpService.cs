using VeskaFlow.Core.Domain;
using VeskaFlow.Core.Scheduling;
using VeskaFlow.Web.Data;
using Microsoft.EntityFrameworkCore;

namespace VeskaFlow.Web.Services;

public sealed record FollowUpRow(
    Guid BookingId, string CustomerName, string CustomerPhone, string ServiceName,
    DateOnly VisitDate, DateOnly FollowUpOn, string? Note, bool Overdue, bool DueToday, string? CustomerEmail = null,
    string? FollowUpServiceName = null);

/// <summary>How many follow-ups need a message soon: overdue, and due today or within the next few days.</summary>
public sealed record FollowUpAlert(int Overdue, int Soon)
{
    public int Total => Overdue + Soon;

    /// <summary>The request item the admin layout fills so the header bell does not ask the database again.</summary>
    public const string ItemKey = "follow_up_alert";
}

/// <summary>
/// Follow-up visits (for example a clinic check-up two weeks after a procedure). Staff set a date on a completed booking;
/// the list shows what is still to do. Every query is scoped by the caller's business id.
/// </summary>
public sealed class FollowUpService(IDbContextFactory<AppDbContext> dbFactory, TimeProvider time)
{
    /// <summary>The number of follow-ups overdue and due within <paramref name="soonDays"/> days (today included), for the bell.</summary>
    public async Task<FollowUpAlert> AlertAsync(Guid businessId, int soonDays = 3, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var tz = await db.Businesses.AsNoTracking().Where(b => b.Id == businessId).Select(b => b.TimeZone).FirstOrDefaultAsync(ct);
        if (tz is null) return new FollowUpAlert(0, 0);

        var today = LocalTime.LocalDateOf(time.GetUtcNow().UtcDateTime, tz);
        var limit = today.AddDays(soonDays);
        var dates = await db.Bookings.AsNoTracking()
            .Where(b => b.BusinessId == businessId && b.FollowUpOn != null && !b.FollowUpDone
                        && b.Status == BookingStatus.Completed && b.FollowUpOn <= limit)
            .Select(b => b.FollowUpOn!.Value).ToListAsync(ct);
        return new FollowUpAlert(dates.Count(d => d < today), dates.Count(d => d >= today));
    }

    /// <summary>Follow-ups not yet done, soonest first (overdue ones come first).</summary>
    public async Task<IReadOnlyList<FollowUpRow>> ListAsync(Guid businessId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var tz = await db.Businesses.AsNoTracking().Where(b => b.Id == businessId).Select(b => b.TimeZone).FirstOrDefaultAsync(ct);
        if (tz is null) return [];

        var today = LocalTime.LocalDateOf(time.GetUtcNow().UtcDateTime, tz);
        var rows = await db.Bookings.AsNoTracking()
            .Where(b => b.BusinessId == businessId && b.FollowUpOn != null && !b.FollowUpDone && b.Status == BookingStatus.Completed)
            .OrderBy(b => b.FollowUpOn).ThenBy(b => b.StartsAtUtc)
            .Select(b => new { b.Id, CustomerName = b.Customer.Name, CustomerPhone = b.Customer.Phone, CustomerEmail = b.Customer.Email, b.ServiceNameSnapshot, b.StartsAtUtc, b.FollowUpOn, b.FollowUpNote, b.FollowUpService })
            .ToListAsync(ct);

        return rows.Select(r => new FollowUpRow(
            r.Id, r.CustomerName, r.CustomerPhone, r.ServiceNameSnapshot,
            LocalTime.LocalDateOf(r.StartsAtUtc, tz), r.FollowUpOn!.Value, r.FollowUpNote,
            r.FollowUpOn.Value < today, r.FollowUpOn.Value == today, r.CustomerEmail, r.FollowUpService)).ToList();
    }

    /// <summary>
    /// Sets (or clears, when <paramref name="date"/> is null) the follow-up date of a completed booking.
    /// The date may not be before the visit or more than two years after it.
    /// </summary>
    public async Task<ServiceResult<bool>> SetAsync(
        Guid businessId, Guid bookingId, DateOnly? date, string? note, Guid? followUpServiceId = null, CancellationToken ct = default)
    {
        var cleanNote = string.IsNullOrWhiteSpace(note) ? null : note.Trim();
        if (cleanNote is { Length: > 200 })
            return ServiceResult<bool>.Invalid(new Dictionary<string, string> { ["followUpNote"] = Text.ReasonTooLong });

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var booking = await db.Bookings.FirstOrDefaultAsync(b => b.Id == bookingId && b.BusinessId == businessId, ct);
        if (booking is null) return ServiceResult<bool>.Fail(FailureKind.NotFound, Text.NotFound);
        if (booking.Status != BookingStatus.Completed)
            return ServiceResult<bool>.Fail(FailureKind.PolicyViolation, Text.FollowUpOnlyCompleted);

        if (date is { } d)
        {
            var visit = LocalTime.LocalDateOf(booking.StartsAtUtc, booking.LocationTimeZone);
            if (d < visit || d.DayNumber - visit.DayNumber > 730)
                return ServiceResult<bool>.Invalid(new Dictionary<string, string> { ["followUpOn"] = Text.FollowUpDateInvalid });
        }

        // The service the follow-up is for must belong to this business; none means the same service as the visit.
        string? serviceName = null;
        if (date is not null && followUpServiceId is { } serviceId)
        {
            serviceName = await db.Services.AsNoTracking().Where(s => s.Id == serviceId && s.BusinessId == businessId)
                .Select(s => s.Name).FirstOrDefaultAsync(ct);
            if (serviceName is null) return ServiceResult<bool>.Fail(FailureKind.NotFound, Text.NotFound);
            if (serviceName == booking.ServiceNameSnapshot) serviceName = null;
        }

        booking.FollowUpOn = date;
        booking.FollowUpNote = date is null ? null : cleanNote;
        booking.FollowUpService = date is null ? null : serviceName;
        booking.FollowUpDone = false;
        await db.SaveChangesAsync(ct);
        return ServiceResult<bool>.Ok(true);
    }

    /// <summary>Takes a follow-up off the list (the customer came back, or was contacted).</summary>
    public async Task<ServiceResult<bool>> MarkDoneAsync(Guid businessId, Guid bookingId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var changed = await db.Bookings.Where(b => b.Id == bookingId && b.BusinessId == businessId && b.FollowUpOn != null)
            .ExecuteUpdateAsync(s => s.SetProperty(b => b.FollowUpDone, true), ct);
        return changed == 0 ? ServiceResult<bool>.Fail(FailureKind.NotFound, Text.NotFound) : ServiceResult<bool>.Ok(true);
    }
}
