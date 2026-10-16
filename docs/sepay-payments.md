# SePay bank transfers

SePay (https://developer.sepay.vn) watches a bank account and calls a webhook when money
arrives. StreetBiz uses it as a third payment provider, `SEPAY`, beside MoMo and ZaloPay, for
all three payment purposes: rental fees (FEE-01), penalties (FEE-04) and orders.

## How a payment works

1. The payer picks **Chuyển khoản** and the API opens a checkout as for any provider
   (`POST /api/vendor/finance/fees/{id}/checkout`, `.../penalties/{id}/checkout`,
   `POST /api/orders/checkout`, all with `"provider": "SEPAY"`).
2. `SePayGateway` returns no page to redirect to. The checkout response carries
   `bankTransfer`: bank, account number, account name, amount, the transfer content and a
   VietQR image from `https://qr.sepay.vn/img`. The transfer content is the **payment code**,
   `{PaymentCodePrefix}{transactionId}` (`SBP42`), and it is stored as the transaction's
   `provider_reference`.
3. The payer scans the QR (or types the details). The web page polls the existing sync
   endpoints every 5 seconds while the QR is shown.
4. SePay posts the incoming transfer to `POST /api/payments/sepay/callback`. The gateway
   authenticates it, reads the payment code from SePay's `code` field or, failing that, from the
   transfer content, and the request goes through the same `ProcessPaymentCallbackCommand` as a
   MoMo IPN: the fee/penalty is marked PAID and invoiced, or the order is PLACED.
5. The next poll sees SUCCESS and the page moves on.

The QR can be fetched again while the payment is still open (after a reload, or on another
device): `GET /api/vendor/finance/payments/{transactionId}/bank-transfer` and
`GET /api/orders/{orderId}/payment/bank-transfer`.

## Configuration

`Payments:SePay` (see `appsettings.json` for the shape). Secrets go in user-secrets or
environment variables, never in a committed file.

| Key | Required | Meaning |
| --- | --- | --- |
| `BankName` | yes | The bank as SePay's QR service names it: `MBBank`, `Vietcombank`, `TPBank`, `ACB`, ... |
| `AccountNumber` | yes | The receiving account linked to SePay |
| `AccountName` | no | Shown to the payer |
| `PaymentCodePrefix` | no (`SBP`) | Must match the prefix of SePay's payment-code rule |
| `TransferContentTemplate` | no (`{code}`) | Some banks need fixed text around the code |
| `WebhookSecret` | one of these two | The webhook's HMAC-SHA256 secret (recommended) |
| `WebhookApiKey` | one of these two | The webhook's API key (`Authorization: Apikey …`) |
| `ApiToken` | no | A My SePay API token. Lets "Kiểm tra lại" and the stale-payment sweep ask SePay directly (`GET /userapi/transactions/list`) when the webhook cannot reach the server |

SEPAY is offered once `BankName`, `AccountNumber` and one of `WebhookSecret`, `WebhookApiKey`
or `ApiToken` are set. In Development with `Payments:SandboxEnabled`, an unconfigured SEPAY
falls back to the simulator like the other providers.

## The webhook in My SePay

- URL: `https://{public host}/api/payments/sepay/callback`
- Event: money in. Money out is acknowledged and ignored.
- Authentication: HMAC-SHA256 (`X-SePay-Signature: sha256={hex}` over
  `"{X-SePay-Timestamp}.{raw body}"`, timestamps more than 5 minutes off are refused) or API
  Key. An unauthenticated request is recorded as REJECTED and never applied.
- The endpoint answers 200 with `{"success": true, "result": "APPLIED"|"DUPLICATE"|...}` for
  every request it recorded, so SePay does not retry a REJECTED/UNMATCHED one. A server error
  answers 500 and SePay retries (up to 7 times over about 5 hours).
- Optional but useful: set a payment-code rule (prefix `SBP`, numeric suffix) in the company
  settings and filter the webhook on it, so transfers unrelated to StreetBiz never reach us.

## What can go wrong with a bank transfer

The payer types the transfer, so unlike MoMo the amount and the code can be wrong:

| Case | Outcome | What happens |
| --- | --- | --- |
| Code and amount match | APPLIED | Paid and invoiced / order placed |
| SePay sends the same transfer again | DUPLICATE | Nothing (matched on SePay's transaction `id`) |
| No code, or an unknown one | UNMATCHED | Money is in the account but assigned to nothing |
| Wrong amount, or money into another linked account | REJECTED | Not applied |
| A second transfer for something already paid, or money for a cancelled order | SURPLUS (stored as REJECTED) | Not applied; owed back by hand |
| Bad signature/API key | REJECTED | Not applied |

Every row but the first two raises a payment alert. SePay has no refund API: refunds are bank
transfers made by hand.

## Payment alerts (Discord)

`IPaymentAlertNotifier` is told about every callback that could not be applied, for every
provider, and about server errors while applying one. `DiscordPaymentAlertNotifier` posts it
to a Discord channel webhook as one embed (provider, outcome, reason, payment code, amount,
SePay transaction id, StreetBiz transaction, environment).

| Key | Meaning |
| --- | --- |
| `Alerts:DiscordWebhookUrl` | The channel webhook URL. Empty switches alerts off. It lets anyone post to the channel: keep it secret |
| `Alerts:DiscordMention` | Optional text before each alert, e.g. `<@&roleId>` or `@here` |

Delivery is best effort with a 5 second timeout; a failing Discord never fails a callback.
SePay's own alert channels (Webhooks → Alert channels, Live mode only) cover the other side:
SePay telling you it could not reach the server at all.

## Local development

- SePay Test mode has its own bank accounts, accepts plain HTTP and can simulate an incoming
  transfer, which fires the webhook.
- SePay must reach the API: expose port 5023 with `ngrok http 5023` or a VS Code port forward
  set to Public, and use that host in the webhook URL.
- Without a tunnel, set `ApiToken`: the page's checks then ask SePay directly.
- With `VITE_ENABLE_PAYMENT_SANDBOX=true` in the web app and `Payments:SandboxEnabled` here,
  the QR panel also offers a Development-only "Giả lập tiền đã về" button.

## Database

`SEPAY` was added to `CK_PaymentTransactions_Provider`, `CK_PaymentCallbackEvents_Provider`
and `CK_RefundTransactions_Provider` in `db/StreetBiz_SQL_Server.sql`. Rebuild the local
database (`scripts/setup-local-db.ps1 -Recreate`) before trying SePay; with the old
constraints the first SEPAY checkout fails.
