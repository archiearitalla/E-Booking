namespace VeskaFlow.Web.Services;

/// <summary>One page of the in-app guide: what a feature is for, the steps to use it, and a tip. <c>Link</c> opens the feature itself.</summary>
public sealed record HelpTopic(string Id, string Title, string Summary, string[] Steps, string? Tip, string? Link, string? LinkLabel, AdminLevel Minimum = AdminLevel.Staff);

/// <summary>
/// The guide behind the Help page and the "How does this work?" links. Written for a shop owner or staff member who has never seen the
/// system: short sentences, one action per step. It describes only what the system really does; when a feature changes, change its topic in the same commit.
/// </summary>
public static class HelpTopics
{
    public static readonly IReadOnlyList<HelpTopic> All =
    [
        new("start", "Start here: your first day",
            "Five steps and your shop is ready to take bookings.",
            [
                "Open Services and check the list. Change the prices and times to match your shop, or add your own.",
                "Open Staff and check the people who do the work. Pick which services each one does and their working hours.",
                "Open Settings and check your opening hours, the name of your shop and your booking address.",
                "Open Settings and share your booking link or print the QR code. Customers use it to book on their phones.",
                "Make a test booking yourself from the link, then find it in Bookings.",
            ],
            "You do not need everything at once. Services, Staff and Settings are enough to start.", "/Admin/Services", "Open Services", AdminLevel.Manager),

        new("bookings", "Bookings: your day at a glance",
            "Every appointment of the day, with buttons to finish, cancel or move it.",
            [
                "Open Bookings. You see today. Use Previous day, Next day or the date box to look at another day.",
                "Tap a number card (Still to do, Done, Earned, No-shows, Cancelled) to see the bookings behind it, one short line each. Tap a line for the details.",
                "When the customer has had the service, press Mark completed. Press Mark no-show if they did not come. These buttons appear when the appointment starts.",
                "To change the time, press Move. To cancel, open Cancel this booking, type the reason and confirm.",
                "Use the tabs Confirmed, Cancelled and Done to switch the list.",
            ],
            "The Earned card shows dots to keep the money private. Tap it to see the total.", "/Admin", "Open Bookings"),

        new("done", "After a visit: payment, extra services and follow-up",
            "The page that opens when you press Mark completed. It records what was done and what was paid.",
            [
                "Check the payment total. It starts at the price of the services. Change it if the customer got a discount or left a tip, then press Save payment. (Your owner can switch typing off in Settings.)",
                "If the customer had another service too, use Add another service: choose it, and type an Amount only if it was different from the listed price. For Other, say what was done and type the price.",
                "Use Follow-up visit only if the customer should come back. Pick the date and save. It is optional; leave it empty if not.",
                "Use the ready message to thank the customer. Copy it or send it as a text from your phone. Nothing is sent automatically.",
                "Clinics also see a Medical record box for notes. Only signed-in staff can read it. It is never sent to the customer.",
            ],
            "Added services and the payment total are what Reports and the customer's history use.", "/Admin", "Open Bookings"),

        new("walkin", "Walk-in: a customer at the counter",
            "Book someone who is in your shop right now.",
            [
                "Open Walk-in and choose the service from the list. If you choose Other, say what the customer needs.",
                "Choose who will do it, or leave it on Anyone.",
                "Press Show free times and pick the time.",
                "Type the customer's name. A mobile number is optional; with no number they are saved as a walk-in. Tick the promo box only if the customer agrees to get promos.",
                "Press Book. It appears in Bookings.",
            ],
            "Walk-ins skip the lead time but still follow your hours and closures, so you never double-book.", "/Admin/WalkIn", "Open Walk-in"),

        new("sale", "Record a sale by hand",
            "For a service that was done and paid without a booking. It counts in Reports and the customer's history.",
            [
                "Ask the owner to switch on Allow recording a sale by hand in Settings. Managers and owners then see Record a sale in Bookings.",
                "Choose the service, type the amount paid and choose the date (today or up to a year back).",
                "Type the customer's name and mobile number if you have them. With no number, the customer is saved as a walk-in.",
                "Press Save the sale. It is saved as a completed visit.",
            ],
            "Pressing Save twice by accident only saves one sale.", "/Admin/RecordSale", "Open Record a sale", AdminLevel.Manager),

        new("reminders", "Reminders: tell customers about tomorrow",
            "A list of customers with an appointment coming up, each with a ready message.",
            [
                "Open Reminders. Each line is a customer with a confirmed appointment soon.",
                "Send the ready message by text, email or Messenger from your phone, or copy it.",
                "Press Mark reminded so the customer leaves the list.",
            ],
            "Nothing is sent by itself. You choose how to reach each customer.", "/Admin/Reminders", "Open Reminders"),

        new("followups", "Follow-ups: bring customers back",
            "Customers you asked to come back, with a ready message when it is time.",
            [
                "Set a follow-up date on the page that opens after a visit.",
                "Open Follow-ups to see who is due or overdue.",
                "Send the message, then mark it done.",
            ],
            "A banner at the top of every page tells you when someone is due.", "/Admin/FollowUps", "Open Follow-ups"),

        new("customers", "Customers: who comes to your shop",
            "Everyone who booked, with visits and total spent.",
            [
                "Open Customers. Type a name, phone or email in the search box and press Search.",
                "Tap a name to see that customer's history, their notes and what they bought.",
                "Press Download CSV to open the list in Excel or Google Sheets.",
            ],
            "Walk-ins without a number appear as separate customers named Walk-in.", "/Admin/Customers", "Open Customers", AdminLevel.Manager),

        new("reports", "Reports: how the shop is doing",
            "Money earned, busy days and your best services and staff.",
            [
                "Open Reports and choose the dates you want to look at.",
                "Read Revenue first. It counts completed visits only, using the payment total (or the service prices).",
                "Look at the lists by service and by staff to see what sells best. Tips and discounts typed in a total are shown on their own line.",
                "If you ran a promo, Promo discounts given shows how much you gave away.",
            ],
            "Cancelled bookings and no-shows are counted separately and never as money.", "/Admin/Reports", "Open Reports", AdminLevel.Manager),

        new("services", "Services: what you sell",
            "The list customers choose from, with price and time.",
            [
                "Open Services and press Add a service. Type the name, price and how long it takes.",
                "Press Add suggested services to get the usual list for your kind of shop. Ones you already have are skipped.",
                "Open a service to change it. Untick Active to hide it from customers without deleting it.",
                "Tick Part of the promo if the promo discount should apply to this service.",
                "The Other service lets staff record something that is not on the list. You set its time; the price is typed each time.",
            ],
            "Changing a price never changes bookings that were already made.", "/Admin/Services", "Open Services", AdminLevel.Manager),

        new("staff", "Staff: the people who do the work",
            "Each person has their own services and working hours.",
            [
                "Open Staff and press Add a staff member.",
                "Type the name, tick the services they do and set their working hours.",
                "Untick Active when someone leaves. Their old bookings stay.",
            ],
            "The free plan has one active staff member. More people need a paid plan.", "/Admin/Staff", "Open Staff", AdminLevel.Manager),

        new("closures", "Closures and opening hours",
            "Days when the shop is closed, such as holidays.",
            [
                "Open Closures. Under Close days, choose On dates, pick the first and last day, type a short reason and press Add closure.",
                "For a day every week (for example Sunday) choose Every week instead and tick the day.",
                "Customers can not book on those days. Bookings already made are not cancelled for you; check the day and tell the customers.",
                "Normal opening hours are in Settings.",
            ],
            "Add closures early so customers do not book those days.", "/Admin/Closures", "Open Closures", AdminLevel.Manager),

        new("promosend", "Send a promo to customers",
            "Your promo, ready to send to customers who agreed to get promo messages.",
            [
                "Make and switch on your promo in the Promo Creator first.",
                "Open Send a promo to customers (a button on the Promo page). You see how many customers can be reached.",
                "Tap a customer. You see the ready message with their name, your promo, the last day and your booking link.",
                "Press Send as text or Email to send it from your own phone. Or send it in an app: WhatsApp opens the customer's chat with the message ready; Viber and Telegram open with the message ready and you choose the customer; More apps lists every app on your phone, such as Messenger. Copy also works for any other app.",
                "Press Mark as sent so you do not send the same promo twice. When you start a new promo, press Clear all sent marks.",
                "To send it to everyone who has an email address, press Send it by email to N customers. The system emails them for you, within your daily email limit (what is left goes out the next day), and every email has a link to stop promo messages. You can press Stop sending any time.",
                "Only customers who said yes are listed. New customers tick a box when they book; for walk-ins, ask first and tick the box. If someone says STOP, open their history and press They asked to stop.",
            ],
            "The system never sends the message by itself, so you stay in control of who gets it.", "/Admin/PromoSend", "Open Send a promo", AdminLevel.Manager),

        new("promo", "Promo Creator: banner and discount",
            "Put a promo on your booking page in a minute, with an optional discount.",
            [
                "Open Promo. Choose a mode (for example Halloween, Christmas or Summer). The preview changes as you type.",
                "Write the headline. Add a short line and a badge if you want.",
                "To give a discount, type a percent from 1 to 90. The booking page then shows the regular price and the lower price.",
                "Choose the last day, tick Active and save. Untick Active to pause it.",
                "In Services, untick Part of the promo for any service that should not get the discount.",
            ],
            "The discount is fixed when the customer books. Changing the promo later never changes bookings already made.", "/Admin/Promo", "Open Promo", AdminLevel.Owner),

        new("settings", "Settings: your shop's rules",
            "Name, address, hours, how far ahead customers can book and what staff may do.",
            [
                "Open Settings. At the top you can change the shop name, time zone and booking rules.",
                "Opening hours: set one range and apply it to everyone, or set each person in Staff.",
                "Time between slots, notice needed and how many days ahead customers can book are all here.",
                "Cancel cutoff is how many hours before the appointment a customer can still cancel on their own.",
                "Tick Staff can type the payment total to let staff change the total on the page after a visit. Tick Allow recording a sale by hand to turn on Record a sale.",
                "Upload your logo, or make one with the Logo Creator.",
            ],
            "Press Save at the bottom of each section after you change something.", "/Admin/Settings", "Open Settings", AdminLevel.Owner),

        new("share", "Share your booking page",
            "Customers book on their own from a link or a QR code.",
            [
                "Open Settings and find your booking address and QR code.",
                "Copy your booking address and send it by Messenger, text or social media. Or press Print under the QR code and put it in your shop. Download QR (SVG) saves it for a poster.",
                "Put the link in your social media bio so customers can book any time.",
            ],
            "Open the link on your own phone to see exactly what customers see.", "/Admin/Settings", "Open Settings", AdminLevel.Owner),

        new("ai", "AI receptionist (paid add-on)",
            "A chat on your booking page that answers questions and books for customers, in English or Tagalog.",
            [
                "The add-on is switched on for your shop after you pay. Ask the platform owner.",
                "When it is on, a chat button appears on your booking page. Customers ask about services, prices and free times.",
                "It books using your real services and hours, so it can never double-book.",
                "Settings shows how many conversations you used this month.",
            ],
            "It can not cancel or move a booking; it gives the customer their manage link instead.", "/Admin/Settings", "Open Settings", AdminLevel.Owner),

        new("billing", "Plan and billing: paying for your access",
            "See your plan, the prices, and pay for Pro or an add-on with GCash.",
            [
                "Open Plan and billing. At the top you see your plan and the date your access ends.",
                "Send the exact amount with GCash: scan the QR code, or use the number shown.",
                "Choose what you paid for (Pro for a month or a year, the AI add-on or the Email add-on) and type the reference number from your GCash receipt.",
                "Press I paid. Your payment shows as waiting until it is confirmed, usually within a day.",
                "When it is confirmed, your access is extended and a receipt appears under Your payments. Open it to print it.",
            ],
            "Paying for a year costs less than twelve months. If your access has ended, you can still open this page to pay and your data is kept.", "/Admin/Billing", "Open Plan and billing", AdminLevel.Owner),

        new("team", "Team: who can sign in",
            "Give your staff their own login with the right level of access.",
            [
                "Open Team and press add. Type their name and email and give them a password to share with them.",
                "Choose a level. Staff run the day. Managers also set up the shop and see reports. Only the owner can change Settings and the team.",
                "A Staff login can be tied to one person so they see only their own bookings.",
                "Switch a person off when they leave.",
            ],
            "Never share your own password. Give each person their own login.", "/Admin/Team", "Open Team", AdminLevel.Owner),

        new("account", "Your account and security",
            "Change your password and see who did what.",
            [
                "Open Account and press Change password.",
                "Managers can open the Audit log to see who cancelled, moved or completed a booking, and who changed a payment.",
                "If you forget your password, use Forgot password on the sign-in page.",
            ],
            "Sign out on shared phones and computers.", "/Admin/Account", "Open Account"),

        new("privacy", "Customer privacy",
            "What you must keep safe, and how to help a customer who asks about their data.",
            [
                "Only people you give a login can see your customers. Do not share logins.",
                "Medical records (for clinics) are never shown to the customer or put in a message.",
                "If a customer asks for a copy of their data or to be erased, the owner opens their history and uses Export this customer's data or Delete this customer's data.",
            ],
            "Read the Privacy Notice and Terms linked at the bottom of every page.", "/Admin/Customers", "Open Customers", AdminLevel.Manager),
    ];

    public static IReadOnlyList<HelpTopic> For(AdminLevel level) => All.Where(t => level >= t.Minimum).ToList();

    public static HelpTopic? Find(string? id) => All.FirstOrDefault(t => string.Equals(t.Id, id, StringComparison.OrdinalIgnoreCase));
}
