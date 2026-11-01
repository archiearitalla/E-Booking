# Meta App Review: getting the Messenger assistant open to the public

Until Meta approves the app, only people with a role on it (admin, developer, tester) can connect a Page or chat with it. This is the list of what to do, in order, and text you can copy. Written from general knowledge of Meta's process on 2026-11-02; **Meta changes its screens and rules often, so check each step against what the dashboard says today.** Nothing here has been submitted yet and approval is not guaranteed.

The app: **VeskaFlow Assistant**, a Business-type app with the Messenger use case and Facebook Login for Business (a configuration that asks for `pages_show_list`, `pages_messaging`, `pages_manage_metadata`, `pages_read_engagement`). The code that uses them is in `src/VeskaFlow.Web/Services/Messenger.cs` and described in BUSINESS_RULES 6.y23.

## 1. Business Verification (usually the slowest part)

1. Meta Business Suite, Business settings, **Security Center**, Start verification, for the business portfolio you want the app to belong to.
2. Meta usually asks for the business's legal name, address, phone and website, and a document that shows them (for example a DTI or SEC registration, a business permit, or a utility bill in the business name), and then confirms by phone, email or a domain check.
3. In the Meta app, **App settings, Basic**, set the business portfolio (the app was created without one).
4. If you do not have a registered business, find out from Meta support whether the verification can be done another way. Advanced Access to `pages_messaging` normally needs a verified business.

## 2. Complete the app's own settings (App settings, Basic)

| Field | What to put |
|---|---|
| App icon | 1024 x 1024 picture (the VeskaFlow logo) |
| Privacy Policy URL | `https://flow.veskacode.com/Privacy` |
| Terms of Service URL | `https://flow.veskacode.com/Terms` |
| User data deletion | Choose **Data deletion instructions URL**: `https://flow.veskacode.com/data-deletion` |
| Category | Business and pages (or the closest one) |
| App domain | `flow.veskacode.com` |
| Contact email | An address you read every day |

Under Facebook Login for Business, Settings, **Valid OAuth Redirect URIs** must contain `https://flow.veskacode.com/Admin/MessengerReturn`.

## 3. Test accounts for the reviewer

The reviewer must be able to try the whole thing without you:

1. Make a demo shop on flow.veskacode.com (for example `Demo Dental`) with a few services, staff and hours, and switch on its AI add-on (Platform page).
2. Make a demo **Facebook Page** for it and connect it in the shop (Admin, Messenger).
3. In the Meta app, App roles, add the reviewer's access as Meta asks (Meta gives instructions for test users in the submission form). Write down, for the reviewer: the shop sign-in email and password, and the Page name.
4. Put those in the "Instructions for the reviewer" box. Use a password made only for this. Change it after the review.

## 4. Permissions to request (App Review, Permissions and features)

Ask for Advanced Access to each. Copy the text and change anything that is not true.

**`pages_show_list`**
> After a shop owner signs in to VeskaFlow and presses "Connect a Facebook Page", Facebook shows its own login and permission screen. We use pages_show_list to read the list of Pages the owner manages and show their names, so the owner can choose which Page to connect. We read the list once, during that step, and show only the names to the owner.

**`pages_manage_metadata`**
> When the owner chooses a Page, we use pages_manage_metadata to subscribe our app to that Page's webhook ("messages" only) so that the messages people send to the Page reach our server. When the owner presses "Disconnect", we remove the subscription. We do not change any other setting of the Page.

**`pages_read_engagement`**
> When the owner chooses a Page, we read that Page's name and the access token Facebook issues for it (the Page endpoint needs pages_read_engagement for this, as Meta's own error message states). The name is shown to the owner on the connection screen; the token is stored encrypted and used only to send replies as that Page. We do not read the Page's posts, comments, followers or insights.

**`pages_messaging`**
> VeskaFlow is an online booking system for barbershops, salons and clinics. A shop can connect its Facebook Page so that an automated assistant answers the messages people send to the Page: it tells them the shop's services, prices and free times, and books an appointment when they ask. We use pages_messaging only to reply to a person who wrote to the Page first, inside the reply window, with a text message. We do not send promotions or messages to people who did not write to the Page. The assistant tells the person it is automated, and the shop's booking page always offers the same booking without it.

**Data use answers (the form asks):**
- What we receive: the message text, the person's Page-scoped id, the Page id, the Page's name and access token (token stored encrypted).
- What we do with it: answer the message, book appointments. No advertising, no selling, no use for anything else.
- How long: 30 days for messages. The token is deleted when the owner disconnects the Page.
- Deletion: `https://flow.veskacode.com/data-deletion`.
- Third parties: our hosting and database providers and the AI service that writes the reply (listed in the Privacy Notice).

## 5. Screencast (one video can cover all three; keep it under about 3 minutes, in English, with captions)

1. Show the VeskaFlow sign-in and the shop's Settings, then **Admin, Messenger**.
2. Press **Connect a Facebook Page**. Show Facebook's login and permission screen (this shows `pages_show_list`). Choose the demo Page. Show it listed on **Choose the Page to connect** and press **Connect this Page**. Say that this subscribes the Page's messages (`pages_manage_metadata`).
3. Open Messenger **as a different person** (a test user or another account) and write to the Page: "What services do you have?" Show the assistant's answer (this shows `pages_messaging`).
4. Ask it to book: "Book me a consultation tomorrow at 2pm", give a name and number. Show the confirmation message.
5. Back in VeskaFlow, show the booking in **Bookings**.
6. Press **Disconnect** on the Page and show that it disappears. Send another message and show that nothing answers.

Record the real screens, not a mock-up. Show the address bar. Do not show any password, token or secret on screen.

## 6. After you submit

- Reviews usually take days to weeks. A rejection says which step could not be reproduced; fix that and resubmit.
- When approved, switch the app from **Development** to **Live** (top of the dashboard). Only then can the shops' customers chat.
- Keep the app's data-use answers true. Meta asks you to confirm them once a year (Data Use Checklist).

## 7. What this does not cover (decide yourself)

- Meta's rule about when a Page may message a person first (the reply window) is not enforced by the code: the assistant only ever replies to a message it just received.
- Whether Meta accepts an AI assistant for this use. Their policies for automated replies change; read the current Messenger Platform policy before you submit and say clearly that the assistant is automated.
- Handing a conversation to a person (not built).
- A token Meta revokes (for example when the owner changes their Facebook password): the Page goes quiet and the owner must connect it again.
