using System.Net.Mail;
using VeskaFlow.Core.Domain;
using VeskaFlow.Core.Scheduling;
using VeskaFlow.Core.Security;
using VeskaFlow.Web.Data;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace VeskaFlow.Web.Services;

public sealed record RegisterRequest(
    string? BusinessName, string? Slug, string? OwnerName, string? Email, string? Password, string? TimeZone = null,
    string? Industry = null, int TrialDays = 0, bool RequireEmailConfirmation = false, bool RequireApproval = false,
    DateTime? TermsAcceptedAtUtc = null, string? TermsVersion = null);

/// <summary>
/// <c>ConfirmationToken</c> is set only when the owner still has to confirm the email address: it goes into the email link
/// and is never stored. <c>Enabled</c> is false when the platform owner must approve the shop first.
/// </summary>
public sealed record RegisteredOwner(
    Guid BusinessId, Guid AdminUserId, string Slug, string DisplayName, string Role, DateTime? TrialEndsAtUtc = null,
    string? ConfirmationToken = null, string Email = "", bool Enabled = true);

/// <summary>
/// Creates a shop: business, owner, one staff member, one service and Mon-Sat 09:00-18:00 hours, all in one
/// transaction (one SaveChanges). The FREE plan allows 1 staff member, which this starter data respects. Only the email must be
/// unique: shops and people may share names, and the booking address comes from the shop name with a short code added when taken.
/// </summary>
public sealed class OnboardingService(IDbContextFactory<AppDbContext> dbFactory, TimeProvider? time = null)
{
    private readonly TimeProvider clock = time ?? TimeProvider.System;

    private const string UniqueViolation = "23505";

    public async Task<ServiceResult<RegisteredOwner>> RegisterAsync(RegisterRequest request, CancellationToken ct = default)
    {
        var errors = new Dictionary<string, string>();

        var businessName = (request.BusinessName ?? "").Trim();
        if (businessName.Length is < 1 or > 80) errors["businessName"] = Text.BusinessNameInvalid;

        // The booking address comes from the shop name (or from what was typed, turned into a clean address: capitals to small letters,
        // spaces and symbols to single hyphens, cut at 40). It never fails: if it is too short it falls back to the shop name, and if
        // that is taken or too short a short code is added at the end (see PickSlugAsync). Only the email is unique among people.
        var wanted = Slug.From(request.Slug ?? "");
        if (!Slug.IsValid(wanted)) wanted = Slug.From(businessName);
        var baseSlug = wanted; // may be empty or too short: PickSlugAsync then uses a stem and a short code, so no one holds a bare "shop"

        var ownerName = (request.OwnerName ?? "").Trim();
        if (ownerName.Length is < 1 or > 80) errors["ownerName"] = Text.OwnerNameInvalid;

        var email = (request.Email ?? "").Trim();
        if (email.Length is < 1 or > 120 || !MailAddress.TryCreate(email, out var parsed) || parsed.Address != email)
            errors["email"] = Text.EmailInvalid;

        var password = request.Password ?? "";
        if (password.Length is < 8 or > 128) errors["password"] = Text.PasswordInvalid;

        var timeZone = string.IsNullOrWhiteSpace(request.TimeZone) ? "Asia/Manila" : request.TimeZone.Trim();
        if (!LocalTime.IsValidZone(timeZone)) errors["timeZone"] = Text.ValidationFailed;

        var industry = string.IsNullOrWhiteSpace(request.Industry) ? Industries.Default : request.Industry.Trim();
        if (!Industries.IsValid(industry)) errors["industry"] = Text.IndustryInvalid;

        if (errors.Count > 0) return ServiceResult<RegisteredOwner>.Invalid(errors);

        // The email is the one thing that must be unique (the unique index below is the real guard against a race).
        await using (var check = await dbFactory.CreateDbContextAsync(ct))
        {
            if (await check.AdminUsers.AnyAsync(u => u.Email.ToLower() == email.ToLower(), ct))
                return ServiceResult<RegisteredOwner>.Invalid(new Dictionary<string, string> { ["email"] = Text.EmailTaken });
        }

        var now = clock.GetUtcNow().UtcDateTime;
        var passwordHash = PasswordHasher.Hash(password); // once, not once per attempt
        string? confirmationToken = null;
        string? tokenHash = null;
        if (request.RequireEmailConfirmation)
        {
            (confirmationToken, tokenHash) = EmailTokens.NewToken();
        }

        // Two shops may have the same name, so two may want the same address. The one that loses a race for it simply tries again
        // with a new short code; a few attempts are far more than ever needed.
        Business business;
        AdminUser owner;
        for (var attempt = 0; ; attempt++)
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            var slug = await PickSlugAsync(db, baseSlug, forceCode: attempt > 0, ct);

            business = new Business
            {
                Slug = slug, Name = businessName, TimeZone = timeZone, Industry = industry,
                TrialEndsAtUtc = request.TrialDays > 0 ? now.AddDays(request.TrialDays) : null,
                IsEnabled = !request.RequireApproval, // when approval is required the platform owner switches the shop on
                TermsAcceptedAtUtc = request.TermsAcceptedAtUtc, TermsVersion = request.TermsVersion,
            };
            owner = new AdminUser
            {
                BusinessId = business.Id,
                Email = email,
                DisplayName = ownerName,
                PasswordHash = passwordHash,
                Role = "OWNER",
                // Not required: confirmed at once. Required: no sign-in until the link in the email is opened.
                EmailConfirmedAtUtc = request.RequireEmailConfirmation ? null : now,
                EmailTokenHash = tokenHash,
                EmailTokenExpiresUtc = request.RequireEmailConfirmation ? now.Add(EmailTokens.Lifetime) : null,
            };
            var service = new Service
            {
                BusinessId = business.Id, Name = Industries.StarterService(industry), DurationMinutes = 30, Price = 0,
            };
            // "Other" is always offered last, for a visit whose service is not in the list.
            var other = new Service
            {
                BusinessId = business.Id, Name = Text.OtherServiceName, DurationMinutes = 30, Price = 0, SortOrder = 1000, IsOther = true,
            };
            var staff = new Staff { BusinessId = business.Id, Name = ownerName };
            staff.StaffServices.Add(new StaffService { ServiceId = service.Id, BusinessId = business.Id });
            staff.StaffServices.Add(new StaffService { ServiceId = other.Id, BusinessId = business.Id });
            for (short day = 1; day <= 6; day++) // Monday to Saturday; the same hours are the shop's opening hours in Settings
            {
                staff.WorkingHours.Add(new WorkingHour { DayOfWeek = day, OpenTime = new TimeOnly(9, 0), CloseTime = new TimeOnly(18, 0) });
                db.Add(new BusinessHour { BusinessId = business.Id, DayOfWeek = day, OpenTime = new TimeOnly(9, 0), CloseTime = new TimeOnly(18, 0) });
            }

            db.AddRange(business, owner, service, other, staff);
            try
            {
                await db.SaveChangesAsync(ct);
                break;
            }
            catch (Exception ex) when (FindPostgres(ex) is { SqlState: UniqueViolation } pg)
            {
                // Lost a race with another registration: for the email that is the answer; for the address, try again with a code.
                if (pg.ConstraintName == "ux_admin_user_email")
                    return ServiceResult<RegisteredOwner>.Invalid(new Dictionary<string, string> { ["email"] = Text.EmailTaken });
                if (attempt >= MaxAttempts)
                    return ServiceResult<RegisteredOwner>.Invalid(new Dictionary<string, string> { ["slug"] = Text.SlugTaken });
            }
        }

