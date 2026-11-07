# payOS: paid straight to the payee

StreetBiz does **not** hold money. Each payee connects the receiving account of **its own** payOS
channel, and the payer's VietQR transfer lands in that account:

| What is paid | Who is paid | Where the money lands |
| --- | --- | --- |
| Rental fee (FEE-01) | the **ward** that owns the slot | the ward's payOS channel |
| Order | the **household** that owns the stall | the household's payOS channel |
| Fine (FEE-04) | the **State Treasury** | not through StreetBiz at all (see below) |

A household without a connected channel takes no online order ("Quán chưa nhận thanh toán
online"); a ward without one shows no online option for its fees. MoMo, ZaloPay and SePay stay,
and are offered only where they are configured (or all simulated in Development). See
`sepay-payments.md` for those.

Why: collecting other people's money and passing it on needs a payment-intermediary licence
(Nghị định 52/2024) and makes StreetBiz the party that must refund. Paying straight to the payee
avoids both, and payOS confirms the payment automatically, so no one reconciles a bank statement.

## How a payment works

1. The payer picks **Chuyển khoản VietQR**. The screens read
   `GET /api/vendor/finance/fees/{id}/payment-options` (the ward) or
   `GET /api/orders/storefronts/{id}/payment-options` (the household); `PAYOS` is in the list,
   with the payee named, only when that payee's account is `ACTIVE`.
2. `POST .../checkout` with `"provider": "PAYOS"` creates a payOS payment link **with the payee's
   keys** (`POST /v2/payment-requests`). The response carries `bankTransfer`: the payee's bank
   account, the amount, `qrContent` (the VietQR, drawn by the web app) and `checkoutUrl` (payOS's
   hosted page). The link is stored on the transaction (`checkout_payload`), so a reload shows the
   same QR instead of opening a new link while it is still valid (`LinkLifetimeMinutes`).
3. The payer scans and pays. Two layers confirm it, both automatic:
   - **Webhook (layer 1).** payOS posts to `POST /api/payments/payos/callback/{token}`. The token
     is random and unique per account, so the URL says which channel it belongs to. The signature
     is checked with **that account's** checksum key.
   - **Reconciliation (layer 2).** The pay page polls the sync endpoints every 5 seconds and the
     hosted sweep (`FeeReminderHostedService`) asks payOS for the state of anything still open
     (`GET /v2/payment-requests/{orderCode}`), with the payee's keys. A lost webhook costs nothing.
4. Both go through the same `ProcessPaymentCallbackCommand` as every provider: the fee is PAID and
   invoiced, or the order is PLACED.

### The rule that keeps channels apart

Only the **payee's own channel** may settle its payment. A transaction records `payee_account_id`;
the webhook is verified with the keys of the account whose token is in the URL, and the callback is
**rejected** unless that account is the transaction's payee. A household cannot settle (or
"pay") the ward's fee, nor another household's order, even with a correctly signed webhook of
its own. A URL token that matches no account is answered `200 IGNORED` and recorded nowhere (payOS
calls the URL once while registering it, before the account exists).

### Codes

`orderCode` = `transactionId × 1000 + attempt` (attempt 1…999), so every payOS link is unique and a
retry never reuses a code; `provider_reference` is `PAYOS-{orderCode}`. payOS's own test webhook
uses a small order code (below 1000) and is ignored. The payment description is `SB{transactionId}`.

A second transfer for an already-paid payment is recorded `REJECTED` (surplus) and raised through
the payment alerts, exactly as for SePay; it is never applied twice.

## Connecting an account

`/api/vendor/payout-account` (a household) and `/api/ward/payout-account` (a ward; any officer of
the ward manages the one shared account):

- `GET` — the account **masked** (bank, last digits of the account number, client id). Keys are
  never returned.
- `PUT {clientId, apiKey, checksumKey}` — connect, or replace the keys. payOS must accept them
  first (`422` otherwise, nothing saved). When `Payments:PayOs:PublicWebhookBaseUrl` is set, the
  webhook URL is registered with payOS (`POST /confirm-webhook`); without it only the keys are
  checked and payments are found by reconciliation.
- `POST .../check` — asks payOS again with the stored keys. Refused keys put the account in
  `ERROR`.
- `DELETE` — `DISABLED`: no new payments, but the **keys are kept** so a late webhook of an earlier
  payment still verifies.

When payOS answers `401` to a link or status request, the account turns `ERROR`, its owner is
notified, a `CHANNEL_ERROR` payment alert is sent (Discord) and `PAYOS` is no longer offered for
that payee until the keys are fixed.

Keys are stored encrypted (AES-256-GCM, `v1:` + base64 of nonce‖cipher‖tag) with
`Payments:PayOs:CredentialKey`. Never log them: `PayeeCredentials` masks itself in `ToString()`.

## Fines: paid to the State Treasury

