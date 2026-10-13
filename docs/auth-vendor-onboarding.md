# Module: Authentication & Vendor Onboarding

Implements the **Authentication subsystem (AUTH-01…AUTH-09)** from Report 3 (SRS),
built on the existing Clean Architecture scaffold (MediatR / FluentValidation /
EF Core database-first). Vendor Business Registration (REG-01…05) and the
AI / Storefront use cases are the next increments.

## Endpoints

| Use case | Endpoint |
|---|---|
| AUTH-02 Send OTP | `POST /api/auth/send-otp` |
| AUTH-01 Register | `POST /api/auth/register` |
| AUTH-03 Sign in | `POST /api/auth/login` (phone + password) |
| AUTH-03 Sign in with OTP | `POST /api/auth/login-otp` (passwordless, FE-01 / BR-03) |
| (token rotation) | `POST /api/auth/refresh` |
| AUTH-04 Sign out | `POST /api/auth/logout` (auth) |
| AUTH-07 Change password | `POST /api/auth/change-password` (auth) |
| AUTH-05 Request reset | `POST /api/auth/forgot-password` |
| AUTH-06 Reset password | `POST /api/auth/reset-password` |
| AUTH-08 List sessions | `GET /api/auth/sessions` (auth) |
| AUTH-09 Revoke session | `DELETE /api/auth/sessions/{id}` (auth) |
| AUTH-09 Sign out other devices | `DELETE /api/auth/sessions` (auth; keeps the current session) |
| Security history | `GET /api/auth/login-history?take=20&before={id}` (auth; own events, newest first) |

Vendor Business Registration (REG-01…05):

