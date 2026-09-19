# Cart and prepaid pickup order workflows

This document covers CART-01, ORD-01–04 and SORD-01–04. The implementation uses
the existing `ShoppingCarts`, `ShoppingCartItems`, `Orders`, `OrderItems`,
`OrderStatusHistories`, `PaymentTransactions` and `RefundTransactions` tables.
It does not create or migrate the database schema.

## Use-case coverage

| Use case | Backend behavior |
| --- | --- |
| CART-01 | View the active cart; add, update, remove or clear items. One active cart belongs to one storefront. |
| ORD-01 | Snapshot cart items and prices, create a `PENDING_PAYMENT` order and pending MOMO/ZALOPAY transaction with an idempotency key. |
| ORD-02 | List and view customer-owned orders with payment and status history. |
| ORD-03 | Cancel only `PENDING_PAYMENT` or `PLACED`; a paid order creates a pending full-refund request. |
| ORD-04 | Customer confirms pickup only from `READY_FOR_PICKUP`. |
| SORD-01 | Seller accepts or rejects a paid `PLACED` order; rejection requires a reason and creates a pending refund. |
| SORD-02 | Seller advances `ACCEPTED → PREPARING → READY_FOR_PICKUP`. |
| SORD-03 | Seller confirms handover from `READY_FOR_PICKUP`; the customer and seller completion paths are race-safe. |
| SORD-04 | Seller reads completed-order count, gross sales, successful refunds, net sales and order rows for the current day, week or month in Vietnam business time (`Asia/Ho_Chi_Minh`). |

The public marketplace endpoints are included because live cart flows need real
numeric menu-item identifiers. Only menu items from an `OPEN` storefront with an
approved registration and a matching active, currently valid rental contract are
returned. Cart addition, checkout and sandbox payment recheck this eligibility.

## State model

```text
PENDING_PAYMENT --payment success--> PLACED
PENDING_PAYMENT --customer cancel--> CANCELLED
PLACED --customer cancel-----------> CANCELLED + pending refund
PLACED --seller reject-------------> REJECTED  + pending refund
PLACED --seller accept-------------> ACCEPTED
ACCEPTED --------------------------> PREPARING
PREPARING -------------------------> READY_FOR_PICKUP
READY_FOR_PICKUP --customer/seller-> COMPLETED
```

The API requires `expectedStatus` on mutable order commands. Each transaction
rechecks the persisted status under serializable isolation; stale requests return
`409 Conflict` instead of overwriting a concurrent transition. Transitions write
an order-history row, an audit log and the corresponding in-app notification.

`PENDING_PAYMENT` is customer-only. Seller list, filter and detail queries never
expose an unpaid order.

## HTTP API

Public marketplace:

| Method | Path | Purpose |
| --- | --- | --- |
| GET | `/api/marketplace/menu-items?query=&take=50` | Search orderable/sold-out menu rows in open storefronts. |
| GET | `/api/marketplace/menu-items/{menuItemId}` | Get one public menu item. |

Authenticated customer (`CUSTOMER`, active account):

| Method | Path | Purpose |
| --- | --- | --- |
| GET | `/api/cart` | Active cart or `null`. |
| POST | `/api/cart/items` | Add `{ menuItemId, quantity, note }`. |
| PUT | `/api/cart/items/{menuItemId}` | Replace quantity/note. |
| DELETE | `/api/cart/items/{menuItemId}` | Remove one item. |
| DELETE | `/api/cart` | Abandon the active cart. |
| POST | `/api/orders` | Place `{ provider, idempotencyKey }`. |
| GET | `/api/orders` | List the current customer's orders. |
| GET | `/api/orders/{orderId}` | Read a customer-owned order. |
| POST | `/api/orders/{orderId}/cancel` | Cancel with `{ expectedStatus }`. |
| POST | `/api/orders/{orderId}/confirm-pickup` | Complete with `{ expectedStatus: "READY_FOR_PICKUP" }`. |

Authenticated seller (`VENDOR`, active account and owned storefront):

| Method | Path | Purpose |
| --- | --- | --- |
| GET | `/api/seller/orders?status=` | List paid/processed orders owned by the seller. |
| GET | `/api/seller/orders/{orderId}` | Read an owned paid/processed order. |
| POST | `/api/seller/orders/{orderId}/decision` | `ACCEPT` or `REJECT`; rejection requires `reason`. |
| POST | `/api/seller/orders/{orderId}/status` | Move to `PREPARING` or `READY_FOR_PICKUP`. |
| POST | `/api/seller/orders/{orderId}/handover` | Complete a ready order. |
| GET | `/api/seller/orders/sales-summary?period=DAY` | `DAY`, `WEEK` or `MONTH` summary. |

