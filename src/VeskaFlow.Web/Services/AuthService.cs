using System.Security.Claims;
using VeskaFlow.Core.Security;
using VeskaFlow.Web.Data;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.EntityFrameworkCore;

namespace VeskaFlow.Web.Services;

public sealed record SignedInUser(Guid AdminUserId, Guid BusinessId, string Role, string DisplayName, DateTime? TrialEndsAtUtc = null);

public sealed class AuthService(IDbContextFactory<AppDbContext> dbFactory)
{
    // Unknown emails are verified against this hash so they take the same code path (and time) as a wrong password.
    private static readonly Lazy<string> DummyHash = new(() => PasswordHasher.Hash("not-a-real-password"));

    private const int MaxPasswordLength = 256;

    public async Task<ServiceResult<SignedInUser>> SignInAsync(string? email, string? password, CancellationToken ct = default)
    {
        var normalized = (email ?? "").Trim();
        var candidate = string.IsNullOrEmpty(password) ? "\0" : password.Length > MaxPasswordLength ? password[..MaxPasswordLength] : password;

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var user = normalized.Length is >= 1 and <= 120
            ? await db.AdminUsers.AsNoTracking().FirstOrDefaultAsync(u => u.Email.ToLower() == normalized.ToLower(), ct)
            : null;

        var passwordOk = PasswordHasher.Verify(candidate, user?.PasswordHash ?? DummyHash.Value);
        if (user is null || !user.IsActive || !passwordOk || string.IsNullOrEmpty(password))
            return ServiceResult<SignedInUser>.Fail(FailureKind.Validation, Text.LoginFailed);

        var trialEnds = await db.Businesses.AsNoTracking().Where(b => b.Id == user.BusinessId)
            .Select(b => b.TrialEndsAtUtc).FirstOrDefaultAsync(ct);
        return ServiceResult<SignedInUser>.Ok(new SignedInUser(user.Id, user.BusinessId, user.Role, user.DisplayName, trialEnds));
    }

    /// <summary>The cookie identity. The business id here is the only source of tenant scoping in the back office.</summary>
    public static ClaimsPrincipal CreatePrincipal(
        Guid adminUserId, Guid businessId, string role, string displayName, DateTime? trialEndsAtUtc = null)
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, adminUserId.ToString()),
            new(UserClaims.BusinessIdType, businessId.ToString()),
            new(ClaimTypes.Role, role),
            new(ClaimTypes.Name, displayName),
        };
        // Display only (the trial banner). Enforcement happens on the server from the database value.
        if (trialEndsAtUtc is { } ends)
            claims.Add(new Claim(UserClaims.TrialEndsType, DateTime.SpecifyKind(ends, DateTimeKind.Utc).ToString("O")));
        return new ClaimsPrincipal(new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme));
    }

    public static ClaimsPrincipal CreatePrincipal(SignedInUser user) =>
        CreatePrincipal(user.AdminUserId, user.BusinessId, user.Role, user.DisplayName, user.TrialEndsAtUtc);

    public static ClaimsPrincipal CreatePrincipal(RegisteredOwner owner) =>
        CreatePrincipal(owner.AdminUserId, owner.BusinessId, owner.Role, owner.DisplayName, owner.TrialEndsAtUtc);
}
