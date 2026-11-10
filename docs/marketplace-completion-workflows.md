# Marketplace completion workflows

This page covers the marketplace and ward use cases added or tightened on top of the
earlier modules: STORE-03, REV-02, REV-03, DISC-05, PAY-02, SIDE-10, WARD-06 and AIC-02.
No database migration is needed: every rule below uses existing tables and columns.

## STORE-03: business hours

| Method and route | Behaviour |
| --- | --- |
| `GET /api/seller/storefronts/{storefrontId}/business-hours` | The weekly windows, the hours the ward rents the pitch for (`allowedWindow`, with `overnight`), the open switch and whether the stall is open right now (Da Nang time). |
| `PUT /api/seller/storefronts/{storefrontId}/business-hours` | Replaces the whole week. |

- A window is `HH:mm` to `HH:mm`, ISO weekday 1 (Monday) to 7; up to 3 windows a day and 21
  a week. Windows of one day may touch (close 12:00, open 12:00) but not overlap.
- A window must sit inside the hours the ward rents the pitch for. A zone with no hours set
  means no limit; a zone that runs overnight accepts windows in the evening or the early part.
- No windows at all means "governed by the open switch", the same rule the customer filter
  "đang mở" applies.
- Validation errors name the offending entry (`hours[1]`) in the standard 400 `errors` map.

## REV-02: review editing window

A customer may edit their own review for 7 days from when it was first submitted
(`ReviewRules.EditWindowDays`). The review returned by `GET /api/orders/{orderId}/review`
carries `editableUntil`; `PUT` after that instant is refused with the message in
`ReviewRules.EditWindowClosed`. Editing does not move the window.

## REV-03: report a storefront or a dish

| Method and route | Behaviour |
| --- | --- |
| `POST /api/marketplace/storefronts/{id}/reports` | Report a public storefront. |
| `POST /api/marketplace/menu-items/{id}/reports` | Report a dish. |

Body: `{ "reasonCode", "detail" }`. Reason codes: `MISLEADING`, `INAPPROPRIATE`,
`UNSAFE_OR_ILLEGAL`, `COUNTERFEIT`, `OTHER` (`OTHER` needs a detail of at least 10 characters;
detail is at most 400). A signed-in account only:

- content that does not exist or is already hidden: 404;
- reporting your own content: 422;
- the same content again while your earlier report is pending: 409;
- more than 10 reports in an hour: 429 with `Retry-After`.

The report lands in the platform administrator's queue (ADM-03). The audit row is
`CONTENT_REPORT_CREATE`.

## DISC-05: rating and price filters

`GET /api/marketplace/storefronts` and `GET /api/marketplace/menu-items` accept `minRating`
(1 to 5; the community rating shown on a card, unrated storefronts drop out) and `maxPrice`
(a storefront stays when at least one of its dishes is at or under the price).

## PAY-02: paying an order, and ZaloPay

- Checkout is guarded against replay: a checkout call that repeats the idempotency key of an order
  that is already paid, or whose payment ended (failed or cancelled), is answered with 409 instead of
  being treated as new. Retrying a failed payment is a new checkout, hence a new order; the order
  screen offers that as a "Thanh toán lại" button.
- ZaloPay Gateway v2 sits beside MoMo behind `ConfiguredPaymentGateway`. It is written from
  ZaloPay's public documentation (create order, MAC over `app_id|app_trans_id|app_user|amount|app_time|embed_data|item`
  with `Key1`, callback verified with `Key2`, status query) and **has not been tested against
  ZaloPay's real sandbox**: it stays off until `Payments:ZaloPay:Enabled` is `true` and every
  setting below is present.
- `POST /api/payments/zalopay/callback` answers in ZaloPay's own shape
  (`return_code` 1 done, 2 already processed, -1 rejected) and feeds the same shared handler as
  the other providers, so a replayed callback is idempotent.

Settings (user-secrets or environment variables, never in `appsettings.json`):
`Payments:ZaloPay:Enabled`, `ApiBaseUrl`, `AppId`, `Key1`, `Key2`, `ReturnUrl`,
`FeeReturnUrl`, `PenaltyReturnUrl`, `CallbackUrl`. Environment names use double underscores,
for example `Payments__ZaloPay__Key1`.

## SIDE-10 / BR-22: giving back the old slot after an address change

An open address-change request that names the contract of the slot to give back
(`released_contract_id`) keeps that slot with the vendor for a grace period of
`AdjacentSlotRelease.GraceDays` = 7 days from the request, plus any extension the ward granted.
The hourly sweep in `FeeReminderHostedService` then:

- releases the old slot once the grace period is over and nothing is owed on it (audit
  `SIDE10_RELEASE`, notification `SLOT_RELEASED`);
- while something is owed, keeps the slot with the vendor and tells them at most once a day
  (`SLOT_RELEASE_BLOCKED`);
- is idempotent: a released contract is no longer active, so the next run skips it.

The ward officer may `EXTEND_GRACE` (another 7 days; audit `WARD_EXTEND_GRACE`) or
`CANCEL_RELEASE` on the address-change case. The vendor sees the release date on their
address-change screen.

## WARD-06 and AIC-02

- WARD-06: asking an applicant for more information notifies them in the same transaction as
  the status change.
- AIC-02: the AI document check now compares the ID number read from the card against the
  number the vendor declared; a difference is raised as a finding for the officer (the check
  never rejects an application by itself, BR-41).

## Verification

```powershell
dotnet build StreetBiz.Backend.sln
dotnet test StreetBiz.Backend.sln --no-build
```
