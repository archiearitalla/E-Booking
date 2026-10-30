namespace VeskaFlow.Web.Services;

/// <summary>A price as the pages show it: what is paid, and, when a promo discount applied, the regular price it came from and the percentage.</summary>
public sealed record PriceLineModel(decimal Net, decimal? Regular = null, int? Percent = null)
{
    public bool HasPromo => Regular is not null && Percent is not null;
}
