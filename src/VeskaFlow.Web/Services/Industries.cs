namespace VeskaFlow.Web.Services;

/// <summary>
/// The kinds of business a shop can pick at sign-up (BUSINESS_RULES section 18). The industry only chooses
/// wording and the starter service; it never changes the booking rules.
/// </summary>
public static class Industries
{
    public const string Default = "barbershop";

    public sealed record SuggestedService(string Name, int DurationMinutes, int? FollowUpDays = null);

    private sealed record Industry(string Key, string Label, string StarterService, string StaffLabel, IReadOnlyList<SuggestedService> Services);

    private static readonly IReadOnlyList<Industry> Kinds =
    [
        new("barbershop", Text.IndustryBarbershop, Text.DefaultServiceName, Text.BookStaff,
        [
            new("Haircut", 30), new("Haircut and beard trim", 45), new("Beard trim", 20), new("Shave", 30),
            new("Kids haircut", 30), new("Hair color", 90), new("Hair wash", 15), new("Hair and scalp treatment", 45),
        ]),
        new("salon", Text.IndustrySalon, Text.DefaultServiceSalon, Text.StaffLabelSalon,
        [
            new("Haircut and styling", 45), new("Hair color", 120), new("Rebond", 180), new("Hair treatment", 60),
            new("Manicure", 45), new("Pedicure", 60), new("Facial", 60), new("Massage", 60),
        ]),
        new("clinic", Text.IndustryClinic, Text.DefaultServiceClinic, Text.StaffLabelClinic,
        [
            new("Consultation", 30, 14), new("Follow-up check-up", 20), new("Dental cleaning", 45, 180), new("Tooth extraction", 45, 7),
            new("Dental filling", 60), new("Braces adjustment", 30, 30), new("Teeth whitening", 60), new("Medical certificate", 15),
        ]),
        new("spa", Text.IndustrySpa, Text.DefaultServiceSpa, Text.StaffLabelSpa,
        [
            new("Swedish massage", 60), new("Shiatsu massage", 60), new("Hot stone massage", 90), new("Back and shoulder massage", 30),
            new("Foot reflexology", 45), new("Body scrub", 60), new("Facial", 60), new("Aromatherapy", 60),
        ]),
        new("nails", Text.IndustryNails, Text.DefaultServiceNails, Text.StaffLabelNails,
        [
            new("Manicure", 45), new("Pedicure", 60), new("Gel polish", 60), new("Nail extensions", 90),
            new("Nail art", 30), new("Eyelash extensions", 90), new("Lash lift", 60), new("Eyebrow shaping", 30),
        ]),
        new("pet", Text.IndustryPet, Text.DefaultServicePet, Text.StaffLabelPet,
        [
            new("Bath and blow dry", 60), new("Full grooming", 90), new("Nail trim", 15), new("Ear cleaning", 15),
            new("Vet consultation", 30, 14), new("Vaccination", 20, 21), new("Deworming", 15, 90), new("Pet check-up", 30),
        ]),
        new("fitness", Text.IndustryFitness, Text.DefaultServiceFitness, Text.StaffLabelFitness,
        [
            new("Personal training", 60), new("Group class", 60), new("Yoga class", 60), new("Dance fitness class", 60),
            new("Fitness assessment", 45), new("Nutrition consultation", 30, 14), new("Trial session", 30), new("Stretching and recovery", 30),
        ]),
        new("tattoo", Text.IndustryTattoo, Text.DefaultServiceTattoo, Text.StaffLabelTattoo,
        [
            new("Design consultation", 30), new("Small tattoo", 60, 30), new("Medium tattoo", 180, 30), new("Large tattoo session", 240, 30),
            new("Piercing", 30, 14), new("Touch-up", 60), new("Tattoo cover-up consultation", 30), new("Tattoo removal consultation", 30),
        ]),
        new("auto", Text.IndustryAuto, Text.DefaultServiceAuto, Text.StaffLabelAuto,
        [
            new("Car wash", 30), new("Interior cleaning", 60), new("Car detailing", 180), new("Motorcycle wash", 20),
            new("Oil change", 45, 90), new("Tire rotation", 30), new("Aircon cleaning", 60), new("General check-up", 45),
        ]),
        new("tutor", Text.IndustryTutor, Text.DefaultServiceTutor, Text.StaffLabelTutor,
        [
            new("One-hour lesson", 60), new("Trial lesson", 30), new("Review session", 90), new("Music lesson", 45),
            new("Language lesson", 60), new("Exam coaching", 90), new("Parent meeting", 20), new("Assessment", 45),
        ]),
        new("photo", Text.IndustryPhoto, Text.DefaultServicePhoto, Text.StaffLabelPhoto,
        [
            new("Portrait session", 60), new("Family photoshoot", 90), new("ID and passport photos", 15), new("Graduation photos", 60),
            new("Product photography", 120), new("Couple or prenup shoot", 120), new("Event coverage consultation", 30), new("Photo editing review", 30),
        ]),
    ];

    public static readonly IReadOnlyList<(string Key, string Label, string StarterService)> All =
        Kinds.Select(k => (k.Key, k.Label, k.StarterService)).ToList();

    private static Industry Find(string? key) => Kinds.FirstOrDefault(k => k.Key == key) ?? Kinds[0];

    /// <summary>The word the public page uses for the person being booked.</summary>
    public static string StaffLabel(string? key) => Find(key).StaffLabel;

    public static bool IsValid(string? key) => key is not null && Kinds.Any(i => i.Key == key);

    public static string StarterService(string key) => Kinds.First(i => i.Key == key).StarterService;

    /// <summary>The usual services for each kind of shop. Prices stay 0 until the owner sets them.</summary>
    public static IReadOnlyList<SuggestedService> Suggested(string? key) => Find(key).Services;
}
