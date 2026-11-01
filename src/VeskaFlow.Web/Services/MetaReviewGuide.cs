namespace VeskaFlow.Web.Services;

/// <summary>
/// The platform owner's guide to Meta App Review (BUSINESS_RULES 6.y23): what to do, in which part of the Meta dashboard, and the texts to paste. It has its own page
/// (<c>/Platform/MetaReview</c>) so it does not mix with the pilot checklist; the ticks are saved the same way. Only the platform owner sees it. English, like every other text.
/// Written from general knowledge of Meta's process: the names of Meta's screens change, so each step says to check what the dashboard shows today.
/// </summary>
public static class MetaReviewGuide
{
    public static readonly IReadOnlyList<PilotSection> Sections =
    [
        new("m-before", "1. Before you submit",
            "Where to click is written under each step. It is the menu on the left of your app at developers.facebook.com (My Apps, VeskaFlow Assistant) unless it says otherwise.",
            [
                new("r-tech", "Check the Tech Provider requirement",
                    "Where: Dashboard, the box 'Become a Tech Provider'. Meta says it is needed to submit to App Review and request access to data from other businesses. Read what Meta asks for there first (it usually comes with Business Verification and an agreement)."),
                new("r-verify", "Verify the business",
                    "Where: business.facebook.com/settings, Security Center, Start verification, for the business portfolio you want the app to belong to. Meta usually asks for the legal name, address, phone, website and a document that shows them (for example a DTI or SEC registration, a business permit or a utility bill in the business name). Then set the portfolio in the app: App settings, Basic."),
                new("r-settings", "Fill in the app's information",
                    "Where: App settings, Basic. App icon (1024 x 1024), category, contact email, app domain flow.veskacode.com. Privacy Policy URL: https://flow.veskacode.com/Privacy. Terms of Service URL: https://flow.veskacode.com/Terms. User data deletion: choose 'Data deletion instructions URL' and enter https://flow.veskacode.com/data-deletion. Also check Facebook Login for Business, Settings: the Valid OAuth Redirect URI must be https://flow.veskacode.com/Admin/MessengerReturn."),
                new("r-demo", "Make the demo shop and the demo Page",
                    "A demo shop on flow.veskacode.com with a few services, staff and hours, and its AI add-on switched on (Platform page). A demo Facebook Page connected to it (Admin, Messenger). Write down for the reviewer: the shop sign-in email and a password made only for this review (change it afterwards), and the Page name. Add the reviewer's access as the Meta form explains."),
                new("r-video", "Record the screencast (about 3 minutes, in English, with captions)",
                    "1) Show the VeskaFlow sign-in and Admin, Messenger. 2) Press 'Connect a Facebook Page', show Facebook's own login and permission screen, choose the demo Page, press 'Connect this Page'. 3) Open Messenger as a different person and write to the Page: 'What services do you have?', show the answer. 4) Ask it to book ('Book me a consultation tomorrow at 2pm', give a name and a number) and show the confirmation. 5) Show the booking in Bookings. 6) Press Disconnect, send another message and show that nothing answers. Record the real screens with the address bar visible. Never show a password, token or secret."),
            ]),
        new("m-permissions", "2. Ask for the permissions",
            "Where: Use cases, open 'Engage with customers on Messenger from Meta', Customize, the tab 'Permissions and features'. For each permission below, press Actions (or Get advanced access / Request advanced access), paste the text as the explanation and attach the screencast. The permissions that show 'Ready for testing' only work for people with a role on the app until Meta approves them.",
            [
                new("r-show", "pages_show_list", "Paste this as the explanation.",
                    "After a shop owner signs in to VeskaFlow and presses \"Connect a Facebook Page\", Facebook shows its own login and permission screen. We use pages_show_list to read the list of Pages the owner manages and show their names, so the owner can choose which Page to connect. We read the list once, during that step, and show only the names to the owner."),
                new("r-meta", "pages_manage_metadata", "Paste this as the explanation.",
                    "When the owner chooses a Page, we use pages_manage_metadata to subscribe our app to that Page's webhook (\"messages\" only) so that the messages people send to the Page reach our server. When the owner presses \"Disconnect\", we remove the subscription. We do not change any other setting of the Page."),
                new("r-read", "pages_read_engagement", "Paste this as the explanation.",
                    "When the owner chooses a Page, we read that Page's name and the access token Facebook issues for it (the Page endpoint needs pages_read_engagement for this, as Meta's own error message states). The name is shown to the owner on the connection screen; the token is stored encrypted and used only to send replies as that Page. We do not read the Page's posts, comments, followers or insights."),
                new("r-msg", "pages_messaging", "Paste this as the explanation.",
                    "VeskaFlow is an online booking system for barbershops, salons and clinics. A shop can connect its Facebook Page so that an automated assistant answers the messages people send to the Page: it tells them the shop's services, prices and free times, and books an appointment when they ask. We use pages_messaging only to reply to a person who wrote to the Page first, inside the reply window, with a text message. We do not send promotions or messages to people who did not write to the Page. The assistant tells the person it is automated, and the shop's booking page always offers the same booking without it."),
                new("r-data", "The data use answers", "Where: the same Permissions and features screens, and Meta's data use questions in the submission. Use these answers.",
                    "What we receive: the message text, the person's Page-scoped id, the Page id, the Page's name and access token (the token is stored encrypted). What we do with it: answer the message and book appointments. No advertising, no selling, no other use. How long: 30 days for messages; the token is deleted when the owner disconnects the Page. Deletion: https://flow.veskacode.com/data-deletion. Third parties: our hosting and database providers and the AI service that writes the reply (listed in the Privacy Notice)."),
            ]),
        new("m-after", "3. Submit and go live",
            "",
            [
                new("r-submit", "Submit for review",
                    "Where: Publish (the left menu, it shows 'Unpublished'). It lists what is still missing and has the button to submit for review. Reviews usually take days to weeks. A rejection says which step could not be reproduced: fix that and submit again. Keep the demo shop and Page working while the review runs. If your dashboard also has a menu called App Review, use that too."),
                new("r-live", "Switch the app from Development to Live",
                    "Where: Publish, after approval. Then the shops' customers can chat with the assistant. In Render set Messenger__Approved to true so the shops stop seeing the 'only testers' note. Confirm the app's data use answers once a year when Meta asks (Data Use Checklist)."),
            ]),
    ];

    public static IEnumerable<PilotItem> TickableItems => Sections.Where(s => s.Tickable).SelectMany(s => s.Items);
}
