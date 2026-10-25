using System.Security.Claims;

namespace VeskaFlow.Web.Services;

/// <summary>
/// The signed-in user's tenant and identity. Back-office code takes the business id from here, never from the request.
/// </summary>
public static class UserClaims
{
    public const string BusinessIdType = "business_id";
    public const string TrialEndsType = "trial_ends_utc";

    /// <summary>Request item set by back-office pages with the trial end read from the database; the layout prefers it to the cookie.</summary>
    public const string TrialEndsItemKey = "trial_ends_from_db";

    /// <summary>The trial end for the header bell: the database value when a back-office page loaded it, otherwise the cookie.</summary>
    public static DateTime? TrialEndsForBell(this ClaimsPrincipal user, IDictionary<object, object?> items) =>
        items.TryGetValue(TrialEndsItemKey, out var fromDb) ? fromDb as DateTime? : user.TrialEndsUtc();

    /// <summary>When the trial ends, from the cookie. For the banner only; null when there is no trial.</summary>
    public static DateTime? TrialEndsUtc(this ClaimsPrincipal user) =>
        DateTime.TryParse(user.FindFirstValue(TrialEndsType), null,
            System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal, out var d) ? d : null;

    public static Guid? BusinessId(this ClaimsPrincipal user) =>
        Guid.TryParse(user.FindFirstValue(BusinessIdType), out var id) ? id : null;

    public static Guid? UserId(this ClaimsPrincipal user) =>
        Guid.TryParse(user.FindFirstValue(ClaimTypes.NameIdentifier), out var id) ? id : null;

    public static string? Role(this ClaimsPrincipal user) => user.FindFirstValue(ClaimTypes.Role);

    public static string? DisplayName(this ClaimsPrincipal user) => user.FindFirstValue(ClaimTypes.Name);
}
