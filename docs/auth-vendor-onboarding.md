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
| AUTH-03 Sign in | `POST /api/auth/login` |
| (token rotation) | `POST /api/auth/refresh` |
| AUTH-04 Sign out | `POST /api/auth/logout` (auth) |
| AUTH-07 Change password | `POST /api/auth/change-password` (auth) |
| AUTH-05 Request reset | `POST /api/auth/forgot-password` |
| AUTH-06 Reset password | `POST /api/auth/reset-password` |
| AUTH-08 List sessions | `GET /api/auth/sessions` (auth) |
| AUTH-09 Revoke session | `DELETE /api/auth/sessions/{id}` (auth) |

## Design decisions (reconciled with the database)

- `UserAccounts.password_hash` is NOT NULL, so login is **phone + password**;
  **OTP** verifies the phone at sign-up and authorizes password reset (differs
  slightly from the SRS phone-OTP-only wording — flag for the mentor if the team
  wants pure OTP login).
- Passwords: **BCrypt** (work factor 12), policy BR-59.
- OTP: 6 digits, SHA-256 hashed in `OtpChallenges.code_hash`, 5-min validity,
  60s resend cooldown, 5-attempt lock (BR-61).
- Sessions: refresh token random 32 bytes; only its **SHA-256 hash** is stored in
  `UserSessions.refresh_token_hash`. Refresh rotates (old session revoked).
- Access token: JWT (HS256) with claims `sub`, `sid`, `phone`, role. Default 60 min.
- Errors map to RFC-7807 ProblemDetails via `GlobalExceptionHandler`.

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

## Build & run

~~~powershell
dotnet build StreetBiz.Backend.sln
dotnet run --project src/StreetBiz.API
~~~

## Quick manual test

1. `POST /api/auth/send-otp` `{ "phoneNumber":"0905000001", "purpose":"SIGNUP" }`
   → OTP is written to the API console ([DEV-SMS]).
2. `POST /api/auth/register` with that OTP → returns access + refresh token.
3. Use the access token as `Bearer` for `/sessions`, `/logout`, `/change-password`.

## New NuGet packages

- Infrastructure: `BCrypt.Net-Next`, `System.IdentityModel.Tokens.Jwt`
- API: `Microsoft.AspNetCore.Authentication.JwtBearer`
