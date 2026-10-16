# Order tracking and pickup range

Two additions to the prepaid pickup-order flow (see `commerce-order-workflows.md`):

| Use case | What it adds |
| --- | --- |
| ORD-01 pickup range | An order may only be placed from within `PickupRange:RadiusMeters` (default 2 km) of the stall. Orders are pickup-only (there are no shippers), so a customer must be able to reach the stall while the food is fresh. |
| ORD-02 tracking | Besides the status history, the customer gets a ready-time estimate from the stall's own recent orders, how many orders are ahead, and where to collect. |

No schema change: the stall's position is its rented slot (`Storefronts.contract.slot.latitude/longitude`),
and preparation times come from the existing `OrderStatusHistory` rows.

## HTTP API

| Method | Path | Who | Purpose |
| --- | --- | --- | --- |
| GET | `/api/marketplace/storefronts/{storefrontId}/pickup-range` | anyone | The stall's pickup point plus the rule (`enforced`, `radiusMeters`, `accuracyAllowanceMeters`, `maxAccuracyMeters`). Slot coordinates are already public through discovery. |
| GET | `/api/orders/{orderId}/tracking` | owning customer | `readyEstimate`, `ordersAhead`, `pickupPoint`, `readyAt`, `arrivalNotifiedAt`. Another customer's order is `404`. |
| POST | `/api/orders/{orderId}/arriving` | owning customer | "Tôi đang đến", body `{ etaMinutes? }` (1–180). Returns `{ orderId, orderStatus, notifiedAt, alreadySent }`. |
| GET | `/api/vendor/orders/arrivals` | seller | Customers on their way to the seller's still-open orders, last 90 minutes, newest first, each with `acknowledgedAt` once answered. |
| POST | `/api/vendor/orders/{orderId}/arrival-seen` | seller | "Đã thấy": tells the customer the stall read their notice. Once per notice. |
| POST | `/api/orders/checkout` | customer | Body gains `location: { latitude, longitude, accuracyMeters }`. |
| POST | `/api/orders` (legacy) | customer | Same optional `location`. |

## ORD-01: the pickup-range rule

`PickupRangeRules.Evaluate` (`Application/Common/Geo/PickupRange.cs`), pure and unit-tested:

1. If the reported accuracy is worse than `MaxAccuracyMeters` (1,000 m) the verdict is `Inaccurate`:
   an IP or Wi-Fi fix can be kilometres off, the size of the whole circle, so nothing is decided on it.
2. Otherwise `distance` is the Haversine distance from the slot (`GeoMath.DistanceMeters`, shared with
   discovery), and the customer gets up to `AccuracyAllowanceMeters` (150 m) of their reported GPS error
   in their favour: `Within` when `distance - min(accuracy, allowance) <= RadiusMeters`.

Straight-line distance is deliberate: it needs no routing service or network call, and gives the same
answer every time. Road distance is a listed follow-up.

### Where it runs

`PickupRangeBehavior` is a MediatR pipeline step registered for exactly the two commands that create an
order (`CheckoutOrderCommand`, `PlacePrepaidOrderCommand`, both marked `IPickupRangeGated`). It runs after
`ValidationBehavior` and before the handler, so:

- neither route can skip it, and the order workflow itself is unchanged;
- a refused checkout writes nothing (no order, no payment transaction);
- the stall is the one the order will actually use: `GetCheckoutPickupPointAsync` picks the cart the same
  way `CommerceRepository.CreatePrepaidOrderAsync` does.

Trade-off: a retry with the same `Idempotency-Key` is re-checked against where the customer is now.

| Situation | Response |
| --- | --- |
| No `location` while enforced | `422` "Cần vị trí hiện tại của bạn để đặt món…" |
| Accuracy worse than the limit | `422` naming the error, asking for GPS |
| Too far | `422` naming the distance and the radius |
| Impossible coordinates (lat > 90, accuracy ≤ 0…) | `400` validation error |

The coordinates are used for the decision only: not stored, not logged.

### Configuration (`appsettings.json`)

```json
"PickupRange": {
  "Enforced": true,
  "RadiusMeters": 2000,
  "AccuracyAllowanceMeters": 150,
  "MaxAccuracyMeters": 1000
}
```

For a demo far from the seeded stalls, either emulate a position next to them (Chrome DevTools →
Sensors → Location, e.g. `16.0612, 108.2190` for Bún chả Hải Châu) or set `PickupRange:Enforced` to
`false` in `appsettings.Development.json`.

## ORD-02: the ready-time estimate

`PrepTimeEstimator` (`Application/Features/Commerce/OrderTracking`), pure and unit-tested:

- Samples: accepted → ready durations of the stall's last 50 orders marked ready in the last 30 days.
- Durations under 1 minute (statuses tapped back to back, not cooking) or over 120 minutes (a
  forgotten "ready" tap) are dropped. A stall selling only ready-made items therefore gets the
  default range; over-estimating is the safe side for a pickup.
- With at least 5 samples: the range is the 25th–75th percentile (linear interpolation, as Excel's
  PERCENTILE.INC), the typical value the median. Percentiles rather than the mean, so one outlier
  barely moves it. Fewer samples: a default 10–20 minutes, flagged `basis: "DEFAULT"`.
- Before the stall accepts (`PLACED`) only the duration is returned; once accepted, the window is
  counted from acceptance (`earliestReadyAt`/`latestReadyAt`), and `isLate` is set once it has passed.
- `ordersAhead`: paid orders at the same stall, placed earlier and not yet ready. It is reported, not
  added to the estimate: the history was measured under real load, so the queue is already in it.

The client refreshes tracking through the existing `OrderUpdated` push: its query key sits under the
order's detail key.

## ORD-02: "Tôi đang đến" (arrival notice)

Pickup orders have no courier to report progress, so the customer reports it: on an order still to
be collected (`PLACED` to `READY_FOR_PICKUP`) they tap "Tôi đang đến", with a walking ETA when the
app knows their distance. The stall's owner gets an in-app `ORDER` notification
(`related_entity_type = OrderArrival`) and the ticket on the seller's board shows "Khách đang đến"
until the order is handed over. A second tap within 2 minutes returns the notice already sent
(`alreadySent: true`) instead of pinging the stall again; a new notice publishes `OrderUpdated`, so
the seller's open order screen refreshes. An order gets at most **3 notices** (`MaxNotices`); the
fourth is `422` ("hãy gọi cho quán"). No schema change: the notice is the notification row.

The seller answers **"Đã thấy"** (`POST /api/vendor/orders/{orderId}/arrival-seen`): the customer gets
an in-app notification ("Quán đã nhận tin bạn đang đến"), the tracking read carries
`arrivalAcknowledgedAt` and `arrivalNoticeCount`, and the open tracking screen refreshes through
`OrderUpdated`. One answer per notice: a second tap returns the first (`alreadyAcknowledged: true`); a
new notice from the customer can be answered again. Only the stall's own vendor can answer another
customer's notice for their order (anyone else is `403`/`404`).

## Verification

```powershell
dotnet test StreetBiz.Backend.sln
```

`PickupRangeTests`, `OrderTrackingTests` (Application) and `OrderTrackingRepositoryTests`
(Infrastructure, SQLite) cover the rule, the estimate, the pipeline wiring and the queries. The frontend
repository has an opt-in browser check, `tests/e2e/order-tracking-live.cjs`.
