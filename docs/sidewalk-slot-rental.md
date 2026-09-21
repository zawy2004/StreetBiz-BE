# Module: Sidewalk Slot & Rental

Implements the **Sidewalk Slot & Rental subsystem (SIDE-01…SIDE-13)** from
Report 3 (SRS): a vendor browses the ward's slot map, applies for a slot,
tracks the application and the resulting contract, requests renewal or
returns it, views the digital QR permit, proposes a new slot at an unlisted
address, requests an address change, and transfers a slot to another vendor.
Built on the Authentication & Vendor Onboarding module
([auth-vendor-onboarding.md](auth-vendor-onboarding.md)).

Ward-side review is implemented in the ward modules: WARD-07/08 approve a rental
application and issue the contract, permit and fee schedule, and WARD-16/17/18 are in
[ward-slot-workflows.md](ward-slot-workflows.md). WARD-09 (renewal approval) has no
endpoint yet. The demo seed (`db/StreetBiz_Demo_Seed.sql`) already contains active
contracts and permits; how to test is in
[testing/sidewalk-slot-rental.md](testing/sidewalk-slot-rental.md).

## Endpoints

| Use case | Endpoint |
|---|---|
| SIDE-01 Browse slot map | `GET /api/sidewalk-slots?lat=&lng=&radiusMeters=`, `?minLat=&maxLat=&minLng=&maxLng=`, or `?zoneId=` (auth); add `&includeUnavailable=true` to also return rented/suspended slots |
| SIDE-02 Slot detail | `GET /api/sidewalk-slots/{slotId}` (auth) |
| SIDE-02 Zone info (regulation, segment ends, deadline, ward contact, fee table, street features) | `GET /api/sidewalk-zones/{zoneId}` (auth) |
| SIDE-02 Price estimate for a term | `GET /api/sidewalk-slots/{slotId}/quote?termDays=` (auth) |
| Hold a slot for 15 minutes | `POST /api/vendor/slot-holds` (auth) — `{ registrationId, slotId }`; holding again renews |
| List / release holds | `GET /api/vendor/slot-holds?registrationId=`, `DELETE /api/vendor/slot-holds/{slotId}?registrationId=` (auth) |
| SIDE-03B Apply for an open slot | `POST /api/vendor/rental-applications/open-slot` (auth) — body must carry `commitmentsAccepted: true` |
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
[testing/sidewalk-slot-rental.md](testing/sidewalk-slot-rental.md).

A transfer request carries `slotCode`, `zoneName`, `contractStartDate` and `contractEndDate` of the contract being handed over,
in both directions. Only the contract's holder can read the contract itself (`GET /api/vendor/rental-contracts/{id}`), so without
these fields the receiver could not tell which slot they are being asked to accept.

## Slot workspace (vendor "Ô thuê" screen)

The redesigned `/vendor/slots` screen needs data the original schema did not
have. It is part of `db/StreetBiz_SQL_Server.sql` (see [database.md](database.md));
rebuild the local database to get it.

- **Slot detail**: `SidewalkSlotDto` now also carries `imageUrl`, `hasPower`,
  `hasWater`, `hasTrashBin`, `businessCategory` (`FOOD_BEVERAGE`, `RETAIL`,
  `SERVICES`, `CRAFTS`, `GENERAL`; advisory, never enforced on an application),
  `tenantName` (only for a slot with an ACTIVE contract) and `holdExpiresAt`.
- **Zone info** lives on `PricingZones` (`zone_code`, `regulation_ref`,
  `segment_from/to`, `application_deadline`), the ward contact on
  `AdministrativeUnits`, the fee table in `ZoneFeeComponents` and technical
  corridors / street furniture in `StreetFeatures` (`blocks_business = 1` means
  no slot can operate there). There is no ward-side UI to edit any of it yet;
  it enters through SQL or the demo seed.
- **Quote** = `price_per_day x days` plus each fee component (`PER_DAY` lines
  times the days, `PER_TERM` lines once). It is informational only -- the real
  fee schedule is generated at WARD-08 -- and is never stored.
- **Holds** reserve an AVAILABLE slot for `Sidewalk:SlotHoldTtlMinutes` (15)
  and are capped at `Sidewalk:MaxSlotHoldsPerRegistration` (3) per
  registration. `SlotHolds` has `PRIMARY KEY (slot_id)`, so two simultaneous
  requests cannot both win: the loser gets a 409. Expired rows are never swept;
  every read ignores rows whose `expires_at` has passed ("lazy expiry"), and a
  new hold takes over an expired row with one conditional UPDATE. Only
  `holdExpiresAt` is exposed, never who holds the slot. While someone else's
  live hold exists, applying for the slot (open-slot or adjacent) is a 409; a
  successful application releases the applicant's own hold.
- **Commitments**: `commitmentsAccepted` must be `true` on an open-slot
  application (400 otherwise) and `RentalApplications.commitments_accepted_at`
  stores when.

## Design decisions (reconciled with the database)

- **Distance**: the database stores plain `DECIMAL(9,6)` latitude/longitude, no
  spatial type. `GeoMath` (`Application/Common/Geo`) computes Haversine
  distance and a bounding box in application code — SIDE-01 filters coarsely
  by bbox in SQL, then exactly by radius in memory (`Math.Atan2` doesn't
  translate to SQL). BR-11's adjacency radius is `Sidewalk:AdjacentRadiusMeters`
  (default 150m), a deliberate simplification since the schema has no
  per-ward radius column.
- **Four mis-scaffolded 1:1 relationships** (`DigitalPermit.contract`,
  `RenewalRequest.contract`, `AddressChangeRequest.registration`,
  `FeeSchedule.contract`) were reconfigured as 1:many in
  `Infrastructure/Persistence/Configurations/ModelFixups.cs` via the
  `OnModelCreatingPartial` hook — the scaffolder read a *filtered* unique
  index (only the currently-open/current row) as an unconditional one; the SQL
  schema itself is correct as-is and was not touched. `Storefront.contract`
  and `Storefront.registration` looked similar but are **not** a bug —
  `UQ_Storefronts_Contract`/`UQ_Storefronts_Registration` are genuine
  unconditional `UNIQUE` constraints, so EF's 1:1 mapping there is correct.
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
  not exist yet — that's why the demo seed inserts a
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

`Sidewalk:AdjacentRadiusMeters`, `Sidewalk:SlotHoldTtlMinutes`,
`Sidewalk:MaxSlotHoldsPerRegistration` and the `Nominatim:*` section have
working defaults for local development (the `Sidewalk:*` ones live in
`SidewalkSettings`, not `appsettings.json`).

## Build & run

~~~powershell
dotnet build StreetBiz.Backend.sln
dotnet run --project src/StreetBiz.API
~~~

## New NuGet packages

- Infrastructure: `Microsoft.Extensions.Http`, `Microsoft.Extensions.Caching.Memory`
