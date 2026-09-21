# Database

StreetBizDB is **database-first**. The schema is a hand-written T-SQL script, EF Core
maps onto it, and the API never creates or changes it.

The whole database is built from exactly **two scripts** in `db/`:

| Script | Contains |
| --- | --- |
| `db/StreetBiz_SQL_Server.sql` | Schema (tables, triggers, views, every CHECK constraint), reference data (roles, wards, violation catalogue) and the `__EFMigrationsHistory` stamp |
| `db/StreetBiz_Demo_Seed.sql` | Demo accounts and a full demo scenario. Development only |

Nothing else creates or alters the database: no delta scripts, no per-feature seed
files, no SQL under `docs/`.

## Build a local database

Stop the API first (an open connection blocks `DROP DATABASE`), then:

~~~powershell
# LocalDB (default)
powershell -ExecutionPolicy Bypass -File scripts/setup-local-db.ps1 -Recreate

# SQL Server in Docker (docker-compose.yml, port 1433)
powershell -ExecutionPolicy Bypass -File scripts/setup-local-db.ps1 `
  -Server "localhost,1433" -SqlUser sa -SqlPassword "<SQLSERVER_SA_PASSWORD>" -AllowNonLocalDb -Recreate
~~~

The script creates the database, applies the schema, then the demo seed (skip it with
`-SkipDemoSeed`), and writes the placeholder evidence files the seeded registrations
point at. Without `-Recreate` it refuses to touch a database that already has tables.
Both scripts must run on an empty database; the schema file is not an upgrade script.

Point the API at it with `STREETBIZ_DB_CONNECTION` (or `ConnectionStrings:StreetBizDB`).
Never run the demo seed against a shared or production database.

## Changing the database

This is the rule for everyone working in this repo:

1. Edit the `CREATE TABLE` / constraint / index **in `db/StreetBiz_SQL_Server.sql`**.
   Do not write an `ALTER` script, a patch, a "fix" script or a feature-specific `.sql`
   file, and do not put SQL under `docs/`. A change that lives in a side file is
   invisible to the next person who builds a database.
2. Add one line to the **change log** at the top of that file (newest first).
3. If demo data is affected, update `db/StreetBiz_Demo_Seed.sql` in the same change.
   New reference data (a role, a ward, a violation type) goes in section 10 of the
   schema file instead. Seed rows that need an account must use the demo accounts
   below; never invent a password hash (see the last section).
4. Update the EF model to match: the scaffolded types in
   `src/StreetBiz.Infrastructure/Persistence/ScaffoldedModels` and
   `StreetBizDbContext.cs` (hand-edit, or re-scaffold and review the diff). The
   Infrastructure tests build their SQLite schema from this model.
5. Verify on an empty database: `scripts/setup-local-db.ps1 -Recreate`, then run the
   tests and start the API.
6. In the PR, tell the team they must rebuild their local database.

Keep the seed idempotent (guard each block) so re-running it is harmless.

## Demo data

Created by `db/StreetBiz_Demo_Seed.sql`. **Every password is `Password123!`** (it meets
BR-59). Stored as BCrypt work-factor-12 hashes.

| # | Phone | Role | Ward | Purpose |
| --- | --- | --- | --- | --- |
| 1 | `0900000001` | PLATFORM_ADMIN | none | Platform administration (ADM-xx) |
| 2 | `0983000001` | WARD_AUTHORITY | 10 Hải Châu 1 | Reviews (REG/WARD-xx). `scripts/e2e-auth-onboarding.sh` signs in as this account |
| 3 | `0983000002` | WARD_AUTHORITY | 11 Thanh Khê Đông | Ward isolation checks: another ward's records return 404 |
| 4 | `0983000003` | WARD_AUTHORITY | 12 An Hải Bắc | Third officer |
| 5 | `0905000101` | VENDOR | 10 | Approved: contract, permit, fee schedule and storefront |
| 6 | `0905000102` | VENDOR | 10 | SUBMITTED with full evidence: ready for the ward to decide |
| 7 | `0905000103` | VENDOR | 11 | SUBMITTED with no evidence: shows the approval blocker (BR-07) |
| 8 | `0905000104` | VENDOR | 10 | The remaining states: UNDER_REVIEW, MORE_INFORMATION_REQUIRED, REJECTED, WITHDRAWN, DRAFT |
| 9 | `0905000201` | CUSTOMER | 10 | Has a completed order and a rejected one |
| 10 | `0905000202` | CUSTOMER | 12 | Has an order ready for pickup and one awaiting payment |

Registrations 1..10 cover every status in `CK_BusinessRegistrations_Status`, so the ward
queue, the vendor tracking list and every status chip have a live example (1 and 9 are
APPROVED with contracts 1 and 2; 2 and 3 are SUBMITTED; 4 UNDER_REVIEW; 5
MORE_INFORMATION_REQUIRED; 6 REJECTED; 7 WITHDRAWN; 8 DRAFT; 10 is a second APPROVED
registration for vendor `0905000102` with no contract, the natural receiver of a slot
transfer). BR-09 holds: vendor 4 carries several registrations but only one open one.

Also seeded: 5 wards in 4 districts of Đà Nẵng, 4 pricing zones and 30 slots (the 20
`NVL-01..20` sit on the real Nguyễn Văn Linh centreline, 10 per side; three more are
vendor proposals: PENDING, APPROVED and REJECTED), 2 active contracts with digital
permits, 6 fee periods (PAID / OVERDUE / PENDING) and 3 invoices, 2 storefronts with 12
menu items, 5 orders across COMPLETED / READY_FOR_PICKUP / PREPARING / PENDING_PAYMENT /
REJECTED, 15 violation types and penalty rates for the three wards that have an officer
(the five legal-citation types carry `legal_basis`). Officers get a
`sanction_authority_title` for their own ward.

Every queue and list has something in it:

- **Sidewalk workflows:** 3 slot transfers (PENDING, ACCEPTED_BY_RECEIVER, REJECTED), 2
  renewals (PENDING, REJECTED), 2 address changes that compete for the same slot (a ward
  conflict), 2 slot holds (valid for a day so the demo stays visible; real holds last 15
  minutes).
- **Ward compliance:** 6 permit scans (one about 95 m from its slot, one forged code), 5
  violations (awaiting sanction, unpaid penalty, paid penalty, unidentified occupier, raised
  from a customer report) and 3 vendor reports.
- **Mẫu số 01 and eKYC:** registrations 1, 2, 4, 9 and 10 carry the owner-identity fields
  with fictional ID numbers. Approved ones were confirmed by an officer; 2 and 4 were not,
  so the ward must confirm identity before approving them. Registration 1 has household
  members; 2 and 4 have recorded OCR and face-match results (4 does not match).
- **Moderation and money:** 2 community comments, 1 order review, 3 reported contents, 2
  complaints, 8 payment transactions with callback events, 1 automatic refund, plus
  notifications and 2 AI log entries.

Things to know:

- **Permit QR codes are fake.** `qr_payload` is `SEED-PERMIT-CONTRACT-n-DO-NOT-SCAN`,
  not a token signed by `IPermitTokenService`. Re-issue a permit through the API to
  test WARD-11 or BUY-02 scans.
- **Evidence files are empty images/PDFs.** The setup script writes a 1x1 JPEG and a
  one-page PDF so the reviewer's document preview works. File names must be exactly 32
  hex characters (`EvidenceFiles.IsValidFileName`) or the API rejects the URL.
- **No real SMS.** `LoggingSmsSender` prints the OTP to the API console as
  `[DEV-SMS] To 0905123456: Your StreetBiz verification code is 482913.` Read it with
  Git Bash `tee`; PowerShell 5.1 `Tee-Object` writes UTF-16 and breaks `grep`.
- **Ward and admin accounts cannot self-register.** `RoleCodes.SelfRegisterable` is only
  CUSTOMER and VENDOR (BR-02), so those accounts exist only through this seed.
- **The API-backed AI providers need keys.** Put them in the git-ignored
  `src/StreetBiz.API/appsettings.Development.json`, never in a tracked file.

### Regenerating a password hash

Hash with the project's own algorithm (BCrypt, work factor 12,
`src/StreetBiz.Infrastructure/Identity/PasswordHasher.cs`) and replace the
`password_hash` strings in the demo seed:

~~~csharp
// throwaway project referencing BCrypt.Net-Next 4.0.3
Console.WriteLine(BCrypt.Net.BCrypt.HashPassword("NewPassword!1", 12));
~~~

Never invent a hash and never put a plaintext password in `password_hash`:
`PasswordHasher.Verify` returns false and sign-in fails without a clear error.

## EF Core model

StreetBizDB predates the EF Core code, so:

- `InitialBaseline` has empty `Up()`/`Down()` by design; its Designer file and the model
  snapshot capture the current model. The schema file stamps `__EFMigrationsHistory`
  (`InitialBaseline` and `AddOrderStorefrontAddressSnapshot`) so EF never thinks it has
  work to do.
- The API must never call `Database.EnsureCreated()`, `Database.Migrate()`,
  `dotnet ef database update`, or any startup SQL that creates or modifies the database.
- Do not use an EF migration to change the schema. Change the schema script (above),
  then bring the model in line.

Scaffolded model types live in `Persistence/ScaffoldedModels` and are `partial`; put
extensions outside that folder if they must survive a full re-scaffold.

To re-scaffold (rarely needed; `--no-onconfiguring` is mandatory so credentials are not
generated, and `--force` overwrites, so commit first and review the whole diff):

~~~powershell
dotnet ef dbcontext scaffold "$env:STREETBIZ_DB_CONNECTION" Microsoft.EntityFrameworkCore.SqlServer --project src/StreetBiz.Infrastructure --startup-project src/StreetBiz.API --context StreetBizDbContext --context-dir Persistence --output-dir Persistence/ScaffoldedModels --no-onconfiguring --use-database-names --force
~~~

Checklist: verify connectivity, stop if a required table has no primary key, commit the
current generated files, scaffold, review type/nullability/relationship changes, rebuild
and rerun the tests. Keyless view types (`vw_PermitValidity`, `vw_VendorRating`) are
expected. Re-scaffolding never authorises changing a shared database.
