# Connecting VeskaFlow to n8n

VeskaFlow posts **events** (a booking was made, cancelled, completed, and so on) to a web address you choose. In n8n that address is a **Webhook node**. From there you can send a message, fill a sheet or start any workflow. VeskaFlow only sends; n8n decides what to do.

Two sample workflows are here. Import them in n8n: **Workflows ▸ Import from file**.

| File | For | Events |
|---|---|---|
| `veskaflow-shop-events-to-telegram.json` | A shop owner | `booking.created`, `booking.confirmed`, `booking.cancelled`, `booking.completed`, `booking.no_show`, `booking.rescheduled`, `sale.recorded`, `test.ping` |
| `veskaflow-platform-alerts-to-telegram.json` | The platform owner | `payment.submitted`, `payment.confirmed`, `shop.signed_up`, `test.ping` |

Both do the same four things: receive the request, check its signature, ignore it if the signature is wrong or the request is old, and send a short Telegram message. Telegram is only an example: replace the last node with WhatsApp, Email, Google Sheets or anything else n8n has.

> **Not tested in a real n8n.** The workflow files were checked as valid JSON and their code nodes were run against a signed sample request, but they were never imported into a running n8n. If an import or a node complains, tell us what it says.

## For a shop owner

1. In n8n, import `veskaflow-shop-events-to-telegram.json`.
2. Open the **Webhook** node and copy its **Production URL** (it looks like `https://your-n8n.example.com/webhook/veskaflow-shop`). The workflow must be **Active** for that address to work.
3. In VeskaFlow open **Integrations** (Owner menu). Paste the address, tick **Send events**, press **Save**.
4. Copy the **Secret** from the same page and paste it into the **Compute signature** node (field *Secret*).
5. In the **Send to Telegram** node choose your Telegram credential and put your chat id. (Create a bot with @BotFather, send it a message, and get the chat id from n8n's Telegram Trigger or from the Bot API.)
6. In VeskaFlow press **Send a test event**. You should get "The test was received" and a Telegram message "VeskaFlow test: the connection works."

The address must start with **https://** and be public. VeskaFlow refuses addresses on a private network.

## For the platform owner

Same steps, but the address and secret are **settings on the server** (Render environment variables), not a page:

- `Webhooks__PlatformUrl` = the production URL of the Webhook node in the platform workflow (path `veskaflow-platform`).
- `Webhooks__PlatformSecret` = a long random secret (at least 16 characters) that you also paste into that workflow's **Compute signature** node.

Redeploy after adding them. Events: a payment waiting for you, a payment confirmed, a new shop.

## What a request looks like

```
POST <your address>
Content-Type: application/json
X-VeskaFlow-Event: booking.created
X-VeskaFlow-Delivery: 5f0c...        (the event id; the same id on a retry)
X-VeskaFlow-Timestamp: 1730000000     (seconds since 1970, UTC)
X-VeskaFlow-Signature: sha256=3a1f... (see below)

{
  "id": "5f0c...",
  "event": "booking.created",
  "createdAt": "2030-03-04T01:00:00.0000000Z",
  "shop": { "id": "...", "slug": "bens", "name": "Ben's", "timeZone": "Asia/Manila" },
  "data": {
    "booking": { "id": "...", "status": "CONFIRMED", "service": "Haircut", "staff": "Jun",
                 "startsAtUtc": "...", "endsAtUtc": "...", "startsAtLocal": "2030-03-05 10:00",
                 "price": 250.00, "paidAmount": null, "reason": null },
    "customer": { "name": "Pia Cruz", "phone": "09175550101", "email": "pia@example.com" },
    "actor": "Pia Cruz (customer)"
  }
}
```

Never sent: medical records, the customer's manage link, passwords.

### Checking the signature

`signature = "sha256=" + hex( HMAC-SHA256( secret, timestamp + "." + rawBody ) )`

`rawBody` is the exact text VeskaFlow sent (the Webhook node's **Raw Body** option keeps it). Reject a request whose signature does not match, or whose timestamp is more than five minutes old. The sample workflows do both.

### Delivery and retries

A delivery that does not answer 2xx within 8 seconds is retried after 1 minute, 5 minutes, 30 minutes, 2 hours and 6 hours, then given up. The retry carries the same `X-VeskaFlow-Delivery` id, so a workflow can ignore one it has already seen. The **Latest deliveries** list on the Integrations page shows what happened. A slow or broken n8n never slows down or breaks a booking.

Events are made a few seconds after the change (a worker checks every 10 seconds), and only for changes made after you switched **Send events** on.

## Safety

- The events contain your customers' names, phones and emails. Use an n8n you control or trust, and keep the secret private. If it leaks, press **Make a new secret** and update the workflow.
- Do not expose an n8n on the free Render instance: it goes to sleep and misses events (VeskaFlow retries, but only for a few hours).
- If you use an event to message customers, the promo rules still apply: only message customers who said yes to promo messages.