A fine is State revenue (Luật Xử lý vi phạm hành chính, Điều 78: paid into the State Treasury
account named in the decision), so StreetBiz neither collects nor passes it on. There is no online
penalty checkout.

1. `GET /api/vendor/finance/penalties/{id}/treasury` — how to pay, and the latest receipt.
2. The household pays the Treasury, uploads the receipt (`POST /api/uploads/evidence`) and sends it:
   `POST /api/vendor/finance/penalties/{id}/receipts`. The ward's officers are notified. Only one
   receipt can wait at a time (`409`).
3. The ward lists them (`GET /api/ward/finance/penalty-receipts`), opens the file (an officer of the
   fine's ward may read it; nobody else may), and **accepts** (the fine is PAID with provider
   `TREASURY` and an invoice) or **rejects** with a reason.
4. If the decision is annulled after payment, `POST /api/ward/finance/penalties/{id}/annul`: the
   fine becomes CANCELLED and its payment `REFUND_REQUIRED`. The Treasury, not StreetBiz, returns
   the money; the officer then records "Kho bạc đã hoàn" in the existing refunds list
   (`POST /api/ward/reports/refunds/{id}/mark-refunded`).

## Configuration

`Payments:PayOs` in `appsettings.json` (no secrets there):

| Key | Meaning |
| --- | --- |
| `Enabled` | Off switch for `PAYOS` everywhere. |
| `ApiBaseUrl` | `https://api-merchant.payos.vn`, or `http://localhost:5199` for the fake server. |
| `PublicWebhookBaseUrl` | Public https origin payOS can reach (ngrok, the deployed API). Empty = no webhook, reconciliation only. |
| `FrontendBaseUrl` | Where payOS's hosted page sends the payer back. |
| `LinkLifetimeMinutes` | How long a link (and its QR) stays payable. Default 30. |
| `CredentialKey` | Base64 of 32 random bytes that encrypts the keys. **User-secrets / environment only.** Development has a fallback; elsewhere `PAYOS` stays off without it. |

```powershell
dotnet user-secrets --project src/StreetBiz.API set Payments:PayOs:CredentialKey (
  [Convert]::ToBase64String((1..32 | ForEach-Object { Get-Random -Maximum 256 }) -as [byte[]]))
```

## Trying it without money: the fake payOS server

payOS has **no test environment**: a real link moves real money. `scripts/payos-fake/server.mjs`
(Node, no dependencies) speaks the same merchant API and sends correctly signed webhooks:

```powershell
node scripts/payos-fake/server.mjs                      # http://localhost:5199 ; control page: /dev
$env:Payments__PayOs__ApiBaseUrl = 'http://localhost:5199'
$env:Payments__PayOs__PublicWebhookBaseUrl = 'http://localhost:5023'   # the API itself
dotnet run --project src/StreetBiz.API
```

Connect the demo keys it lists on `/dev` (one ward, two households) in the app's **Tài khoản nhận
tiền**. Open a pay link (or `/dev`) and press **Khách đã trả**: a signed webhook reaches the API and
the fee/order settles. **Tắt webhook** simulates a lost webhook to see reconciliation settle it;
**Chuyển lần 2** sends a second transfer (surplus).

## Trying it with real payOS

1. Make a channel at https://my.payos.vn with a bank account you own; copy Client ID, API Key,
   Checksum Key.
2. Expose the API (`ngrok http 5023`) and set `PublicWebhookBaseUrl` to its https origin.
3. Connect the keys in the app, then pay a **small amount** to your own account.
4. Check the points marked "to verify" below against payOS's current documentation.

To verify against real payOS (the code is conservative about all of them: an unknown status is
treated as not settled, a `429` skips the round and retries on the next one): the full list of
payment-link statuses; the exact body of the webhook payOS sends while registering the URL; the
`description` length rule for a linked bank account; API rate limits; and the conditions of payOS's
free plan.

## Database

`PayeeAccounts` (one per household or ward; encrypted keys, `webhook_token`), `PenaltyPaymentReceipts`
(one waiting receipt per fine), and on `PaymentTransactions`: `payee_account_id` and
`checkout_payload`. `PAYOS` joins the provider CHECKs (and `TREASURY` on transactions). The schema
lives only in `db/StreetBiz_SQL_Server.sql`; there are no migrations, so an existing local database
must be rebuilt (`scripts/setup-local-db.ps1 -Recreate`, which destroys its data: back up first).

## Tests

`PayOsPaymentGatewayTests`, `CredentialProtectorTests`, `PayeeAccountRepositoryTests`,
`PayeePaymentRepositoryTests`, `PenaltyReceiptRepositoryTests` (Infrastructure),
`PayeePaymentHandlerTests`, `PayoutAccountAndReceiptTests` (Application), `PayOsEndpointTests`,
`UploadsControllerTests` (API).
