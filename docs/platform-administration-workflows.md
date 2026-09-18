# Platform administration workflows

This module implements ADM-01 and ADM-03 through ADM-05 against the existing
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
| ADM-03 | `GET /api/platform/reported-content` | Lists reports; supports `status`, `page`, and `pageSize`. |
| ADM-03 | `GET /api/platform/reported-content/{id}` | Returns the report and a live content snapshot. |
| ADM-03 | `POST /api/platform/reported-content/{id}/dismiss` | Dismisses an unsubstantiated pending report. |
| ADM-04 | `POST /api/platform/reported-content/{id}/hide` | Soft-hides the content and closes duplicate pending reports. |
| ADM-05 | `GET /api/platform/order-complaints` | Lists complaints with payment/refund context. |
| ADM-05 | `GET /api/platform/order-complaints/{id}` | Returns a complaint detail. |
| ADM-05 | `POST /api/platform/order-complaints/{id}/decision` | Resolves/rejects a complaint and optionally requests a refund. |

Mutation bodies use `expectedStatus` as an optimistic-concurrency guard. A
stale moderation or complaint page receives HTTP 409 and must reload before
retrying.

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
