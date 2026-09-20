# Cart and prepaid pickup order workflows

This document covers CART-01, ORD-01–04 and SORD-01–04. The implementation uses
the existing `ShoppingCarts`, `ShoppingCartItems`, `Orders`, `OrderItems`,
`OrderStatusHistories`, `PaymentTransactions` and `RefundTransactions` tables.
Migration `AddOrderStorefrontAddressSnapshot` adds and backfills the immutable
storefront address stored with each order.

## Use-case coverage

| Use case | Backend behavior |
| --- | --- |
| CART-01 | View the active cart; add, update, remove or clear items. One active cart belongs to one storefront. |
| ORD-01 | Snapshot cart items, prices and storefront address; create a `PENDING_PAYMENT` order and pending MOMO/ZALOPAY transaction with an idempotency key. |
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
PENDING_PAYMENT --payment failure--> CANCELLED
PENDING_PAYMENT --customer cancel--> CANCELLED
PLACED --customer cancel-----------> CANCELLED + pending refund
PLACED --seller reject-------------> REJECTED  + pending refund
PLACED --seller accept-------------> ACCEPTED
ACCEPTED --------------------------> PREPARING
PREPARING -------------------------> READY_FOR_PICKUP
READY_FOR_PICKUP --customer/seller-> COMPLETED
```

Legacy API actions accept `expectedStatus`; the canonical customer/vendor
actions infer the only valid source state server-side. Each transaction
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
| POST | `/api/orders/checkout` | Checkout `{ cartId, provider }` with required `Idempotency-Key` header. |
| GET | `/api/orders/me?status=&page=&pageSize=&fromDate=&toDate=&sort=` | Paged current-customer orders. |
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

Canonical vendor routes are `/api/vendor/orders`: list/detail plus
`/{id}/accept`, `/{id}/reject`, `/{id}/preparing`,
`/{id}/ready-for-pickup`, `/{id}/confirm-handover`, and
`/sales-summary?fromDate=&toDate=&groupBy=day|week|month`. The old
`/api/seller/orders` routes remain for existing clients.

Cart responses include `storefrontAddress`, resolved from the approved business
registration. Order responses expose the persisted value as both
`storefrontAddress` and `storefront.address`, so later registration address
changes do not rewrite historical orders.

## Realtime order updates

Authenticated customers and vendors connect to:

```text
/hubs/orders
```

The JavaScript SignalR client supplies the normal JWT through its
`accessTokenFactory`. Query-string bearer tokens are accepted only on this hub
path. Call `SubscribeOrder(orderId)` after connecting. The hub verifies customer
ownership or vendor storefront ownership before adding the connection to the
private `order:{id}` group; unauthorized callers receive a generic hub error.

After payment callbacks, cancel/pickup, accept/reject, preparation, ready and
handover transitions commit, the server emits `OrderUpdated` containing
`orderId`, `orderStatus` and `changedAtUtc`. Clients refetch the authoritative
order detail. SignalR automatic reconnect is supported and polling remains a
frontend fallback while the hub is disconnected.

## Payment boundary

Checkout creates the order, immutable item/price snapshots and a provider-neutral
`PENDING` transaction under serializable isolation. The cart remains `ACTIVE`
and locked from edits until the provider callback succeeds. `IPaymentGateway`
returns the configured payment URL; no controller accepts a client-supplied
success status.

Providers call this unauthenticated but signed endpoint:

```text
POST /api/payments/{MOMO|ZALOPAY}/callback
X-Payment-Signature: <hex HMAC-SHA256 of the exact raw request body>
```

Normalized callback JSON contains `providerReference`, `idempotencyKey`,
`amount` and `status` (`SUCCESS` or `FAILED`). Every callback is stored in
`PaymentCallbackEvents`. Invalid signatures/amounts are `REJECTED`; unknown
transactions are `UNMATCHED`; terminal repeats are `DUPLICATE`. Only an
`APPLIED` success changes the order to `PLACED`, sets `placed_at`, checks out
the cart and notifies customer/vendor. Failure changes the order to `CANCELLED`,
keeps the cart active and notifies only the customer. Callback history uses
`changed_by = NULL`.

Development enables `Payments:SandboxEnabled`; only then the legacy hidden
simulation endpoint below is available:

```text
POST /api/orders/{orderId}/payment/sandbox-confirm
```

It is excluded from Swagger and still requires the owning customer. It changes a
pending sandbox payment to `SUCCESS` and the order to `PLACED`.
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
| POST | `/api/orders/{orderId}/payment/sandbox-fail` | Owning customer, Development only: simulate a failed callback, cancelling the order while keeping its cart active. |
| POST | `/api/orders/{orderId}/refund/sandbox-confirm` | Owning customer, Development only: complete approved PENDING refunds for a successful sandbox payment. |

Prices/refund requests are integer VND. Menu prices are 1–50,000,000.
Seller edits cannot restore HIDDEN/ARCHIVED items or an admin-hidden store.
Complaints require a paid processed order, allow only one open case per order,
and cannot request more than the payment minus pending/successful refunds.
ADM-05 resolves/rejects these same complaint rows; customers see the resolution.
Order/refund/history/complaint timestamps are returned as UTC instants.

Store writes respect the existing SQL `TR_Storefronts_Phase2Gate`; an invalid
contract returns a domain error.

For EF-managed databases, apply the new migration normally. Databases imported
from the provided SQL script may report `InitialBaseline` as pending even though
the schema already exists; do not run that baseline against an existing schema.
Apply only the idempotent address migration SQL or first baseline the migration
history according to the deployment process.

## Remaining production payment work

The provider-neutral boundary, checkout URL configuration, signed callback
verification, callback audit/idempotency and refund interface are implemented.
Provider-specific MoMo/ZaloPay request signing, token acquisition, create-payment
HTTP calls, reconciliation jobs and real refund execution still require merchant
accounts and provider credentials. Do not enable production checkout until those
adapters, an HTTPS callback URL and provider sandbox certification are supplied.
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
