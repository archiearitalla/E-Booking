namespace VeskaFlow.Web.Services;

/// <summary>One thing of a shop that is ending soon or has just ended: its access, or an add-on.</summary>
public sealed record ExpiryItem(Guid BusinessId, string Shop, string Slug, string What, DateTime EndUtc, int DaysLeft);

/// <summary>
/// What the platform owner is told about ending subscriptions: a shop's access (trial or paid period) and its AI and email add-ons.
/// Something is "due" from <see cref="WarnDays"/> days before its end until <see cref="RecentDays"/> days after it, so a client who
/// has just lapsed is still on the list until the platform owner has acted. Shops that are switched off are left out.
/// </summary>
public static class ShopExpiry
{
    public const int WarnDays = 7;
    public const int RecentDays = 14;

    /// <summary>Whole days from now to the end; negative once it has passed. An end later today is 0.</summary>
    public static int DaysLeft(DateTime endUtc, DateTime nowUtc) =>
        (int)Math.Floor((DateTime.SpecifyKind(endUtc, DateTimeKind.Utc) - nowUtc).TotalDays);

    public static bool IsDue(DateTime? endUtc, DateTime nowUtc) =>
        endUtc is { } e && DaysLeft(e, nowUtc) <= WarnDays && DaysLeft(e, nowUtc) >= -RecentDays;

    /// <summary>The access end and the add-on ends of one shop, each with what it is.</summary>
    public static IEnumerable<ExpiryItem> All(ShopRow s, DateTime nowUtc)
    {
        if (s.TrialEndsAtUtc is { } a) yield return Make(s, Text.ExpiryWhatAccess, a, nowUtc);
        if (s.AiAddonUntilUtc is { } ai) yield return Make(s, Text.PlatformAiAddon, ai, nowUtc);
        if (s.EmailAddonUntilUtc is { } em) yield return Make(s, Text.PlatformEmailAddon, em, nowUtc);
    }

    /// <summary>What needs the platform owner's attention for this shop (empty for a switched-off shop).</summary>
    public static IReadOnlyList<ExpiryItem> Due(ShopRow s, DateTime nowUtc) =>
        !s.Enabled ? [] : All(s, nowUtc).Where(i => IsDue(i.EndUtc, nowUtc)).OrderBy(i => i.EndUtc).ToList();

    private static ExpiryItem Make(ShopRow s, string what, DateTime end, DateTime now) =>
        new(s.BusinessId, s.Name, s.Slug, what, DateTime.SpecifyKind(end, DateTimeKind.Utc), DaysLeft(end, now));
}
