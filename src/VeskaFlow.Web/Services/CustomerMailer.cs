using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using VeskaFlow.Core.Domain;
using VeskaFlow.Core.Scheduling;
using VeskaFlow.Web.Data;

namespace VeskaFlow.Web.Services;

public enum MailResult
{
    Sent,
    /// <summary>Not sent for a normal reason (no email, switched off, no email service, refused): nothing to retry now.</summary>
    NotSent,
    /// <summary>The shop used its share of the day; other shops may still send.</summary>
    ShopLimit,
    /// <summary>The day's customer emails are used up for everyone.</summary>
    PoolLimit,
}

/// <summary>
/// The two automatic emails to a shop's customer: the booking confirmation and the reminder. Both need the customer to have
/// given an email, the shop to have the switch on, the email service to be set up, and room in the daily limit
/// (<see cref="EmailQuota"/>). Without room nothing is sent and staff use the manual messages as before.
/// </summary>
public sealed class CustomerMailer(
    [FromKeyedServices("raw")] IEmailSender email, EmailQuota quota, IDbContextFactory<AppDbContext> dbFactory, ILogger<CustomerMailer> log)
{
    public Task<MailResult> SendConfirmationAsync(Guid businessId, Guid bookingId, string baseUrl, CancellationToken ct = default) =>
        SendAsync(businessId, bookingId, baseUrl, reminder: false, ct);

    public Task<MailResult> SendReminderAsync(Guid businessId, Guid bookingId, string baseUrl, CancellationToken ct = default) =>
        SendAsync(businessId, bookingId, baseUrl, reminder: true, ct);

    private async Task<MailResult> SendAsync(Guid businessId, Guid bookingId, string baseUrl, bool reminder, CancellationToken ct)
    {
        if (!email.IsConfigured || string.IsNullOrWhiteSpace(baseUrl)) return MailResult.NotSent;

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var row = await db.Bookings.AsNoTracking().Where(b => b.Id == bookingId && b.BusinessId == businessId)
            .Select(b => new
            {
                b.Status, b.StartsAtUtc, b.ManageToken, b.ServiceNameSnapshot, StaffName = b.Staff.Name,
                CustomerName = b.Customer.Name, CustomerEmail = b.Customer.Email,
                ShopName = db.Businesses.Where(x => x.Id == b.BusinessId).Select(x => x.Name).First(),
                TimeZone = db.Businesses.Where(x => x.Id == b.BusinessId).Select(x => x.TimeZone).First(),
                Enabled = db.Businesses.Where(x => x.Id == b.BusinessId).Select(x => x.IsEnabled && x.CustomerEmails).First(),
            }).FirstOrDefaultAsync(ct);
        if (row is null || !row.Enabled || row.Status != BookingStatus.Confirmed) return MailResult.NotSent;
        var to = (row.CustomerEmail ?? "").Trim();
        if (to.Length is < 3 or > 120 || !to.Contains('@') || to.Contains(' ')) return MailResult.NotSent;

        var room = await quota.CheckCustomerAsync(businessId, ct);
        if (room == EmailRoom.ShopLimit) return MailResult.ShopLimit;
        if (room == EmailRoom.PoolLimit) return MailResult.PoolLimit;

        var first = row.CustomerName.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? row.CustomerName;
        var when = $"{LocalTime.ToLocal(row.StartsAtUtc, row.TimeZone).ToString("dddd, MMMM d", System.Globalization.CultureInfo.InvariantCulture)} at {DisplayFormat.Time(row.StartsAtUtc, row.TimeZone)}";
        var link = $"{baseUrl.TrimEnd('/')}/booking/{row.ManageToken}";
        var subject = reminder ? Text.ReminderSubject : Text.BookingEmailSubject(row.ShopName);
        var body = reminder
            ? Text.ReminderMessage(first, row.ShopName, row.ServiceNameSnapshot, when, link) + "\n\n" + Text.CustomerEmailFooter(row.ShopName)
            : Text.BookingEmailBody(first, row.ShopName, row.ServiceNameSnapshot, row.StaffName, when, link);

        var ok = await email.SendAsync(to, subject, body, null, ct);
        if (!ok)
        {
            log.LogWarning("A customer email was not accepted by the email service."); // no address in the log
            return MailResult.NotSent;
        }
        await quota.CountAsync(businessId, ct);
        return MailResult.Sent;
    }
}

/// <summary>Sends the reminder email for confirmed bookings that are inside the shop's reminder window, then takes them off the manual list.</summary>
public sealed class AutoReminderService(IDbContextFactory<AppDbContext> dbFactory, TimeProvider time, CustomerMailer mailer, IConfiguration config)
{
    /// <summary>A booking made less than this long ago already got its confirmation; the reminder waits so the two do not arrive together.</summary>
    public static readonly TimeSpan MinAge = TimeSpan.FromHours(3);

    /// <summary>No reminder in the last half hour before the appointment.</summary>
    public static readonly TimeSpan MinLead = TimeSpan.FromMinutes(30);

    public async Task<int> SendDueAsync(CancellationToken ct = default)
    {
        var baseUrl = config["App:PublicBaseUrl"] is { Length: > 0 } configured ? configured.Trim().TrimEnd('/') : "";
        if (baseUrl.Length == 0) return 0; // no address for the manage link: leave it to the manual list

        var now = time.GetUtcNow().UtcDateTime;
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var due = await db.Bookings.AsNoTracking()
            .Where(b => b.Status == BookingStatus.Confirmed && b.RemindedAtUtc == null && b.Customer.Email != null && b.Customer.Email != ""
                        && b.StartsAtUtc > now.Add(MinLead) && b.CreatedAtUtc <= now.Subtract(MinAge)
                        && db.Businesses.Any(x => x.Id == b.BusinessId && x.IsEnabled && x.CustomerEmails
                                                   && b.StartsAtUtc <= now.AddHours(x.ReminderHours)))
            .OrderBy(b => b.StartsAtUtc).Take(200).Select(b => new { b.Id, b.BusinessId }).ToListAsync(ct);

        var sent = 0;
        foreach (var item in due)
        {
            var result = await mailer.SendReminderAsync(item.BusinessId, item.Id, baseUrl, ct);
            if (result == MailResult.PoolLimit) break; // the day's customer emails are used up
            if (result != MailResult.Sent) continue;
            await db.Bookings.Where(b => b.Id == item.Id).ExecuteUpdateAsync(s => s.SetProperty(b => b.RemindedAtUtc, now), ct);
            sent++;
        }
        return sent;
    }
}

/// <summary>Looks for reminders to email a few minutes after start and then every 15 minutes.</summary>
public sealed class ReminderEmailWorker(IServiceScopeFactory scopes, IConfiguration config, ILogger<ReminderEmailWorker> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!config.GetValue("Notices:Enabled", true)) return;
        try
        {
            await Task.Delay(TimeSpan.FromMinutes(3), stoppingToken);
            using var timer = new PeriodicTimer(TimeSpan.FromMinutes(15));
            do
            {
                try
                {
                    await using var scope = scopes.CreateAsyncScope();
                    var sent = await scope.ServiceProvider.GetRequiredService<AutoReminderService>().SendDueAsync(stoppingToken);
                    if (sent > 0) log.LogInformation("Sent {Count} customer reminder emails.", sent);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    log.LogWarning("The customer reminder emails could not run: {Reason}", ex.GetType().Name);
                }
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException) { }
    }
}
