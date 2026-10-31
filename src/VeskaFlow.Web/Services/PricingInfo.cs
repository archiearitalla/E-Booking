namespace VeskaFlow.Web.Services;

/// <summary>What a client can buy: the access to the system (PRO) and the two add-ons.</summary>
public static class PaymentKinds
{
    public const string Pro = "PRO", Ai = "AI", Email = "EMAIL";
    public const string Month = "MONTH", Year = "YEAR";
    public static bool IsKind(string? k) => k is Pro or Ai or Email;
    public static int Days(string period) => period == Year ? 365 : 30;
    public static string Label(string kind) => kind switch { Pro => Text.PayKindPro, Ai => Text.PlatformAiAddon, Email => Text.PlatformEmailAddon, _ => kind };
}

/// <summary>One thing for sale: what it is, how long it lasts, and its price.</summary>
public sealed record PriceOption(string Kind, string Period, decimal Price)
{
    public string Key => $"{Kind}-{Period}";
    public int Days => PaymentKinds.Days(Period);
}

/// <summary>
/// The prices, from settings (never in the code, so they can change without a release): <c>Pricing:ProMonthly</c> (default 499) and <c>Pricing:ProYearly</c>
/// (default 4990, two months free), and the add-ons <c>Pricing:AiMonthly</c> and <c>Pricing:EmailMonthly</c>. The AI and email prices are placeholders until the
/// real cost is known. A price of 0 or less means "not for sale".
/// </summary>
public sealed class PricingInfo(IConfiguration config)
{
    public decimal ProMonthly => config.GetValue("Pricing:ProMonthly", 499m);
    public decimal ProYearly => config.GetValue("Pricing:ProYearly", 4990m);
    public decimal AiMonthly => config.GetValue("Pricing:AiMonthly", 1500m);
    public decimal EmailMonthly => config.GetValue("Pricing:EmailMonthly", 199m);

    /// <summary>What a client can pay for, with the price of each.</summary>
    public IReadOnlyList<PriceOption> Options
    {
        get
        {
            var list = new List<PriceOption>
            {
                new(PaymentKinds.Pro, PaymentKinds.Month, ProMonthly),
                new(PaymentKinds.Pro, PaymentKinds.Year, ProYearly),
                new(PaymentKinds.Ai, PaymentKinds.Month, AiMonthly),
                new(PaymentKinds.Email, PaymentKinds.Month, EmailMonthly),
            };
            return list.Where(o => o.Price > 0).ToList();
        }
    }

    public PriceOption? Find(string? kind, string? period) => Options.FirstOrDefault(o => o.Kind == kind && o.Period == period);

    /// <summary>What a year saves against twelve months, or 0 when there is no saving.</summary>
    public decimal YearlySaving => Math.Max(0, ProMonthly * 12 - ProYearly);
}
