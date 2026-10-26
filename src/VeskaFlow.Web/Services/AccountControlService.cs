using VeskaFlow.Web.Data;
using Microsoft.EntityFrameworkCore;

namespace VeskaFlow.Web.Services;

public sealed record ShopRow(
    Guid BusinessId, string Slug, string Name, string Industry, Guid OwnerUserId, string OwnerName, string OwnerEmail,
    DateTime CreatedAtUtc, bool EmailConfirmed, bool Enabled, DateTime? TrialEndsAtUtc);

/// <summary>A confirmation link that has been issued: the raw token (only ever in the email) and who it is for.</summary>
public sealed record IssuedConfirmation(string Token, string Email, string Name);

/// <summary>
/// Who may use the system: email confirmation for new owners, and the platform owner's switch to enable or disable a shop.
/// The platform owner is whoever signs in with an email listed in <c>Platform:AdminEmails</c>.
/// </summary>
public sealed class AccountControlService(
    IDbContextFactory<AppDbContext> dbFactory, TimeProvider time, IConfiguration config, IEmailSender email)
{
    public bool EmailConfigured => email.IsConfigured;

    // ------------------------------------------------------------------ email confirmation

    /// <summary>Opens the link from the email. An unknown, used or expired token is refused with one message.</summary>
    public async Task<ServiceResult<bool>> ConfirmEmailAsync(string? token, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(token) || token.Length > 200)
            return ServiceResult<bool>.Fail(FailureKind.NotFound, Text.ConfirmLinkInvalid);

        var hash = EmailTokens.Hash(token);
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var user = await db.AdminUsers.FirstOrDefaultAsync(u => u.EmailTokenHash == hash, ct);
        var now = time.GetUtcNow().UtcDateTime;
        if (user is null || user.EmailTokenExpiresUtc is not { } expires || expires < now)
            return ServiceResult<bool>.Fail(FailureKind.NotFound, Text.ConfirmLinkInvalid);

        user.EmailConfirmedAtUtc ??= now;
        user.EmailTokenHash = null;
        user.EmailTokenExpiresUtc = null;
        await db.SaveChangesAsync(ct);
        return ServiceResult<bool>.Ok(true);
    }

    /// <summary>A fresh link for an owner who has not confirmed yet. Null (silently) for an unknown or already confirmed email.</summary>
    public async Task<IssuedConfirmation?> IssueConfirmationAsync(string? emailAddress, CancellationToken ct = default)
    {
        var normalized = (emailAddress ?? "").Trim();
        if (normalized.Length is < 3 or > 120) return null;
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var user = await db.AdminUsers.FirstOrDefaultAsync(u => u.Email.ToLower() == normalized.ToLower(), ct);
        return user is null ? null : await IssueAsync(db, user, ct);
    }

    private async Task<IssuedConfirmation?> IssueAsync(AppDbContext db, AdminUser user, CancellationToken ct)
    {
        if (user.EmailConfirmedAtUtc is not null) return null;
        var (token, hash) = EmailTokens.NewToken();
        user.EmailTokenHash = hash;
        user.EmailTokenExpiresUtc = time.GetUtcNow().UtcDateTime.Add(EmailTokens.Lifetime);
        await db.SaveChangesAsync(ct);
        return new IssuedConfirmation(token, user.Email, user.DisplayName);
    }

    /// <summary>Emails the confirmation link. False when no email service is set up or the email was not accepted.</summary>
    public Task<bool> SendConfirmationAsync(IssuedConfirmation issued, string baseUrl, CancellationToken ct = default) =>
        email.SendAsync(issued.Email, Text.ConfirmEmailSubject, Text.ConfirmEmailBody(issued.Name, ConfirmUrl(baseUrl, issued.Token)),
            Text.ConfirmEmailHtml(issued.Name, ConfirmUrl(baseUrl, issued.Token)), ct);

    /// <summary>True when the email address of this account is confirmed. Used by the "check your email" page to continue by itself.</summary>
    public async Task<bool> IsEmailConfirmedAsync(string? emailAddress, CancellationToken ct = default)
    {
        var normalized = (emailAddress ?? "").Trim();
        if (normalized.Length is < 3 or > 120) return false;
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.AdminUsers.AsNoTracking().AnyAsync(u => u.Email.ToLower() == normalized.ToLower() && u.EmailConfirmedAtUtc != null, ct);
    }

    public static string ConfirmUrl(string baseUrl, string token) => $"{baseUrl.TrimEnd('/')}/ConfirmEmail?token={Uri.EscapeDataString(token)}";

    // ------------------------------------------------------------------ platform owner

    /// <summary>True when the signed-in user's email is listed in <c>Platform:AdminEmails</c> and confirmed.</summary>
    public async Task<bool> IsPlatformAdminAsync(Guid? adminUserId, CancellationToken ct = default)
    {
        var listed = (config["Platform:AdminEmails"] ?? "")
            .Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (adminUserId is null || listed.Length == 0) return false;

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var user = await db.AdminUsers.AsNoTracking().Where(u => u.Id == adminUserId).Select(u => new { u.Email, u.EmailConfirmedAtUtc, u.IsActive }).FirstOrDefaultAsync(ct);
        return user is { IsActive: true, EmailConfirmedAtUtc: not null }
               && listed.Any(a => string.Equals(a, user.Email, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Every shop with its owner, newest first.</summary>
    public async Task<IReadOnlyList<ShopRow>> ListShopsAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var shops = await db.Businesses.AsNoTracking().OrderByDescending(b => b.CreatedAtUtc)
            .Select(b => new { b.Id, b.Slug, b.Name, b.Industry, b.CreatedAtUtc, b.IsEnabled, b.TrialEndsAtUtc }).ToListAsync(ct);
        var owners = (await db.AdminUsers.AsNoTracking().Where(u => u.Role == "OWNER").OrderBy(u => u.CreatedAtUtc)
                .Select(u => new { u.Id, u.BusinessId, u.DisplayName, u.Email, Confirmed = u.EmailConfirmedAtUtc != null }).ToListAsync(ct))
            .GroupBy(u => u.BusinessId).ToDictionary(g => g.Key, g => g.First());

        return shops.Select(s =>
        {
            var owner = owners.GetValueOrDefault(s.Id);
            return new ShopRow(s.Id, s.Slug, s.Name, s.Industry, owner?.Id ?? Guid.Empty, owner?.DisplayName ?? "", owner?.Email ?? "",
                s.CreatedAtUtc, owner?.Confirmed ?? false, s.IsEnabled, s.TrialEndsAtUtc);
        }).ToList();
    }

    public async Task<ServiceResult<bool>> SetEnabledAsync(Guid businessId, bool enabled, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var changed = await db.Businesses.Where(b => b.Id == businessId).ExecuteUpdateAsync(s => s.SetProperty(b => b.IsEnabled, enabled), ct);
        return changed == 0 ? ServiceResult<bool>.Fail(FailureKind.NotFound, Text.NotFound) : ServiceResult<bool>.Ok(enabled);
    }

    /// <summary>The platform owner confirms an owner's email by hand (for example when no email service is set up).</summary>
    public async Task<ServiceResult<bool>> ConfirmManuallyAsync(Guid businessId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var now = time.GetUtcNow().UtcDateTime;
        var changed = await db.AdminUsers.Where(u => u.BusinessId == businessId && u.EmailConfirmedAtUtc == null)
            .ExecuteUpdateAsync(s => s.SetProperty(u => u.EmailConfirmedAtUtc, now)
                .SetProperty(u => u.EmailTokenHash, (string?)null).SetProperty(u => u.EmailTokenExpiresUtc, (DateTime?)null), ct);
        return changed == 0 ? ServiceResult<bool>.Fail(FailureKind.NotFound, Text.NotFound) : ServiceResult<bool>.Ok(true);
    }

    /// <summary>A new link for the owner of a shop, for the platform owner to send or copy.</summary>
    public async Task<IssuedConfirmation?> IssueForShopAsync(Guid businessId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var owner = await db.AdminUsers.Where(u => u.BusinessId == businessId && u.Role == "OWNER").OrderBy(u => u.CreatedAtUtc).FirstOrDefaultAsync(ct);
        return owner is null ? null : await IssueAsync(db, owner, ct);
    }

    /// <summary>
    /// Permanently deletes a shop and everything in it: bookings, customers, staff, services, hours, closures, follow-ups,
    /// medical records, the audit log and the owner's account. It cannot be undone. The caller must type the shop's booking
    /// address as confirmation, and may not delete the shop they are signed in to.
    /// </summary>
    public async Task<ServiceResult<bool>> DeleteShopAsync(Guid businessId, string? typedSlug, Guid? actingBusinessId, CancellationToken ct = default)
    {
        if (businessId == actingBusinessId)
            return ServiceResult<bool>.Fail(FailureKind.PolicyViolation, Text.PlatformCannotDeleteOwn);

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var slug = await db.Businesses.AsNoTracking().Where(b => b.Id == businessId).Select(b => b.Slug).FirstOrDefaultAsync(ct);
        if (slug is null) return ServiceResult<bool>.Fail(FailureKind.NotFound, Text.NotFound);
        if (!string.Equals((typedSlug ?? "").Trim(), slug, StringComparison.OrdinalIgnoreCase))
            return ServiceResult<bool>.Invalid(new Dictionary<string, string> { ["confirm"] = Text.PlatformDeleteMismatch });

        // Children first, one transaction: either the whole shop goes or nothing does.
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await db.AuditLogs.Where(x => x.BusinessId == businessId).ExecuteDeleteAsync(ct);
        await db.Bookings.Where(x => x.BusinessId == businessId).ExecuteDeleteAsync(ct);
        await db.Customers.Where(x => x.BusinessId == businessId).ExecuteDeleteAsync(ct);
        await db.WeeklyClosures.Where(x => x.BusinessId == businessId).ExecuteDeleteAsync(ct);
        await db.Blackouts.Where(x => x.BusinessId == businessId).ExecuteDeleteAsync(ct);
        await db.BusinessHours.Where(x => x.BusinessId == businessId).ExecuteDeleteAsync(ct);
        await db.StaffServices.Where(x => x.BusinessId == businessId).ExecuteDeleteAsync(ct);
        await db.StaffMembers.Where(x => x.BusinessId == businessId).ExecuteDeleteAsync(ct); // working hours go with them
        await db.Services.Where(x => x.BusinessId == businessId).ExecuteDeleteAsync(ct);
        await db.AdminUsers.Where(x => x.BusinessId == businessId).ExecuteDeleteAsync(ct);
        await db.Businesses.Where(x => x.Id == businessId).ExecuteDeleteAsync(ct);
        await tx.CommitAsync(ct);
        return ServiceResult<bool>.Ok(true);
    }

    public async Task<bool> IsEnabledAsync(Guid businessId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.Businesses.AsNoTracking().Where(b => b.Id == businessId).Select(b => b.IsEnabled).FirstOrDefaultAsync(ct);
    }
}
