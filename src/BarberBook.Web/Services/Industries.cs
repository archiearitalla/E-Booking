namespace BarberBook.Web.Services;

/// <summary>
/// The kinds of business a shop can pick at sign-up (BUSINESS_RULES section 18). The industry only chooses
/// wording and the starter service; it never changes the booking rules.
/// </summary>
public static class Industries
{
    public const string Default = "barbershop";

    public static readonly IReadOnlyList<(string Key, string Label, string StarterService)> All =
    [
        ("barbershop", Text.IndustryBarbershop, Text.DefaultServiceName),
        ("salon", Text.IndustrySalon, Text.DefaultServiceSalon),
        ("clinic", Text.IndustryClinic, Text.DefaultServiceClinic),
    ];

    public static bool IsValid(string? key) => key is not null && All.Any(i => i.Key == key);

    public static string StarterService(string key) => All.First(i => i.Key == key).StarterService;
}
