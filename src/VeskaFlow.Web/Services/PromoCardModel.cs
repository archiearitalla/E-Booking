namespace VeskaFlow.Web.Services;

/// <summary>What the promo card shows: the mode, the headline, the optional sub-line and badge, the picture address (null for a words-only banner) and the last day as text. <c>Live</c> is the creator's preview: every part is rendered so the page script can fill it.</summary>
public sealed record PromoCardModel(string Theme, string Headline, string? Details, string? Badge, string? ImageUrl, string? Until, bool Live = false);
