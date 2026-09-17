# Module: Sidewalk Slot & Rental

Implements the **Sidewalk Slot & Rental subsystem (SIDE-01…SIDE-13)** from
Report 3 (SRS): a vendor browses the ward's slot map, applies for a slot,
tracks the application and the resulting contract, requests renewal or
returns it, views the digital QR permit, proposes a new slot at an unlisted
address, requests an address change, and transfers a slot to another vendor.
Built on the Authentication & Vendor Onboarding module
([auth-vendor-onboarding.md](auth-vendor-onboarding.md)).

Ward-side review (WARD-07/08/09/16/17/18) is not implemented yet — a contract
only exists today via [dev-seed-side.sql](dev-seed-side.sql) or a manual SQL
update, documented in
[testing-sidewalk-slot-rental.md](testing-sidewalk-slot-rental.md).

## Endpoints

| Use case | Endpoint |
|---|---|
| SIDE-01 Browse slot map | `GET /api/sidewalk-slots?lat=&lng=&radiusMeters=` or `?minLat=&maxLat=&minLng=&maxLng=` (auth) |
| SIDE-02 Slot detail | `GET /api/sidewalk-slots/{slotId}` (auth) |
| SIDE-03B Apply for an open slot | `POST /api/vendor/rental-applications/open-slot` (auth) |
| SIDE-03A Apply for a storefront-adjacent slot | `POST /api/vendor/rental-applications/adjacent` (auth) |
| SIDE-04 Track applications | `GET /api/vendor/rental-applications` (auth) |
| SIDE-04 Application detail | `GET /api/vendor/rental-applications/{applicationId}` (auth) |
| SIDE-04 Withdraw application | `POST /api/vendor/rental-applications/{applicationId}/withdraw` (auth) |
| SIDE-05 List contracts | `GET /api/vendor/rental-contracts?status=` (auth) |
| SIDE-05 Contract detail | `GET /api/vendor/rental-contracts/{contractId}` (auth) |
| SIDE-06 Request renewal | `POST /api/vendor/rental-contracts/{contractId}/renewals` (auth) |
| SIDE-06 List renewals | `GET /api/vendor/rental-contracts/{contractId}/renewals` (auth) |
| SIDE-07 Cancel / return slot | `POST /api/vendor/rental-contracts/{contractId}/cancel` (auth) |
| SIDE-08 View digital permit (QR) | `GET /api/vendor/rental-contracts/{contractId}/permit` (auth) |
| SIDE-11 Propose a new slot | `POST /api/vendor/slot-proposals` (auth) |
| SIDE-11 Track proposals | `GET /api/vendor/slot-proposals` (auth) |
| SIDE-09/10 Request address change | `POST /api/vendor/address-changes` (auth) |
| SIDE-09 Track address changes | `GET /api/vendor/address-changes` (auth) |
| SIDE-12 Request slot transfer | `POST /api/vendor/slot-transfers` (auth) |
| SIDE-12/13 List transfers | `GET /api/vendor/slot-transfers?direction=outgoing\|incoming` (auth) |
| SIDE-13 Accept transfer | `POST /api/vendor/slot-transfers/{transferId}/accept` (auth) |
| SIDE-13 Decline transfer | `POST /api/vendor/slot-transfers/{transferId}/decline` (auth) |

How to run and test all of this end to end:
[testing-sidewalk-slot-rental.md](testing-sidewalk-slot-rental.md).

## Design decisions (reconciled with the database)

- **Distance**: the database stores plain `DECIMAL(9,6)` latitude/longitude, no
  spatial type. `GeoMath` (`Application/Common/Geo`) computes Haversine
  distance and a bounding box in application code — SIDE-01 filters coarsely
  by bbox in SQL, then exactly by radius in memory (`Math.Atan2` doesn't
  translate to SQL). BR-11's adjacency radius is `Sidewalk:AdjacentRadiusMeters`
  (default 150m), a deliberate simplification since the schema has no
  per-ward radius column.
