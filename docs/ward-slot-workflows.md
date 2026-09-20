# Ward slot workflows: WARD-16, WARD-17, WARD-18, SYS-01, SYS-02

## Implemented scope

| Use case | API and behavior |
| --- | --- |
| WARD-16 | List, inspect, approve or reject proposed slots. Approval requires authoritative ward geofence membership. Rejection records a reason and makes the proposed slot unavailable. |
| WARD-17 | List address-change conflicts, enqueue or reject. Queue membership uses UNDER_REVIEW, ordered by reviewed_at then address_change_id. Existing tenants and the applicant's previous contract are untouched. |
| WARD-18 | Inspect and decide transfers. Approval requires ACCEPTED_BY_RECEIVER, accepted_at, active in-term contract, unchanged owner, eligible approved recipient registration, and no outstanding fees or slot/contract penalties. |
| SYS-01 | Explicit address search through a configurable Nominatim-compatible provider, manual coordinate entry and persistence of a pending proposal's pin. |
| SYS-02 | Server-side ward containment using NetTopologySuite MultiPolygon with holes, boundary-inclusive Covers. Missing or invalid boundaries return 503 instead of assuming containment. |

All decisions require a 1–500 character reason. The reviewer ID and ward come
from the authenticated database account, never a client-supplied ward or user.
SQL Server SERIALIZABLE transactions include current-state checks, updates,
AuditLog and in-app Notification records. Stale decisions return 409. No push
delivery provider is implemented by this feature.

The implementation uses the current generated models in Infrastructure.
Application exposes transport records and service ports. The partial DbContext
configuration disables SQL Server OUTPUT for RentalContracts because the
existing database has triggers on that table. No schema migration is required.
No migration, schema change or seed operation is performed on application startup.

## Run Backend

The API still needs the existing StreetBizDB connection configuration. Open
StreetBiz.Backend.sln in Visual Studio, set StreetBiz.API as Startup Project,
and use the https profile. Swagger opens at https://localhost:7147/swagger.

Right-click StreetBiz.API → Manage User Secrets. Merge the following configuration
with the existing ConnectionStrings object. Use independent random secrets of at
least 32 characters; never put them in tracked appsettings or VITE_* variables.

```json
{
  "Jwt": {
    "SigningKey": "<random-signing-secret>",
    "Issuer": "StreetBiz",
    "Audience": "StreetBiz"
  },
  "WardDevelopment": {
    "AccessKey": "<different-random-development-secret>",
    "UserId": "<existing-active-ward-user-id>"
  },
  "Nominatim": {
    "BaseUrl": "https://<your-approved-nominatim-compatible-provider>/",
    "UserAgent": "StreetBiz/1.0 (your-contact-address)",
    "TimeoutSeconds": 5
  },
  "Sidewalk": {
    "AdjacentRadiusMeters": 150
  },
  "Geolocation": {
    "Boundaries": []
  }
}
```

To generate each secret in PowerShell 7:

```powershell
[Convert]::ToBase64String([System.Security.Cryptography.RandomNumberGenerator]::GetBytes(32))
```

The development session endpoint POST /api/dev/ward-session exists only in
Development. It accepts { "accessKey": "..." }, returns a 30-minute JWT, and binds
it to the configured UserId after checking the real UserAccounts record. It does
not accept a selectable user or ward. It is rate-limited to five attempts/minute
per IP. In Production, sign in through the existing authentication module and
use its access token. Ward endpoints share the same Jwt issuer, audience and
HS256 signing key, including the persisted login-session revocation check.

UserAccounts must have role_code=WARD_AUTHORITY, account_status=ACTIVE and a
non-null ward_unit_id. Every authenticated request rechecks that database account.
Changing or suspending the account invalidates its access without trusting stale
role claims. Example read-only query to find an eligible account:

```sql
SELECT user_id, full_name, ward_unit_id
FROM dbo.UserAccounts
WHERE role_code = 'WARD_AUTHORITY'
  AND account_status = 'ACTIVE'
  AND ward_unit_id IS NOT NULL;
```

If no rows exist, provision the ward account using your team's approved data
setup. Mock frontend identities cannot authenticate to this API.

## Configure ward boundaries

Each item in Geolocation:Boundaries must have:

- WardId: actual AdministrativeUnits.unit_id.
- Version: identifier/date of the approved administrative boundary source.
- Polygons: MultiPolygon coordinate nesting: polygons → rings → positions →
  [longitude, latitude]. First ring is the outer boundary, remaining rings are holes.

Every ring must be closed (first and last coordinate equal), have at least four
positions and form valid geometry. Multiple separated polygons are supported.
Do not infer a ward boundary from a bounding box, a pricing zone name or one point.
No sample geometry is enabled in the application.

Configure Sidewalk:AdjacentRadiusMeters from the approved rule. WARD-18 and the
vendor adjacent-slot flow intentionally use this same policy.
ITINERANT recipients still require an approved registration in the same ward.

