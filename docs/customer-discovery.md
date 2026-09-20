# Customer discovery (DISC-01 .. DISC-06)

How a customer finds an open storefront: detect where they are, pick a service area, browse, search,
filter, and open one storefront in full. All endpoints are public (a guest can browse).

| Use case | Backend | Frontend |
|---|---|---|
| DISC-01 Detect current location | none -- the browser reports it; the position is sent as `latitude`/`longitude` and only measures distance | `LocationBar`, `discovery-store`, `geolocation.ts` |
| DISC-02 Select service area | `GET /api/marketplace/service-areas` | area select in `LocationBar` |
| DISC-03 Browse open storefronts | `GET /api/marketplace/storefronts` | Explore -> "Quán ăn" tab |
| DISC-04 Search storefront / menu item | `?query=` on `storefronts` and `menu-items` | Search screen |
| DISC-05 Filter search results | `wardId`, `categoryId`, `openNow`, `radiusMeters`, `minPrice`/`maxPrice`, `sort`; `GET /api/marketplace/categories` | `FilterBar`, sort control |
| DISC-06 View storefront / menu item | `GET /api/marketplace/storefronts/{id}`, `GET /api/marketplace/menu-items/{id}` | Storefront detail screen, item screen |

## Rules

- **What is listed.** A storefront is public only while `availability_status = OPEN`, its rental contract is
  `ACTIVE`, its registration is `APPROVED` and the vendor account is `ACTIVE`. Storefront list, ward counts,
  category counts and menu-item search all start from the same predicate, so they cannot disagree. Only
  `AVAILABLE` and `SOLD_OUT` items are shown.
- **Service area.** A ward that has at least one listed storefront, counted by the ward of the storefront's slot
  zone. No ward polygons are configured, so "nearest area" is the ward whose nearest storefront is closest to the
  customer, not a point-in-polygon test.
- **Opening hours.** `StorefrontBusinessHours.day_of_week` is ISO (1 = Monday ... 7 = Sunday), times are local
  Vietnam time (UTC+7, no daylight saving). Open = a window of today covers now (opening inclusive, closing
  exclusive). A storefront with **no hours at all** is governed only by its own open switch and counts as open.
  `openNow` is applied in SQL (`StorefrontsWhere`) and `isOpenNow` is computed by `StorefrontHours.IsOpen`; both
  follow the same rule and each is tested.
- **Distance.** `latitude` and `longitude` go together; `radiusMeters` needs them; `sort=distance` needs them.
  SQL narrows by bounding box (max 500 candidates), Haversine does the exact cut in memory.
- **Sorting.** Storefronts: `distance` (default with a position), `rating`, `name` (default without one; Vietnamese
  order, "Đ" after "D"). Menu items: `name` (storefront, then item), `price_asc`, `price_desc`.
- **Search text.** Matches storefront name/description and item/category names. On SQL Server the comparison is
  accent-insensitive (`Latin1_General_100_CI_AI`), so `bun cha` finds "Bún chả" and `do uong` finds "Đồ uống".
  Matching a storefront's name returns all of its items from the menu search.

## Local data

Storefronts are created by STORE-01, which is not implemented yet. `docs/dev-seed-discovery.sql` adds two
storefronts (with hours and menus) for the first two registrations that pass the Phase 2 gate; it is idempotent
and only adds missing rows.

## Not covered

- No point-in-polygon ward detection (needs verified ward boundaries).
- Menu-item search has no distance filter (dishes carry no position of their own; use the storefront list).
- No accent folding on SQLite (tests only).
