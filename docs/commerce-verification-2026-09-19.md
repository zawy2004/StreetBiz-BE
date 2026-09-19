# Commerce verification — 2026-09-19

## Result

Verified sales/order workflows with live SQL Server and Chromium at 390×844
(Asia/Ho_Chi_Minh timezone). Payment/refund operations were internal sandbox
simulations, not MoMo/ZaloPay gateway transactions.

| Check | Result |
| --- | --- |
| Backend Release tests with SQL connection | 198 passed: Domain 1, Application 118, Infrastructure 61, API 18 |
| Frontend Vitest | 107 passed across 21 files |
| Frontend ESLint | Passed |
| Frontend TypeScript and production build | Passed; existing bundle-size warning remains |
| Browser page errors in final end-to-end run | None |
| Git whitespace checks | Passed |

## Live checks

- Create storefront from an approved registration and current owned contract.
- Create menu item; pause/reopen storefront; mark sold out/available.
- Paused stores disappear publicly; sold-out cart addition returns 422.
- Customer cannot manage seller stores; unrelated sellers receive 404.
- Add cart item, create a pending order, and keep unpaid orders hidden from sellers.
- Simulate payment failure, reload, and retry the same order successfully.
- Seller accepts, prepares and marks ready; customer confirms pickup.
- Customer saves a review and creates a partial-refund complaint.
- Admin resolves the complaint; customer completes the sandbox refund.
- Repeated payment/refund confirmations remain idempotent.
- Paid cancellation creates and completes a sandbox refund.
- Menu archival preserves order history and removes public availability.
- Sales satisfies net = gross - successful refunds.
- JSON timestamps carry UTC and mobile action buttons retain 48 px height.

The final run used storefront 4, menu item 13, completed order 15, cancelled
order 16 and resolved complaint 4. SQL confirmed successful payment/refund rows.
Earlier exploratory runs created orders 11–14, menu items 8–12 and complaint 3.
Test contract 5 and storefront 4 were retained for inspection. No schema changes
or broad cleanup were performed.

## Production boundary

Real payment creation/redirect, signed gateway callbacks, reconciliation,
late-callback handling and actual provider refunds are not implemented. Merchant
credentials and a public HTTPS callback environment were not provided.
Production checkout therefore remains unavailable.

See [commerce-order-workflows.md](commerce-order-workflows.md) and
[commerce-live.postman_collection.json](commerce-live.postman_collection.json).
The repeatable browser test is in StreetBiz-FE/tests/e2e/commerce-live.cjs.
