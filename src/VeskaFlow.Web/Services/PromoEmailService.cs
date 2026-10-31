using System.Security.Cryptography;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using VeskaFlow.Web.Data;

namespace VeskaFlow.Web.Services;

/// <summary>Where a shop's automatic promo email stands, for its page.</summary>
public sealed record PromoEmailStatus(bool ServiceReady, bool CustomersEmailOn, bool Queued, int Pending, int PerDay, bool HasAddon);

/// <summary>
/// The automatic promo email (BUSINESS_RULES 6.y21). The shop starts it with one button; a background worker then emails the running promo to the
/// customers who <b>said yes</b> to promo messages and have an email address, and marks each as sent so nobody gets it twice. It stays inside the daily
/// email limit (<see cref="EmailQuota"/>): when the day's room is used up it stops and goes on the next day. Every email carries a link that stops promo
/// messages for that customer (<c>/Unsubscribe/{token}</c>). It stops by itself when the promo ends or is switched off, or when everyone has been sent it.
/// </summary>
public sealed class PromoEmailService(
    [FromKeyedServices("raw")] IEmailSender email, EmailQuota quota, IDbContextFactory<AppDbContext> dbFactory, PromoBannerService promo,
    TimeProvider time, IConfiguration config, ILogger<PromoEmailService> log)
{
    /// <summary>Emails sent to one shop's customers in one run (the worker runs every few minutes).</summary>
    public const int BatchSize = 40;

    private static bool ValidEmail(string? e) => e is { Length: >= 3 and <= 120 } && e.Contains('@') && !e.Contains(' ');

    private static IQueryable<Customer> Pending(AppDbContext db, Guid businessId) =>
        db.Customers.Where(c => c.BusinessId == businessId && c.MarketingOk && c.PromoSentAtUtc == null && c.Email != null && c.Email != "");

    public async Task<PromoEmailStatus> StatusAsync(Guid businessId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var shop = await db.Businesses.AsNoTracking().Where(b => b.Id == businessId)
            .Select(b => new { b.CustomerEmails, b.PromoEmailStartedAtUtc, b.EmailAddonUntilUtc }).FirstOrDefaultAsync(ct);
        if (shop is null) return new PromoEmailStatus(false, false, false, 0, 0, false);
        var pending = (await Pending(db, businessId).Select(c => c.Email!).ToListAsync(ct)).Count(ValidEmail);
        var addon = shop.EmailAddonUntilUtc is { } u && DateTime.SpecifyKind(u, DateTimeKind.Utc) > time.GetUtcNow().UtcDateTime;
        return new PromoEmailStatus(email.IsConfigured && PublicBaseUrl.Length > 0, shop.CustomerEmails, shop.PromoEmailStartedAtUtc is not null, pending,
            addon ? quota.CustomerCap : quota.FreeShopMax, addon);
    }

    private string PublicBaseUrl => config["App:PublicBaseUrl"] is { Length: > 0 } u ? u.Trim().TrimEnd('/') : "";

    /// <summary>Starts the automatic sending. Needs a promo that is showing, the shop's customer-email switch, a working email service, and someone to send to.</summary>
    public async Task<ServiceResult<int>> StartAsync(Guid businessId, string timeZone, BookingActor actor, CancellationToken ct = default)
    {
        if (await promo.GetForPageAsync(businessId, timeZone, ct) is null) return ServiceResult<int>.Fail(FailureKind.PolicyViolation, Text.PromoEmailNeedPromo);
        var status = await StatusAsync(businessId, ct);
        if (!status.ServiceReady) return ServiceResult<int>.Fail(FailureKind.PolicyViolation, Text.PromoEmailNoService);
        if (!status.CustomersEmailOn) return ServiceResult<int>.Fail(FailureKind.PolicyViolation, Text.PromoEmailSwitchedOff);
        if (status.Pending == 0) return ServiceResult<int>.Fail(FailureKind.PolicyViolation, Text.PromoEmailNobody);

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var now = time.GetUtcNow().UtcDateTime;
        await db.Businesses.Where(b => b.Id == businessId).ExecuteUpdateAsync(s => s.SetProperty(b => b.PromoEmailStartedAtUtc, now), ct);
        db.AuditLogs.Add(new AuditLog
        {
            BusinessId = businessId, ActorType = actor.TypeCode, ActorId = actor.Id, ActorLabel = actor.Label, AtUtc = now,
            Action = "PROMO_EMAIL_STARTED", Reason = $"{status.Pending} customers",
        });
        await db.SaveChangesAsync(ct);
        return ServiceResult<int>.Ok(status.Pending);
    }

    public async Task StopAsync(Guid businessId, BookingActor actor, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var changed = await db.Businesses.Where(b => b.Id == businessId && b.PromoEmailStartedAtUtc != null)
            .ExecuteUpdateAsync(s => s.SetProperty(b => b.PromoEmailStartedAtUtc, (DateTime?)null), ct);
        if (changed == 0) return;
        db.AuditLogs.Add(new AuditLog
        {
            BusinessId = businessId, ActorType = actor.TypeCode, ActorId = actor.Id, ActorLabel = actor.Label,
            AtUtc = time.GetUtcNow().UtcDateTime, Action = "PROMO_EMAIL_STOPPED",
        });
        await db.SaveChangesAsync(ct);
    }

    /// <summary>One pass of the worker: sends the next batch for every shop that has sending switched on. Returns how many emails went out.</summary>
    public async Task<int> RunAsync(CancellationToken ct = default)
    {
        if (!email.IsConfigured || PublicBaseUrl.Length == 0) return 0;
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var shops = await db.Businesses.AsNoTracking().Where(b => b.PromoEmailStartedAtUtc != null && b.IsEnabled)
            .Select(b => new { b.Id, b.Name, b.Slug, b.TimeZone, b.CustomerEmails }).ToListAsync(ct);

        var sent = 0;
        foreach (var shop in shops)
        {
            var view = await promo.GetForPageAsync(shop.Id, shop.TimeZone, ct);
            if (view is null || !shop.CustomerEmails) { await ClearAsync(shop.Id, "PROMO_EMAIL_ENDED", view is null ? "the promo is not showing" : "customer emails are off", ct); continue; }

            var batch = await Pending(db, shop.Id).OrderBy(c => c.Name).Take(BatchSize).ToListAsync(ct);
            batch = batch.Where(c => ValidEmail(c.Email)).ToList();
            if (batch.Count == 0)
            {
                var more = (await Pending(db, shop.Id).Select(c => c.Email!).ToListAsync(ct)).Any(ValidEmail);
                if (!more) await ClearAsync(shop.Id, "PROMO_EMAIL_DONE", "everyone has been sent it", ct);
                continue;
            }

            foreach (var customer in batch)
            {
                var room = await quota.CheckCustomerAsync(shop.Id, ct);
                if (room != EmailRoom.Ok) break; // today's room is used up for this shop (or for everyone): the rest waits for tomorrow

                await db.Entry(customer).ReloadAsync(ct); // they may have stopped, or been sent it by hand, a moment ago
                if (!customer.MarketingOk || customer.PromoSentAtUtc is not null) continue;
                customer.MarketingToken ??= NewToken();
                var first = customer.Name.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? customer.Name;
                var body = Text.PromoEmailBody(first, shop.Name, view.Headline, view.Details, view.DiscountPercent, view.EndsOn,
                    $"{PublicBaseUrl}/b/{shop.Slug}", $"{PublicBaseUrl}/Unsubscribe/{customer.MarketingToken}");
                // Save the token first: the link in the email must work even if the next step fails.
                await db.SaveChangesAsync(ct);
                if (!await email.SendAsync(customer.Email!.Trim(), Text.PromoSendSubject(shop.Name), body, null, ct))
                {
                    log.LogWarning("A promo email was not accepted by the email service."); // no address in the log
                    continue;
                }
                await quota.CountAsync(shop.Id, ct);
                customer.PromoSentAtUtc = time.GetUtcNow().UtcDateTime;
                await db.SaveChangesAsync(ct);
                sent++;
            }

            // Everyone in the batch was sent it and nobody is left: finish now, so the page does not say "sending" with nothing to send.
            if (!(await Pending(db, shop.Id).Select(c => c.Email!).ToListAsync(ct)).Any(ValidEmail))
                await ClearAsync(shop.Id, "PROMO_EMAIL_DONE", "everyone has been sent it", ct);
        }
        return sent;
    }

    private async Task ClearAsync(Guid businessId, string action, string reason, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        await db.Businesses.Where(b => b.Id == businessId).ExecuteUpdateAsync(s => s.SetProperty(b => b.PromoEmailStartedAtUtc, (DateTime?)null), ct);
        db.AuditLogs.Add(new AuditLog { BusinessId = businessId, ActorType = "SYSTEM", ActorLabel = "System", AtUtc = time.GetUtcNow().UtcDateTime, Action = action, Reason = reason });
        await db.SaveChangesAsync(ct);
    }

    private static string NewToken() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).Replace('+', '-').Replace('/', '_').TrimEnd('=');

    /// <summary>The shop and customer name behind an unsubscribe link, or null when the link is not valid.</summary>
    public async Task<(string Shop, string Customer)?> LookupAsync(string? token, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(token) || token.Length is < 20 or > 80) return null;
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var row = await db.Customers.AsNoTracking().Where(c => c.MarketingToken == token)
            .Select(c => new { c.Name, Shop = db.Businesses.Where(b => b.Id == c.BusinessId).Select(b => b.Name).First() }).FirstOrDefaultAsync(ct);
        return row is null ? null : (row.Shop, row.Name);
    }

    /// <summary>Stops promo messages for the customer behind the link. Safe to repeat. False when the link is not valid.</summary>
    public async Task<bool> UnsubscribeAsync(string? token, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(token) || token.Length is < 20 or > 80) return false;
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var customer = await db.Customers.FirstOrDefaultAsync(c => c.MarketingToken == token, ct);
        if (customer is null) return false;
        if (!customer.MarketingOk) return true;
        customer.MarketingOk = false;
        customer.PromoSentAtUtc = null;
        db.AuditLogs.Add(new AuditLog
        {
            BusinessId = customer.BusinessId, ActorType = "CUSTOMER", ActorLabel = $"{customer.Name} (unsubscribe link)", AtUtc = time.GetUtcNow().UtcDateTime,
            Action = "PROMO_CONSENT_OFF", Reason = $"{customer.Name}: promo messages stopped",
        });
        await db.SaveChangesAsync(ct);
        return true;
    }
}

/// <summary>Sends promo emails in the background: a few minutes after start and then every 5 minutes.</summary>
public sealed class PromoEmailWorker(IServiceScopeFactory scopes, IConfiguration config, ILogger<PromoEmailWorker> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!config.GetValue("Notices:Enabled", true)) return;
        try
        {
            await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);
            using var timer = new PeriodicTimer(TimeSpan.FromMinutes(5));
            do
            {
                try
                {
                    await using var scope = scopes.CreateAsyncScope();
                    var sent = await scope.ServiceProvider.GetRequiredService<PromoEmailService>().RunAsync(stoppingToken);
                    if (sent > 0) log.LogInformation("Sent {Count} promo emails.", sent);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    log.LogWarning("The promo emails could not run: {Reason}", ex.GetType().Name);
                }
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException) { }
    }
}
