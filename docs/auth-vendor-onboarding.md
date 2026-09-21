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

Vendor Business Registration (REG-01…05):

| Use case | Endpoint |
|---|---|
| REG-01 Submit registration | `POST /api/vendor/registrations` (auth) |
| REG-02 Upload evidence file | `POST /api/uploads/evidence` (auth, multipart `file`) → `{ fileUrl }` |
| REG-02 Attach evidence | `POST /api/vendor/registrations/{id}/evidence` (auth, `fileUrl` from the upload) |
| REG-03 Track registrations | `GET /api/vendor/registrations` (auth) |
| REG-03 Registration detail | `GET /api/vendor/registrations/{id}` (auth) → registration + evidence |
| REG-04 Update & re-submit | `PUT /api/vendor/registrations/{id}` (auth) |
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
