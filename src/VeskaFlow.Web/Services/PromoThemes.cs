namespace VeskaFlow.Web.Services;

/// <summary>
/// The looks a promo banner can have (BUSINESS_RULES 6.y18). A closed list, like the shop colours: the page styles each key with
/// its own colours and icon, so an owner picks a mode and never has to design anything, and nothing typed can reach the styles.
/// </summary>
public static class PromoThemes
{
    public const string Default = "plain";

    public static readonly IReadOnlyList<string> All =
        ["plain", "halloween", "christmas", "new_year", "valentines", "summer", "back_to_school", "payday", "rainy"];

    public static bool IsValid(string? key) => key is not null && All.Contains(key);

    public static string Normalize(string? key) => IsValid(key) ? key! : Default;

    /// <summary>The name shown when choosing.</summary>
    public static string Label(string key) => key switch
    {
        "halloween" => Text.PromoThemeHalloween, "christmas" => Text.PromoThemeChristmas, "new_year" => Text.PromoThemeNewYear,
        "valentines" => Text.PromoThemeValentines, "summer" => Text.PromoThemeSummer, "back_to_school" => Text.PromoThemeSchool,
        "payday" => Text.PromoThemePayday, "rainy" => Text.PromoThemeRainy, _ => Text.PromoThemePlain,
    };

    /// <summary>The small line above the headline on the banner.</summary>
    public static string Kicker(string key) => key switch
    {
        "halloween" => Text.PromoKickerHalloween, "christmas" => Text.PromoKickerChristmas, "new_year" => Text.PromoKickerNewYear,
        "valentines" => Text.PromoKickerValentines, "summer" => Text.PromoKickerSummer, "back_to_school" => Text.PromoKickerSchool,
        "payday" => Text.PromoKickerPayday, "rainy" => Text.PromoKickerRainy, _ => Text.PromoKickerPlain,
    };
}
