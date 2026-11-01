namespace VeskaFlow.Web.Services;

/// <summary>One thing to do. <c>Id</c> is stable (it is what is saved when the platform owner ticks it); the words can be edited freely. <c>Copy</c> is ready-made text to paste somewhere else.</summary>
public sealed record PilotItem(string Id, string Title, string? Detail = null, string? Copy = null);

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
        new("review", "Meta App Review (to open Messenger to the public)",
            "Until Meta approves the app, only people with a role on it can connect a Page or chat with the assistant. Do these in order. This list comes from general knowledge of Meta's process: check every step against what the Meta dashboard says today. Nothing has been submitted yet and approval is not guaranteed. The same guide, with more detail, is in docs/META_REVIEW.md.",
            [
                new("r-tech", "Check the Tech Provider requirement", "The dashboard of the Meta app shows a box 'Become a Tech Provider' and says it is needed to submit to App Review and request access to data from other businesses. Read what Meta asks for there first (it usually comes with Business Verification and an agreement)."),
                new("r-verify", "Verify the business", "Meta Business Settings, Security Center, Start verification, for the business portfolio you want the app to belong to. Meta usually asks for the legal name, address, phone, website and a document that shows them (for example a DTI or SEC registration, a business permit or a utility bill in the business name). Then set the portfolio in the app: App settings, Basic."),
                new("r-settings", "Fill in the app's settings (App settings, Basic)", "App icon (1024 x 1024), category, contact email. Privacy Policy URL: https://flow.veskacode.com/Privacy. Terms of Service URL: https://flow.veskacode.com/Terms. User data deletion: choose 'Data deletion instructions URL' and enter https://flow.veskacode.com/data-deletion. App domain: flow.veskacode.com. Under Facebook Login for Business, Settings, the Valid OAuth Redirect URI must be https://flow.veskacode.com/Admin/MessengerReturn."),
                new("r-demo", "Make the demo shop and the demo Page", "A demo shop on flow.veskacode.com with a few services, staff and hours, and its AI add-on switched on (Platform). A demo Facebook Page connected to it (Admin, Messenger). Write down for the reviewer: the shop sign-in email and a password made only for this review (change it afterwards), and the Page name. Add the reviewer's access as the Meta form explains."),
                new("r-video", "Record the screencast (about 3 minutes, in English, with captions)", "1) Show the VeskaFlow sign-in and Admin, Messenger. 2) Press 'Connect a Facebook Page', show Facebook's own login and permission screen, choose the demo Page, press 'Connect this Page'. 3) Open Messenger as a different person and write to the Page: 'What services do you have?', show the answer. 4) Ask it to book ('Book me a consultation tomorrow at 2pm', give a name and a number) and show the confirmation. 5) Show the booking in Bookings. 6) Press Disconnect, send another message and show that nothing answers. Record the real screens with the address bar visible. Never show a password, token or secret."),
                new("r-show", "Request pages_show_list", "Ask for Advanced Access and paste this as the explanation.",
                    "After a shop owner signs in to VeskaFlow and presses \"Connect a Facebook Page\", Facebook shows its own login and permission screen. We use pages_show_list to read the list of Pages the owner manages and show their names, so the owner can choose which Page to connect. We read the list once, during that step, and show only the names to the owner."),
                new("r-meta", "Request pages_manage_metadata", "Paste this as the explanation.",
                    "When the owner chooses a Page, we use pages_manage_metadata to subscribe our app to that Page's webhook (\"messages\" only) so that the messages people send to the Page reach our server. When the owner presses \"Disconnect\", we remove the subscription. We do not change any other setting of the Page."),
                new("r-read", "Request pages_read_engagement", "Paste this as the explanation.",
                    "When the owner chooses a Page, we read that Page's name and the access token Facebook issues for it (the Page endpoint needs pages_read_engagement for this, as Meta's own error message states). The name is shown to the owner on the connection screen; the token is stored encrypted and used only to send replies as that Page. We do not read the Page's posts, comments, followers or insights."),
                new("r-msg", "Request pages_messaging", "Paste this as the explanation.",
                    "VeskaFlow is an online booking system for barbershops, salons and clinics. A shop can connect its Facebook Page so that an automated assistant answers the messages people send to the Page: it tells them the shop's services, prices and free times, and books an appointment when they ask. We use pages_messaging only to reply to a person who wrote to the Page first, inside the reply window, with a text message. We do not send promotions or messages to people who did not write to the Page. The assistant tells the person it is automated, and the shop's booking page always offers the same booking without it."),
                new("r-data", "Answer the data use questions", "Use these answers where Meta asks about the data.",
                    "What we receive: the message text, the person's Page-scoped id, the Page id, the Page's name and access token (the token is stored encrypted). What we do with it: answer the message and book appointments. No advertising, no selling, no other use. How long: 30 days for messages; the token is deleted when the owner disconnects the Page. Deletion: https://flow.veskacode.com/data-deletion. Third parties: our hosting and database providers and the AI service that writes the reply (listed in the Privacy Notice)."),
                new("r-submit", "Submit, and answer Meta's questions", "Reviews usually take days to weeks. A rejection says which step could not be reproduced: fix that and submit again. Keep the demo shop and Page working while the review runs."),
                new("r-live", "Switch the app from Development to Live", "Only after approval. Then the shops' customers can chat with the assistant. Confirm the app's data use answers once a year when Meta asks (Data Use Checklist)."),
            ]),
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