Search results are only candidate coordinates: they never constitute a verified
ward boundary. The provider must support Nominatim search with jsonv2 output;
configure a provider/deployment whose usage policy permits the anticipated load.
No autocomplete, public-provider default or bulk geocoding is enabled.

## Run Frontend

In StreetBiz-FE/.env.local:

```dotenv
VITE_API_BASE_URL=https://localhost:7147/api
```

Trust the .NET HTTPS development certificate, then run:

```powershell
dotnet dev-certs https --trust
dotnet run --project src/StreetBiz.API --launch-profile https
# In a second terminal, from StreetBiz-FE:
npm run dev
```

Open http://localhost:5173/ward/inbox/reviews. The legacy /ward-reviews URL
redirects here. This workspace uses real Backend
authentication independently from the existing mock login screens.
In a Vite development build, choose "Khóa phát triển cục bộ" and enter the
WardDevelopment:AccessKey configured above; alternatively paste a valid access token.
Only the token is stored in sessionStorage, not the development key.
The three tabs load real database records and show an empty state when none exist.
The existing Ward dashboard and inbox link to this real-data workspace.

Change Cors:AllowedOrigins if the frontend runs on another origin; the development
defaults allow localhost:5173 and 127.0.0.1:5173. Do not enable wildcard origins.

## API contract

All /api/ward routes require Authorization: Bearer <token>.

| Method | Path | Request |
| --- | --- | --- |
| GET | /api/ward/me | None |
| GET | /api/ward/cases/{kind}?page=1 | kind=proposals, conflicts, transfers; 20 records/page |
| GET | /api/ward/cases/{kind}/{id} | Positive database identifier |
| POST | /api/ward/cases/{kind}/{id}/decision | decision, reason, expectedStatus |
| PUT | /api/ward/cases/proposals/{id}/location | latitude, longitude |
| GET | /api/ward/geo/search?address=... | Explicit address query |
| POST | /api/ward/geo/verify | latitude, longitude |

Decision values: proposals/transfers use APPROVE or REJECT; conflicts use QUEUE
or REJECT. Clients display the server-provided actions and blockers, but the
server repeats all checks inside the write transaction. IDs are serialized as
strings to preserve SQL bigint precision in JavaScript.

Errors: 400 invalid input, 401 invalid/expired token, 403 inactive/unauthorized
account, 404 record outside the caller's ward or absent, 409 stale/non-actionable
state, 422 point outside ward, 429 throttled, and 503 missing/invalid ward boundary.
An unavailable geocoder returns no candidates so manual coordinates remain usable.
Business errors use ProblemDetails with title and code.

## Data prerequisites and boundaries

- WARD-16: source=VENDOR_PROPOSED, proposal_review_status=PENDING, proposed
  registration and pricing zone belonging to the reviewer ward. The SQL constraint
  also requires proposal_photo_url. Existing tables and upstream proposal submission
  are reused; this task does not implement SIDE-11.
- WARD-17: AddressChangeRequest must identify requested_new_slot_id. Occupancy
  is calculated from ACTIVE/SUSPENDED contracts with a non-expired end date.
  UNDER_REVIEW represents the explicit waiting list; dequeue/allocation when a
  slot becomes vacant belongs to the rental/address-change workflow.
- WARD-18: acceptance is ACCEPTED_BY_RECEIVER in the database, not the old mock
  UI's UNDER_REVIEW. Future unpaid fee installments also block transfer under
  the requested no-unpaid-fees rule. CANCELLED/WAIVED/PAID penalties do not.
- A transfer changes the current contract vendor and records both parties on
  SlotTransferRequest; original rental application, contract term, schedules and
  attached digital permit are preserved. The public permit view derives vendor
  identity from the current contract.
- A contract already linked to a Phase 2 Storefront is blocked with an explanation,
  because storefront ownership migration is not defined by these five use cases.
  Agree that separate policy before enabling such transfers.
- Existing Ward dashboard statistics and other modules still use their original
  mock implementation; the five use cases above use `/ward/inbox/reviews` and
  real APIs.

## Verification

```powershell
dotnet test StreetBiz.Backend.sln
# In StreetBiz-FE:
npm run typecheck
npm run lint
npm test
npm run build
```

Relational service tests use disposable SQLite databases with FK constraints and
transactions. SQL Server computed expressions are replaced in the test model;
these tests do not claim to validate the existing SQL Server triggers. HTTP
tests use TestServer and cover JWT validation, actor scoping and disabled
development endpoints in Production. Frontend tests cover reason/confirmation
requirements, blocked approval, stale-state errors and coordinate verification.
No test seeds or changes StreetBizDB.

Provider references:
[JWT bearer validation](https://learn.microsoft.com/en-us/aspnet/core/security/authentication/configure-jwt-bearer-authentication)
and [Nominatim Search](https://nominatim.org/release-docs/latest/api/Search/).