| Use case | Endpoint |
|---|---|
| REG-01 Create registration (draft) | `POST /api/vendor/registrations` (auth) → status `DRAFT`, invisible to the ward |
| REG-01 File the draft | `POST /api/vendor/registrations/{id}/submit` (auth; needs the documents the vendor type requires, BR-09) |
| REG-02 Upload evidence file | `POST /api/uploads/evidence` (auth, multipart `file`) → `{ fileUrl }` |
| REG-02 Attach evidence | `POST /api/vendor/registrations/{id}/evidence` (auth, `fileUrl` from the upload) |
| REG-03 Track registrations | `GET /api/vendor/registrations` (auth) |
| REG-03 Registration detail | `GET /api/vendor/registrations/{id}` (auth) → registration + evidence |
| REG-04 Update | `PUT /api/vendor/registrations/{id}` (auth; a `DRAFT` stays a draft, a filed registration is re-filed and loses the officer's earlier identity check) |
| REG-02 Remove evidence | `DELETE /api/vendor/registrations/{id}/evidence/{evidenceId}` (auth; only while editable) |
| AIC-09 Onboarding assistant | `POST /api/vendor/assistant` (vendors only; question ≤ 500, context ≤ 1500 characters) |
| REG-05 Withdraw | `POST /api/vendor/registrations/{id}/withdraw` (auth) |
| Evidence download | `GET /api/uploads/evidence/{ownerUserId}/{file}` (auth: owner, or the reviewing ward officer) |

PLATFORM_ADMIN is deliberately **not** on that list: BR-44 keeps the platform
administrator out of registration identity evidence entirely. `UploadsController`
enforces this and `UploadsControllerTests` asserts it.

Reference data for the ward pickers (anonymous, because a guest picks a ward
before an account exists):

| Purpose | Endpoint |
|---|---|
| List wards | `GET /api/administrative-units/wards` |

How to run and test all of this end to end: [testing/auth-vendor-onboarding.md](testing/auth-vendor-onboarding.md).

## Design decisions (reconciled with the database)

- `UserAccounts.password_hash` is NOT NULL, so login is **phone + password**;
  **OTP** verifies the phone at sign-up and authorizes password reset (differs
  slightly from the SRS phone-OTP-only wording — flag for the mentor if the team
  wants pure OTP login).
- Passwords: **BCrypt** (work factor 12), policy BR-59.
- OTP: 6 digits, SHA-256 hashed in `OtpChallenges.code_hash`, 5-min validity,
  60s resend cooldown, 5-attempt lock (BR-61). The cooldown answers **429**
  `otp_cooldown` with `retryAfterSeconds` and a `Retry-After` header.
- Account enumeration (SEC-05): login failures are uniform, and password reset
  (both `forgot-password` and `send-otp` with `PASSWORD_RESET`) answers 200 for
  every phone — the cooldown is swallowed there, since only registered phones
  could hit it. `send-otp` with `REGISTRATION` does report an already-registered
  phone (409), matching what `register` reveals anyway.
- Sessions: refresh token random 32 bytes; only its **SHA-256 hash** is stored in
  `UserSessions.refresh_token_hash`. Refresh rotates (old session revoked).
- Access token: JWT (HS256) with claims `sub`, `sid`, `phone`, role. Default 60 min.
  Every authenticated request also checks that the `sid` session is still active
  (`AuthenticationSetup.ValidateSessionAsync`), so sign-out, revoking a device,
  changing and resetting the password take effect immediately rather than when
  the token expires. That check refreshes `last_active_at` at most every 5 minutes.
- A wrong current password on change-password is a **400** keyed `CurrentPassword`,
  not a 401, so clients do not mistake it for an expired token.
- Evidence files are stored through `IFileStorage` (local disk under
  `Storage:RootPath`, default `App_Data/uploads`), typed by their leading bytes
  (JPG/PNG/WEBP/PDF, max 5 MB), and never served statically. `file_url` must be a
  URL issued by the upload endpoint to the same caller, and evidence can only be
  added while the registration is editable (BR-62).
- `wardUnitId` is checked against WARD units before insert (400 `WardUnitId`)
  instead of failing on the foreign key with a 500.
- BR-09 also applies when re-submitting (REG-04): a vendor cannot put an
  application back into review while another one is SUBMITTED/UNDER_REVIEW.
- Errors map to RFC-7807 ProblemDetails via `GlobalExceptionHandler`. A 400
  carries an `errors` dictionary keyed by the FluentValidation property name
  (PascalCase), which the SPA uses to highlight individual fields.
- OTP purposes are the **database** values: `REGISTRATION` and `PASSWORD_RESET`
  (see `AppConstants.OtpPurposes`).

## Configuration

Set a real JWT signing key (>= 32 chars):

~~~powershell
dotnet user-secrets --project src/StreetBiz.API set "Jwt:SigningKey" "<a-long-random-secret>"
~~~

## Required reference data

`role_code` is a FK to `Roles`. Ensure these exist:

~~~sql
INSERT INTO Roles (role_code, role_name) VALUES
 ('CUSTOMER','Customer'), ('VENDOR','Vendor'),
 ('WARD_AUTHORITY','Ward Authority'), ('PLATFORM_ADMIN','Platform Administrator');
~~~

If your seed uses different codes, update `RoleCodes` in
`Application/Common/Security/AppConstants.cs`.

`ward_unit_id` is a FK to `AdministrativeUnits`, matched on `(unit_id,
unit_type)` where `unit_type = 'WARD'`. At least one WARD row must exist or no
registration can be submitted and the ward picker is empty.

## Build & run

~~~powershell
dotnet build StreetBiz.Backend.sln
dotnet run --project src/StreetBiz.API
~~~

The `http` profile listens on **http://localhost:5023**, which is what
`StreetBiz-FE/.env` points `VITE_API_BASE_URL` at. HTTPS redirection is disabled
in Development so the SPA's plain-HTTP preflight is not answered with a 307.

## CORS (browser clients)

`Cors:AllowedOrigins` lists the origins allowed to call the API; it defaults to
the Vite dev and preview servers when unset. Add the deployed SPA origin per
environment:

~~~json
"Cors": { "AllowedOrigins": [ "http://localhost:5173" ] }
~~~

Requests carry the token in an `Authorization` header rather than a cookie, so
the policy does not need `AllowCredentials`.

## Quick manual test

1. `POST /api/auth/send-otp` `{ "phoneNumber":"0905000001", "purpose":"REGISTRATION" }`
   → OTP is written to the API console ([DEV-SMS]).
2. `POST /api/auth/register` with that OTP → returns access + refresh token.
3. Use the access token as `Bearer` for `/sessions`, `/logout`, `/change-password`.

## New NuGet packages

- Infrastructure: `BCrypt.Net-Next`, `System.IdentityModel.Tokens.Jwt`
- API: `Microsoft.AspNetCore.Authentication.JwtBearer`

## Ward review of registrations (WARD-04…06, REG-06)

| Use case | Endpoint |
|---|---|
| Queue | `GET /api/ward/enrollments?status=&vendorType=&page=` — pending files: fast-track first, then oldest first. Drafts never appear. |
| Detail | `GET /api/ward/enrollments/{id}` |
| Take a file into review | `POST /api/ward/enrollments/{id}/claim` — `SUBMITTED` → `UNDER_REVIEW`; the vendor can no longer edit it |
| Confirm identity (BR-41) | `POST /api/ward/enrollments/{id}/confirm-identity` |
| Decide | `POST /api/ward/enrollments/{id}/decision` — `APPROVE` / `REJECT` / `MORE_INFO` with a written reason |
| Fast-track check (REG-06) | `GET /api/ward/enrollments/{id}/fast-track-check` — advisory list of met / unmet conditions |

### Registration status machine

One table (`RegistrationStatuses.CanTransition`) governs vendor edits, withdrawals and ward decisions:

~~~
DRAFT ─submit→ SUBMITTED ─claim→ UNDER_REVIEW ─┬→ APPROVED ─→ WITHDRAWN
   │              │                              ├→ REJECTED   (terminal)
   │              ├──────────────────────────────┤→ MORE_INFORMATION_REQUIRED ─edit→ SUBMITTED
   └→ WITHDRAWN   └→ WITHDRAWN                   └→ WITHDRAWN
~~~

- `REJECTED` and `WITHDRAWN` are terminal; the vendor files a new registration.
- Decisions, claims and withdrawals are conditional `UPDATE`s: if another request changed the status
  first the caller gets **409** instead of silently overwriting it.
- `APPROVE` requires the officer's identity confirmation **and** every document in
  `EvidenceTypes.RequiredFor(vendorType)`.

## Security behaviour

- **OTP:** 6 digits, HMAC-SHA256 hashed with `Otp:HashKey`, valid 5 minutes, 60 s resend cooldown,
  5 wrong attempts lock the code, at most 5 codes per phone per hour and 10 per day.
- **SMS:** `LoggingSmsSender` is Development-only. Any other environment must set
  `Sms:Provider=Http` and `Sms:Endpoint` (+ `Sms:ApiKey`), or the API refuses to start.
- **Sign-in lockout:** 5 wrong passwords lock the account for 15 minutes
  (`UserAccounts.failed_login_count` / `lockout_until`). Unknown phone numbers cost the same time as
  known ones.
- **Refresh tokens:** rotated atomically; replaying a token that was rotated more than 30 s ago
  revokes every session of that user; a login expires 30 days after it started however often it is
  refreshed. A suspended account loses access on its next request.
- **Passwords:** 8–72 characters (BCrypt ignores anything past 72 bytes).
- **Security history:** `ISecurityEvents` writes sign-in success/failure, lockouts, password
  change/reset and session revocations to `AuditLogs`; password change/reset and a lockout also
  create an in-app `SECURITY_ALERT` notification.
- **Behind a proxy:** list trusted proxy addresses in `ForwardedHeaders:KnownProxies`, otherwise
  every client shares the proxy's address and the per-IP auth limits become one global bucket.

### Configuration added

| Key | Purpose |
|---|---|
| `Otp:HashKey` | server secret for hashing OTP codes (required outside Development) |
| `Sms:Provider`, `Sms:Endpoint`, `Sms:ApiKey`, `Sms:Sender` | SMS gateway (`Http` provider) |
| `ForwardedHeaders:KnownProxies` | trusted reverse-proxy IPs |

### Schema change

`UserAccounts.failed_login_count` and `lockout_until` — rebuild the local database
(`scripts/setup-local-db.ps1 -Recreate`).
