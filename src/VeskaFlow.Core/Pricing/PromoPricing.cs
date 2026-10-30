namespace VeskaFlow.Core.Pricing;

/// <summary>
/// The arithmetic of a promo discount (BUSINESS_RULES 6.y18 and 10). Pure: the services decide <em>which</em> promo applies and
/// which service is in it; this only says whether a percentage is allowed, whether a promo is running on a day, and what the
/// price becomes.
/// </summary>
public static class PromoPricing
{
    public const int MinPercent = 1;
    public const int MaxPercent = 90;

    public static bool IsValidPercent(int percent) => percent is >= MinPercent and <= MaxPercent;

    /// <summary>
    /// The discount in force on a local day, or null. A promo that is switched off gives nothing; one with a last day gives its
    /// discount through the whole of that day and nothing after it. A percentage outside the allowed range gives nothing.
    /// </summary>
    public static int? ActivePercent(bool isActive, DateOnly? lastDay, int? percent, DateOnly today) =>
        isActive && percent is { } p && IsValidPercent(p) && (lastDay is null || today <= lastDay) ? p : null;

    /// <summary>
    /// The discount that applies to one service: the promo's, unless the service is the "Other" one (its price is not fixed), is left
    /// out of the promo, or is free. This is the single rule used by the booking page, the booking itself and the reports.
    /// </summary>
    public static int? PercentFor(bool isOther, bool inPromo, decimal regularPrice, int? activePercent) =>
        isOther || !inPromo || regularPrice <= 0 ? null : activePercent;

    /// <summary>
    /// The price after the discount, to the centavo (half a centavo rounds up). A price that is not above zero, or a percentage
    /// that is not allowed, is returned unchanged.
    /// </summary>
    public static decimal Net(decimal regular, int percent) =>
        regular <= 0 || !IsValidPercent(percent)
            ? regular
            : Math.Round(regular * (100 - percent) / 100m, 2, MidpointRounding.AwayFromZero);
}
