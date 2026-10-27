using VeskaFlow.Web.Data;
using Microsoft.EntityFrameworkCore;

namespace VeskaFlow.Web.Services;

/// <summary>
/// Emails a shop's owner 7, 3 and 1 days before their access ends, once each, so nobody is locked out by surprise.
/// Nothing is sent without an email service, to a shop that is switched off, or after the end date. An extension (a new
/// end date) starts the reminders over.
/// </summary>
public sealed class ExpiryNoticeService(
    IDbContextFactory<AppDbContext> dbFactory, TimeProvider time, IEmailSender email, BillingInfo billing, IConfiguration config,
    ILogger<ExpiryNoticeService> log)
{
    public static readonly int[] Thresholds = [7, 3, 1];

    /// <summary>The reminder step for this many whole days left (7, 3 or 1), or null when it is too early or already over.</summary>
    public static int? StageFor(double daysLeft)
    {
        if (daysLeft <= 0) return null;
        var whole = (int)Math.Ceiling(daysLeft);
        var stage = Thresholds.Where(t => whole <= t).DefaultIfEmpty(0).Min();
        return stage == 0 ? null : stage;
    }

    /// <summary>Sends every reminder that is due now. Returns how many emails went out.</summary>
    public async Task<int> SendDueAsync(CancellationToken ct = default)
    {
        if (!email.IsConfigured) return 0;
        var now = time.GetUtcNow().UtcDateTime;
        var horizon = now.AddDays(Thresholds.Max() + 1);
        var baseUrl = config["App:PublicBaseUrl"] is { Length: > 0 } configured ? configured.Trim().TrimEnd('/') : "";

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var shops = await db.Businesses
            .Where(b => b.IsEnabled && b.TrialEndsAtUtc != null && b.TrialEndsAtUtc > now && b.TrialEndsAtUtc <= horizon)
            .ToListAsync(ct);

        var sent = 0;
        foreach (var shop in shops)
        {
            var end = DateTime.SpecifyKind(shop.TrialEndsAtUtc!.Value, DateTimeKind.Utc);
            if (StageFor((end - now).TotalDays) is not { } stage) continue;
            var lastStage = shop.ExpiryNoticeForEndUtc is { } forEnd && DateTime.SpecifyKind(forEnd, DateTimeKind.Utc) == end ? shop.ExpiryNoticeDays : null;
            if (lastStage is { } last && stage >= last) continue; // already told them at this step (or a closer one)

            var owner = await db.AdminUsers.AsNoTracking()
                .Where(u => u.BusinessId == shop.Id && u.Role == AdminRoles.Owner && u.IsActive && u.EmailConfirmedAtUtc != null)
                .OrderBy(u => u.CreatedAtUtc).FirstOrDefaultAsync(ct);
            if (owner is null) continue;

            var endText = LocalDate(end, shop.TimeZone);
            var ok = await email.SendAsync(owner.Email, Text.ExpirySubject(stage),
                Text.ExpiryBody(owner.DisplayName, shop.Name, endText, stage, billing.Price, billing.PayTo, billing.Note, baseUrl + "/Login"), null, ct);
            if (!ok)
            {
                log.LogWarning("An expiry reminder email was not accepted by the email service."); // no address in the log
                continue; // try again at the next run
            }
            shop.ExpiryNoticeDays = stage;
            shop.ExpiryNoticeForEndUtc = end;
            await db.SaveChangesAsync(ct);
            sent++;
        }
        return sent;
    }

    private static string LocalDate(DateTime utc, string timeZone) =>
        VeskaFlow.Core.Scheduling.LocalTime.ToLocal(utc, timeZone).ToString("MMMM d, yyyy", System.Globalization.CultureInfo.InvariantCulture);
}

/// <summary>Runs the expiry reminders a couple of minutes after start and then every 6 hours.</summary>
public sealed class ExpiryNoticeWorker(IServiceScopeFactory scopes, IConfiguration config, ILogger<ExpiryNoticeWorker> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!config.GetValue("Notices:Enabled", true)) return;
        try
        {
            await Task.Delay(TimeSpan.FromMinutes(2), stoppingToken);
            using var timer = new PeriodicTimer(TimeSpan.FromHours(6));
            do
            {
                try
                {
                    await using var scope = scopes.CreateAsyncScope();
                    var sent = await scope.ServiceProvider.GetRequiredService<ExpiryNoticeService>().SendDueAsync(stoppingToken);
                    if (sent > 0) log.LogInformation("Sent {Count} access-ending reminder emails.", sent);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    log.LogWarning("The access-ending reminders could not run: {Reason}", ex.GetType().Name);
                }
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException) { }
    }
}
