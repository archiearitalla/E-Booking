namespace VeskaFlow.Web.Services;

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

    /// <summary>The word the public page uses for the person being booked.</summary>
    public static string StaffLabel(string? key) => key switch
    {
        "salon" => Text.StaffLabelSalon,
        "clinic" => Text.StaffLabelClinic,
        _ => Text.BookStaff,
    };

    public static bool IsValid(string? key) => key is not null && All.Any(i => i.Key == key);

    public static string StarterService(string key) => All.First(i => i.Key == key).StarterService;

    public sealed record SuggestedService(string Name, int DurationMinutes);

    /// <summary>The usual services for each kind of shop. Prices stay 0 until the owner sets them.</summary>
    public static IReadOnlyList<SuggestedService> Suggested(string? key) => key switch
    {
        "salon" =>
        [
            new("Haircut and styling", 45), new("Hair color", 120), new("Rebond", 180), new("Hair treatment", 60),
            new("Manicure", 45), new("Pedicure", 60), new("Facial", 60), new("Massage", 60),
        ],
        "clinic" =>
        [
            new("Consultation", 30), new("Follow-up check-up", 20), new("Dental cleaning", 45), new("Tooth extraction", 45),
            new("Dental filling", 60), new("Braces adjustment", 30), new("Teeth whitening", 60), new("Medical certificate", 15),
        ],
        _ =>
        [
            new("Haircut", 30), new("Haircut and beard trim", 45), new("Beard trim", 20), new("Shave", 30),
            new("Kids haircut", 30), new("Hair color", 90), new("Hair wash", 15), new("Hair and scalp treatment", 45),
        ],
    };
}