## Payment boundary

The repository creates provider-neutral pending transactions, but real MOMO and
ZaloPay redirect/callback adapters are external integrations and are not faked in
production. Development enables `Payments:SandboxEnabled`; only then the hidden
endpoint below is mapped:

```text
POST /api/orders/{orderId}/payment/sandbox-confirm
```

It is excluded from Swagger and still requires the owning customer. It changes a
pending/failed sandbox payment to `SUCCESS` and the order to `PLACED`.
Repeated confirmation never resets an already processed order. The frontend now
reads `GET /api/orders/payment-options`; a client environment flag cannot enable
server payment simulation. Checkout creates a pending order, then opens a
separate payment screen with failure/retry and resume support.

A cancel/reject operation records a `PENDING` refund request. Order responses
include the latest refund amount, reason, status, requested time and completed
time so tracking clients can distinguish `PENDING`, `SUCCESS` and `FAILED`.
A provider refund worker or callback must later finish that request in production.
Development can simulate completion using the owner-only sandbox refund endpoint.
This never contacts MoMo/ZaloPay or moves money.

## Storefront, menu, review and complaint APIs

All routes below require an active account; seller/customer ownership is checked
against the database, not caller-provided user/vendor IDs.

| Method | Path | Role / request |
| --- | --- | --- |
| GET / POST | `/api/seller/storefronts` | Vendor: list owned stores / create with registrationId, contractId, name, description, availabilityStatus. |
| PUT | `/api/seller/storefronts/{storefrontId}` | Vendor: update name, description, OPEN/PAUSED/CLOSED; cannot reassign registration/contract. |
| GET | `/api/seller/storefronts/food-categories` | Vendor: existing category IDs/names. |
| GET / POST | `/api/seller/storefronts/{storefrontId}/menu-items` | Vendor: list / create with categoryId, name, description, unitPrice, availabilityStatus. |
| PUT / DELETE | `/api/seller/storefronts/{storefrontId}/menu-items/{itemId}` | Vendor: edit or mark ARCHIVED without deleting order history. |
| GET / PUT | `/api/orders/{orderId}/review` | Customer: read own review (JSON null if absent) / upsert rating 1–5 and text for a COMPLETED order. |
| GET / POST | `/api/orders/{orderId}/complaints` | Customer: list / create complaintType COMPLAINT or REFUND_REQUEST, description, requestedRefundAmount. |
| GET | `/api/orders/payment-options` | Authenticated: SANDBOX or UNAVAILABLE, provider list and explanatory message. |
| POST | `/api/orders/{orderId}/payment/sandbox-fail` | Owning customer, Development only: simulate failure, leaving the order retryable. |
| POST | `/api/orders/{orderId}/refund/sandbox-confirm` | Owning customer, Development only: complete approved PENDING refunds for a successful sandbox payment. |

Prices/refund requests are integer VND. Menu prices are 1–50,000,000.
Seller edits cannot restore HIDDEN/ARCHIVED items or an admin-hidden store.
Complaints require a paid processed order, allow only one open case per order,
and cannot request more than the payment minus pending/successful refunds.
ADM-05 resolves/rejects these same complaint rows; customers see the resolution.
Order/refund/history/complaint timestamps are returned as UTC instants.

No schema migration is needed. Store writes respect the existing SQL
`TR_Storefronts_Phase2Gate`; an invalid contract returns a domain error.

## Remaining production payment work

This is a complete **development simulation flow**, not a real gateway integration.
MoMo/ZaloPay create-payment/redirect, signed callback verification, server-side
payment reconciliation and real refund execution are still not implemented.
Do not enable production checkout until those adapters and merchant credentials,
HTTPS callback URL, timeout/late-callback handling and gateway sandbox tests exist.
No secrets belong in frontend VITE variables or version-controlled files.

See `commerce-live.postman_collection.json` for manual API requests.

## Verification

Run from `StreetBiz-BE`:

```powershell
dotnet test StreetBiz.Backend.sln --no-restore
```

For a read-only database smoke test, start the API and verify `/health`, the two
public marketplace endpoints and anonymous `401` responses for cart/order/seller
routes. `CommerceRepositoryDatabaseTests` additionally executes the marketplace,
order/refund projection and sales summary against SQL Server when
`STREETBIZ_DB_CONNECTION` is present. End-to-end authenticated order tests create
real carts, orders, payments, history, notifications and audit rows, so use a
disposable test account/database.
