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
    string? Industry = null, int TrialDays = 0, bool RequireEmailConfirmation = false, bool RequireApproval = false);

/// <summary>
/// <c>ConfirmationToken</c> is set only when the owner still has to confirm the email address: it goes into the email link
/// and is never stored. <c>Enabled</c> is false when the platform owner must approve the shop first.
/// </summary>
public sealed record RegisteredOwner(
    Guid BusinessId, Guid AdminUserId, string Slug, string DisplayName, string Role, DateTime? TrialEndsAtUtc = null,
    string? ConfirmationToken = null, string Email = "", bool Enabled = true);

/// <summary>
/// Creates a shop: business, owner, one staff member, one service and Mon-Sat 09:00-18:00 hours, all in one
/// transaction (one SaveChanges). The FREE plan allows 1 staff member, which this starter data respects.
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

        // Lenient on purpose: whatever was typed is turned into a valid address (capitals to small letters, spaces and
        // symbols to single hyphens, cut at 40). Only a minimum length is enforced, because the page lives at /b/<address>.
        var slug = Slug.From(request.Slug ?? "");
        if (slug.Length == 0) slug = Slug.From(businessName);
        if (!Slug.IsValid(slug)) errors["slug"] = Text.SlugInvalid;

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

        await using var db = await dbFactory.CreateDbContextAsync(ct);

        // Friendly pre-checks; the unique indexes below remain the real guard against races.
        if (await db.Businesses.AnyAsync(b => b.Slug == slug, ct)) errors["slug"] = Text.SlugTaken;
        if (await db.AdminUsers.AnyAsync(u => u.Email.ToLower() == email.ToLower(), ct)) errors["email"] = Text.EmailTaken;
        if (errors.Count > 0) return ServiceResult<RegisteredOwner>.Invalid(errors);

        var business = new Business
        {
            Slug = slug, Name = businessName, TimeZone = timeZone, Industry = industry,
            TrialEndsAtUtc = request.TrialDays > 0 ? clock.GetUtcNow().UtcDateTime.AddDays(request.TrialDays) : null,
            IsEnabled = !request.RequireApproval, // when approval is required the platform owner switches the shop on
        };
        var now = clock.GetUtcNow().UtcDateTime;
        var owner = new AdminUser
        {
            BusinessId = business.Id,
            Email = email,
            DisplayName = ownerName,
            PasswordHash = PasswordHasher.Hash(password),
            Role = "OWNER",
            // Not required: confirmed at once. Required: no sign-in until the link in the email is opened.
            EmailConfirmedAtUtc = request.RequireEmailConfirmation ? null : now,
        };
        string? confirmationToken = null;
        if (request.RequireEmailConfirmation)
        {
            var (token, hash) = EmailTokens.NewToken();
            confirmationToken = token;
            owner.EmailTokenHash = hash;
            owner.EmailTokenExpiresUtc = now.Add(EmailTokens.Lifetime);
        }
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
        }
        catch (Exception ex) when (FindPostgres(ex) is { SqlState: UniqueViolation } pg)
        {
            // Lost a race with another registration for the same slug or email.
            var field = pg.ConstraintName == "ux_admin_user_email" ? "email" : "slug";
            return ServiceResult<RegisteredOwner>.Invalid(new Dictionary<string, string>
            {
                [field] = field == "email" ? Text.EmailTaken : Text.SlugTaken,
            });
        }

        return ServiceResult<RegisteredOwner>.Ok(new RegisteredOwner(
            business.Id, owner.Id, business.Slug, owner.DisplayName, owner.Role, business.TrialEndsAtUtc,
            confirmationToken, owner.Email, business.IsEnabled));
    }

    private static PostgresException? FindPostgres(Exception? ex)
    {
        for (; ex is not null; ex = ex.InnerException)
            if (ex is PostgresException pg) return pg;
        return null;
    }
}