        return ServiceResult<RegisteredOwner>.Ok(new RegisteredOwner(
            business.Id, owner.Id, business.Slug, owner.DisplayName, owner.Role, business.TrialEndsAtUtc,
            confirmationToken, owner.Email, business.IsEnabled));
    }

    private const int MaxAttempts = 8;

    /// <summary>Letters and numbers that cannot be mistaken for each other, for the short code at the end of an address.</summary>
    private const string CodeAlphabet = "abcdefghjkmnpqrstuvwxyz23456789";

    /// <summary>A booking address nobody uses yet: the wanted one when it is free (and long enough), otherwise it with a short code at the end.</summary>
    private static async Task<string> PickSlugAsync(AppDbContext db, string baseSlug, bool forceCode, CancellationToken ct)
    {
        if (!forceCode && Slug.IsValid(baseSlug) && !await db.Businesses.AnyAsync(b => b.Slug == baseSlug, ct)) return baseSlug;

        for (var length = 4; ; length = Math.Min(length + 1, 6))
        {
            var candidate = WithCode(baseSlug, NewCode(length));
            if (!await db.Businesses.AnyAsync(b => b.Slug == candidate, ct)) return candidate;
        }
    }

    /// <summary>
    /// The address with a short code at the end ("san-juan-salon-k7x2"). The wanted address is cut so that "-" and the code fit inside the 40
    /// characters an address may have, a cut never ends in a hyphen, and an empty or unusable one becomes "shop".
    /// </summary>
    public static string WithCode(string baseSlug, string code)
    {
        var room = Math.Max(1, 39 - code.Length);
        var stem = (baseSlug.Length > room ? baseSlug[..room] : baseSlug).TrimEnd('-');
        return $"{(stem.Length == 0 ? "shop" : stem)}-{code}";
    }

    private static string NewCode(int length) =>
        string.Create(length, 0, static (span, _) =>
        {
            for (var i = 0; i < span.Length; i++) span[i] = CodeAlphabet[System.Security.Cryptography.RandomNumberGenerator.GetInt32(CodeAlphabet.Length)];
        });

    private static PostgresException? FindPostgres(Exception? ex)
    {
        for (; ex is not null; ex = ex.InnerException)
            if (ex is PostgresException pg) return pg;
        return null;
    }
}
