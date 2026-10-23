# Platform administration workflows

This module implements ADM-01 through ADM-07 against the existing
StreetBizDB schema. Every endpoint requires a valid JWT whose user is still an
active `PLATFORM_ADMIN` in the database; trusting only the role claim is not
sufficient.

## API surface

| Use case | Method and route | Behaviour |
| --- | --- | --- |
| Session | `GET /api/platform/me` | Revalidates the platform administrator session. |
| ADM-01 | `GET /api/platform/food-categories` | Lists categories with menu-item usage counts. |
| ADM-01 | `POST /api/platform/food-categories` | Creates a unique category. |
| ADM-01 | `PUT /api/platform/food-categories/{id}` | Renames an existing category. |
| ADM-01 | `DELETE /api/platform/food-categories/{id}` | Deletes only a category with no menu items. |
| ADM-02 | `GET /api/platform/accounts` | Lists Customer and Vendor accounts; supports `role`, `status`, `search` (name or phone), `page`, `pageSize`. Phone numbers are masked. |
| ADM-02 | `GET /api/platform/accounts/{userId}` | Account detail with the last 20 suspend/reactivate events. |
| ADM-02 | `POST /api/platform/accounts/{userId}/suspend` | Body `{ "reason", "expectedStatus": "ACTIVE" }` (reason 3-500 characters). |
| ADM-02 | `POST /api/platform/accounts/{userId}/reactivate` | Body `{ "expectedStatus": "SUSPENDED" }`. |
| ADM-06 | `GET /api/platform/dashboard` | Platform figures for `from`/`to` (Vietnamese calendar days, default last 30, at most 366) against the equally long period before, plus a snapshot of the platform right now. |
| ADM-07 | `POST /api/platform/reports/export` | Builds `OPERATIONS_SUMMARY`, `ORDERS`, `MODERATION` or `ACCOUNTS` as `xlsx` or `csv` and returns the file (`X-Export-Id` header). |
| ADM-07 | `GET /api/platform/reports/exports` | The caller's own export history, newest first. |
| ADM-07 | `GET /api/platform/reports/exports/{exportId}/download` | Downloads a finished export again (only the administrator who made it). |
| ADM-03 | `GET /api/platform/reported-content` | Lists reports; supports `status`, `page`, and `pageSize`. |
| ADM-03 | `GET /api/platform/reported-content/{id}` | Returns the report and a live content snapshot. |
| ADM-03 | `POST /api/platform/reported-content/{id}/dismiss` | Dismisses an unsubstantiated pending report. |
| ADM-04 | `POST /api/platform/reported-content/{id}/hide` | Soft-hides the content and closes duplicate pending reports. |
| ADM-04 | `POST /api/platform/reported-content/{id}/restore` | Undoes a hide (body `{ "expectedStatus": "HIDDEN" }`). Every report that kept the content hidden becomes `DISMISSED`; a dish returns as `SOLD_OUT`, a storefront stays `PAUSED`, so nothing goes on sale until the seller reopens it. The seller is notified and the action is audited as `ADM_CONTENT_RESTORE`. |
| ADM-05 | `GET /api/platform/order-complaints` | Lists complaints with payment/refund context. |
| ADM-05 | `GET /api/platform/order-complaints/{id}` | Returns a complaint detail. |
| ADM-05 | `POST /api/platform/order-complaints/{id}/decision` | Resolves/rejects a complaint and optionally requests a refund. |

Mutation bodies use `expectedStatus` as an optimistic-concurrency guard. A
stale moderation or complaint page receives HTTP 409 and must reload before
retrying.

## Account suspension (ADM-02)

- Only `CUSTOMER` and `VENDOR` accounts can be managed here; ward officers and other
  administrators are outside this screen, and an administrator cannot suspend themselves.
- Suspending revokes every open session, pauses the vendor's `OPEN` storefronts and
  notifies the user. Reactivating does **not** reopen the storefronts: the vendor does that,
  so nothing goes back on sale by itself. Both actions are audited
  (`ADM_ACCOUNT_SUSPEND`, `ADM_ACCOUNT_REACTIVATE`, with the reason, the number of
  sessions revoked and storefronts paused) in the same serializable transaction as the
  status change and the notification.
- A stale page (`expectedStatus` no longer true) gets HTTP 409.

## Dashboard and export (ADM-06, ADM-07)

- The dashboard counts users, orders (placed/completed/cancelled/rejected), GMV, reports and
  review time, and the open work queues. The previous period is the same number of days
  immediately before, so changes are like for like.
- Exports carry no password or token data and no customer contact details (the `ORDERS`
  sheet has no customer columns); phone numbers in the `ACCOUNTS` report are masked. A request is limited
  to 366 days and 50,000 rows, and CSV cells that start with `=`, `+`, `-`, `@` are
  neutralised against spreadsheet formula injection. Every export is recorded in
  `ReportExports` and audited (`ADM_REPORT_EXPORT`).
- Platform administration has no authority over permits, rental contracts, fees or
  penalties; those figures belong to the Ward Authority and are not in these reports.

## Moderation and refunds

- Hiding a menu item changes its availability to `HIDDEN`.
- Hiding a storefront changes its availability to `PAUSED`.
- Reviews have no status column in the current database, so a `HIDDEN`
  `ReportedContent` row is the soft-hide marker that public review queries must
  exclude.
- Complaint refunds are permitted only for `REFUND_REQUEST` complaints with a
  successful order payment. The new amount plus every non-failed refund cannot
  exceed either the requested amount or the paid amount.
- Resolving a complaint creates a `PENDING` refund transaction; payment-provider
  execution/webhooks remain a separate responsibility.

Every successful mutation writes an audit log. Moderation and complaint
decisions also create an in-app notification in the same serializable database
transaction.

## Verification

From `StreetBiz-BE`:

```powershell
dotnet build StreetBiz.Backend.sln
dotnet test StreetBiz.Backend.sln --no-build
```

No migration is required because these workflows use existing tables and
columns.
