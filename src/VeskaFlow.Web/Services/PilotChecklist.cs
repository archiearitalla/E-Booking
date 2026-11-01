namespace VeskaFlow.Web.Services;

/// <summary>One thing to do. <c>Id</c> is stable (it is what is saved when the platform owner ticks it); the words can be edited freely.</summary>
public sealed record PilotItem(string Id, string Title, string? Detail = null);

public sealed record PilotSection(string Id, string Title, string Intro, IReadOnlyList<PilotItem> Items, bool Tickable = true);

/// <summary>
/// The platform owner's checklist for the pilot (BUSINESS_RULES 19): what to finish before charging, how to set up a pilot shop, what to ask the
/// owner after two weeks, and what to watch every week. Only the platform owner sees it. English, like every other text.
/// </summary>
public static class PilotChecklist
{
    private const string Prefix = "pilot:";

    public static string Key(string itemId) => Prefix + itemId;

    public static readonly IReadOnlyList<PilotSection> Sections =
    [
        new("before", "Before you charge anyone",
            "A pilot with 3 to 5 shops you know can start now, free or at a discount. Finish these before you take real money.",
            [
                new("phone", "Test the main flows on a real phone", "Book as a customer, sign in as the owner, mark a booking done, Record a sale, Billing with the GCash QR, and install the app to the home screen. Try Android and an iPhone if you can."),
                new("email", "Test the real emails", "Book with your own Gmail. Check that the confirmation, the reminder and the confirm link arrive, and whether they land in the inbox or in spam. Ask Brevo to verify your sending domain if they go to spam."),
                new("legal", "Have a lawyer read the Privacy Notice and Terms", "The questions are in docs/LEGAL_BRIEF.md. Ask about the Data Privacy Act and whether you must register with the NPC (clinics keep medical notes)."),
                new("hosting", "Move to paid, always-on hosting", "The free Render instance sleeps and every deploy cuts the site for about a minute. Check the database size limit on the free Neon plan too."),
                new("restore", "Practise restoring a backup once", "docs/BACKUP.md. A backup you have never restored is only a hope."),
                new("support", "Decide how you give support", "How owners reach you (Messenger), when you answer, and what you do when something breaks on a Saturday night."),
                new("prices", "Set the real prices in Render", "Pricing__ProMonthly, Pricing__ProYearly, Pricing__ExtraStaffMonthly, Billing__Price, Billing__ExtraStaffPrice, Billing__PayTo, Trial__ContactUrl. Make the About page match."),
                new("email-service", "Check the email service and its daily limit", "Brevo free is 300 emails a day for everyone. Watch the count on the Platform page and upgrade the plan (and raise Email__DailyLimit) before shops reach it."),
            ]),
        new("setup", "Setting up a pilot shop (about 30 minutes, with the owner)",
            "Do this with the owner, in the shop or on a call, so you see where they get stuck.",
            [
                new("s-signup", "Sign up the shop", "Pick the right kind of shop, so the wording and the suggested services fit."),
                new("s-services", "Add the services, prices and times", "Use Add suggested services, then change them to the shop's real list. Set the deposit only if the shop wants one."),
                new("s-staff", "Add the staff and their hours", "Up to 5 are included. Check each person's hours and the shop's opening hours."),
                new("s-closures", "Add closed days and breaks", "For example every Sunday, holidays and the lunch break."),
                new("s-settings", "Check the shop settings", "Cancel cutoff, booking notice, how far ahead customers can book, reminder hours, and where customers send a deposit."),
                new("s-link", "Put the booking link and the QR code where customers see them", "Facebook page, Messenger auto-reply, the door and the counter. Print the QR from Settings."),
                new("s-test", "Make a test booking together", "The owner books as a customer, then handles it in Bookings: mark it done, then cancel a second one."),
                new("s-team", "Add the people who will use it", "Team page: a staff login for each barber if they should only see their own bookings."),
                new("s-pay", "Agree on the pilot terms in writing", "The price after the pilot, what happens to their data if they stop, and how to reach you."),
            ]),
        new("ask", "After two weeks: ask the owner",
            "Ask these in a call or in Messenger, and write down the answers word for word. These answers matter more than any guess.",
            [
                new("q-use", "How many bookings came through VeskaFlow, and how many did you still take by message or by phone?", "If most still come by message, find out why."),
                new("q-stuck", "What was the most confusing thing in the first week?"),
                new("q-noshow", "Did you have fewer no-shows or empty slots? How many?"),
                new("q-time", "How much time does it save you in a day, or does it cost you time?"),
                new("q-customers", "What did your customers say? Did any of them find it hard to book?"),
                new("q-missing", "What do you miss, or what did you ask for that was not there?"),
                new("q-price", "If it costs PHP 999 a month, would you keep it? What would be a price that feels fair? Too cheap to trust?"),
                new("q-recommend", "Would you recommend it to another shop owner? Who?"),
            ], Tickable: false),
        new("watch", "What to watch every week",
            "Five minutes on Monday morning.",
            [
                new("w-incidents", "Is the site up? Check the UptimeRobot dashboard", "Look for red Down marks and how long they lasted."),
                new("w-backup", "Did last night's backup run and was the restore check green?", "GitHub, Actions, backup."),
                new("w-emails", "How many emails went out yesterday?", "Platform page. Near 250 a day means it is time to upgrade the email plan."),
                new("w-payments", "Are any payments waiting to be confirmed?", "Platform, Payments."),
                new("w-expiring", "Which shops end their trial or access in the next 7 days?", "Message them before the reminder email does."),
                new("w-errors", "Any errors in the Render logs?", "Look for exceptions and migration messages after each deploy."),
                new("w-feedback", "Did any owner send a complaint or an idea?", "Write it down. Three owners asking for the same thing is a feature."),
            ]),
    ];

    public static IEnumerable<PilotItem> TickableItems => Sections.Where(s => s.Tickable).SelectMany(s => s.Items);

    public static bool IsKnown(string? itemId) => itemId is not null && TickableItems.Any(i => i.Id == itemId);
}