- **Three mis-scaffolded 1:1 relationships** (`DigitalPermit.contract`,
  `RenewalRequest.contract`, `AddressChangeRequest.registration`) were
  reconfigured as 1:many in `Infrastructure/Persistence/Configurations/ModelFixups.cs`
  via the `OnModelCreatingPartial` hook — the scaffolder read a *filtered*
  unique index (only the currently-open row) as an unconditional one.
  `FeeSchedule` and `Storefront` have the same bug and are still unfixed,
  deferred to the FEE / Phase 2 module.
- **SQL trigger errors become typed exceptions.** `TR_RentalContracts_*`
  triggers `RAISERROR` + `ROLLBACK`, which .NET sees as `SqlException` 50000
  regardless of which trigger fired. `Infrastructure/Persistence/SqlErrorTranslator`
  matches the literal trigger message text (not just the error number) and
  maps it to a `ConflictException`/`DomainRuleException`. Every write path
  also pre-checks the same rule so the happy path never needs the translator —
  it is a safety net for races, not the primary enforcement.
- **Digital permits use HMAC-SHA256 tokens, not JWT.** `IPermitTokenService`
  (`Infrastructure/Security/PermitTokenService`) signs `contract_id` + a
  timestamp + random bytes with `Permit:SigningKey`, producing a ~60-70
  character opaque string — short enough for a small QR code, and it carries
  no PII or embedded expiry (permit validity always comes from the
  `vw_PermitValidity` view, never from the token). SIDE-08 only reads; nothing
  calls `Create` yet since permits are only ever issued by WARD-08, which does
  not exist yet — that's why [dev-seed-side.sql](dev-seed-side.sql) inserts a
  placeholder `qr_payload` directly.
- **`effectiveStatus` always comes from `vw_PermitValidity`**, never derived
  from `DigitalPermits.permit_status` alone: a cancelled contract still shows
  an ACTIVE `permit_status` unless read through the view, which folds in
  contract status and dates.
- **Geocoding (SYS-01) never blocks a write.** `IGeocodingService`
  (`Infrastructure/Geocoding/NominatimGeocodingService`) calls the free
  Nominatim (OpenStreetMap) API, throttled to 1 request/second and cached 7
  days in `IMemoryCache`; any failure or timeout returns `null` rather than
  throwing, so SIDE-09/10 and SIDE-11 always let the write through with the
  client-supplied or null coordinates.
- **SIDE-12 identifies the receiving vendor by phone number**, not
  `vendor_id` — vendors don't know each other's ids. Self-transfer is
  rejected as a 400 in the handler rather than relying on the database's
  `CK_SlotTransferRequests_DifferentVendors` check, so the failure is a clean
  validation error instead of a possible 500.
- **BR-26/BR-27 gate SIDE-12**: the receiver must hold an `APPROVED`
  registration, and the sender's contract must have no `OVERDUE` fee item or
  `UNPAID` penalty. Neither check changes `RentalContracts.vendor_id` — only
  WARD-18's approval does that; SIDE-12/13 only get the transfer to
  `ACCEPTED_BY_RECEIVER`.
- **Ownership mismatches are 403, not 404** — e.g. a contract or application
  that exists but belongs to another vendor. A record that doesn't exist at
  all is a 404.
- Errors map to RFC-7807 `ProblemDetails` via the shared `GlobalExceptionHandler`
  (same as the Auth/Registration module).

## Configuration

Set a real permit signing key (>= 32 chars), same pattern as the JWT key:

~~~powershell
dotnet user-secrets --project src/StreetBiz.API set "Permit:SigningKey" "<a-long-random-secret>"
~~~

`Sidewalk:AdjacentRadiusMeters` and the `Nominatim:*` section have working
defaults in `appsettings.json` for local development.

## Build & run

~~~powershell
dotnet build StreetBiz.Backend.sln
dotnet run --project src/StreetBiz.API
~~~

## New NuGet packages

- Infrastructure: `Microsoft.Extensions.Http`, `Microsoft.Extensions.Caching.Memory`
