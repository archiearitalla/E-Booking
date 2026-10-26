using VeskaFlow.Core.Security;
using VeskaFlow.Web.Data;
using Microsoft.EntityFrameworkCore;

namespace VeskaFlow.Web.Services;

/// <summary>What a signed-in person may do in the back office. Staff run the day; managers also set up the shop; owners do everything.</summary>
public enum AdminLevel { Staff = 0, Manager = 1, Owner = 2 }

/// <summary>Marks a back-office page as needing at least this level. Without it, any signed-in team member may open the page.</summary>
[AttributeUsage(AttributeTargets.Class)]
public sealed class AdminAccessAttribute(AdminLevel minimum) : Attribute
{
    public AdminLevel Minimum { get; } = minimum;
}

public static class AdminRoles
{
    public const string Owner = "OWNER";
    public const string Manager = "MANAGER";
    public const string Staff = "STAFF";

    public static AdminLevel LevelOf(string? role) => role switch
    {
        Owner => AdminLevel.Owner,
        Manager => AdminLevel.Manager,
        _ => AdminLevel.Staff,
    };

    /// <summary>Request item set by back-office pages with the level read from the database.</summary>
    public const string LevelItemKey = "admin_level";
}

public sealed record TeamMember(Guid Id, string DisplayName, string Email, string Role, bool IsActive, DateTime CreatedAtUtc);

public sealed record TeamAccess(string Role, bool IsActive);

/// <summary>
/// The people who can sign in to one shop's back office. The owner adds them with a password to hand over; they can be
/// switched off at any time. Every query is scoped by the caller's business id.
/// </summary>
public sealed class TeamService(IDbContextFactory<AppDbContext> dbFactory, TimeProvider time)
{
    public const int MaxMembers = 20;

    /// <summary>The person's current role and state from the database (a cookie can be older than a change). Null when not in that shop.</summary>
    public async Task<TeamAccess?> GetAccessAsync(Guid userId, Guid businessId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.AdminUsers.AsNoTracking().Where(u => u.Id == userId && u.BusinessId == businessId)
            .Select(u => new TeamAccess(u.Role, u.IsActive)).FirstOrDefaultAsync(ct);
    }

    public async Task<IReadOnlyList<TeamMember>> ListAsync(Guid businessId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var rows = await db.AdminUsers.AsNoTracking().Where(u => u.BusinessId == businessId)
            .Select(u => new TeamMember(u.Id, u.DisplayName, u.Email, u.Role, u.IsActive, u.CreatedAtUtc)).ToListAsync(ct);
        return rows.OrderBy(r => AdminRoles.LevelOf(r.Role) == AdminLevel.Owner ? 0 : 1).ThenBy(r => r.CreatedAtUtc).ToList();
    }

    /// <summary>Adds a manager or staff member who can sign in straight away (the owner vouches for the email address).</summary>
    public async Task<ServiceResult<TeamMember>> AddAsync(
        Guid businessId, string? name, string? email, string? role, string? password, CancellationToken ct = default)
    {
        var errors = new Dictionary<string, string>();
        var cleanName = (name ?? "").Trim();
        if (cleanName.Length is < 1 or > 80) errors["name"] = Text.NameInvalid;
        var cleanEmail = (email ?? "").Trim();
        if (cleanEmail.Length is < 3 or > 120 || !cleanEmail.Contains('@') || cleanEmail.Contains(' ')) errors["email"] = Text.EmailInvalid;
        if (role is not (AdminRoles.Manager or AdminRoles.Staff)) errors["role"] = Text.TeamRoleInvalid;
        var pw = password ?? "";
        if (pw.Length is < 8 or > 128) errors["password"] = Text.PasswordInvalid;
        if (errors.Count > 0) return ServiceResult<TeamMember>.Invalid(errors);

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        if (await db.AdminUsers.CountAsync(u => u.BusinessId == businessId, ct) >= MaxMembers)
            return ServiceResult<TeamMember>.Fail(FailureKind.PolicyViolation, Text.TeamFull);
        if (await db.AdminUsers.AnyAsync(u => u.Email.ToLower() == cleanEmail.ToLower(), ct))
            return ServiceResult<TeamMember>.Invalid(new Dictionary<string, string> { ["email"] = Text.EmailTaken });

        var now = time.GetUtcNow().UtcDateTime;
        var user = new AdminUser
        {
            BusinessId = businessId,
            Email = cleanEmail,
            DisplayName = cleanName,
            PasswordHash = PasswordHasher.Hash(pw),
            Role = role!,
            CreatedAtUtc = now,
            EmailConfirmedAtUtc = now,
        };
        db.AdminUsers.Add(user);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            // Two people added the same email at once: the unique index decided.
            return ServiceResult<TeamMember>.Invalid(new Dictionary<string, string> { ["email"] = Text.EmailTaken });
        }
        return ServiceResult<TeamMember>.Ok(new TeamMember(user.Id, user.DisplayName, user.Email, user.Role, true, user.CreatedAtUtc));
    }

    /// <summary>Switches a manager or staff member on or off. The owner can never be switched off.</summary>
    public async Task<ServiceResult<bool>> SetActiveAsync(Guid businessId, Guid userId, bool active, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var user = await db.AdminUsers.FirstOrDefaultAsync(u => u.Id == userId && u.BusinessId == businessId, ct);
        if (user is null) return ServiceResult<bool>.Fail(FailureKind.NotFound, Text.NotFound);
        if (user.Role == AdminRoles.Owner) return ServiceResult<bool>.Fail(FailureKind.PolicyViolation, Text.TeamOwnerLocked);
        user.IsActive = active;
        await db.SaveChangesAsync(ct);
        return ServiceResult<bool>.Ok(active);
    }

    /// <summary>The owner sets a new password for a manager or staff member (for example when it was forgotten).</summary>
    public async Task<ServiceResult<bool>> SetPasswordAsync(Guid businessId, Guid userId, string? password, CancellationToken ct = default)
    {
        var pw = password ?? "";
        if (pw.Length is < 8 or > 128)
            return ServiceResult<bool>.Invalid(new Dictionary<string, string> { ["password"] = Text.PasswordInvalid });
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var user = await db.AdminUsers.FirstOrDefaultAsync(u => u.Id == userId && u.BusinessId == businessId, ct);
        if (user is null) return ServiceResult<bool>.Fail(FailureKind.NotFound, Text.NotFound);
        if (user.Role == AdminRoles.Owner) return ServiceResult<bool>.Fail(FailureKind.PolicyViolation, Text.TeamOwnerLocked);
        user.PasswordHash = PasswordHasher.Hash(pw);
        await db.SaveChangesAsync(ct);
        return ServiceResult<bool>.Ok(true);
    }
}
