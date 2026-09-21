# StreetBiz Backend

WARD-16–18, SYS-01–02, BUY-01–05, ADM-01, ADM-03–05, CART-01,
ORD-01–04 and SORD-01–04 are now implemented. See
[Ward slot workflows](docs/ward-slot-workflows.md) for API contracts, authentication,
geofence configuration and test instructions, and
[Community vendor workflows](docs/community-vendor-workflows.md) for the public map,
permit verification, public profiles, customer ratings and suspicious-vendor reports.
[Platform administration workflows](docs/platform-administration-workflows.md) documents
food-category management, content moderation and order-complaint resolution. See
[Commerce order workflows](docs/commerce-order-workflows.md) for cart, prepaid order,
pickup, seller processing, refund-request, address snapshots, authenticated
SignalR updates and sales-summary contracts.

StreetBiz backend foundation built with .NET 8, ASP.NET Core, Entity Framework
Core 8, SQL Server, and Clean Architecture. The current codebase includes
authentication, vendor onboarding, sidewalk/rental workflows and the ward slot
proposal, address-conflict, transfer and geolocation workflows.

## Prerequisites

- .NET 8 SDK
- SQL Server 2022 or a compatible SQL Server instance
- dotnet-ef 8.x
- Docker Desktop (optional)

Check the SDK and install the EF CLI:

~~~powershell
dotnet --list-sdks
dotnet tool update --global dotnet-ef --version "8.*" --allow-downgrade
dotnet ef --version
~~~

Run every dotnet command below from the directory containing
StreetBiz.Backend.sln.

## Configure the database connection

The application resolves the connection string in this order:

1. Environment variable STREETBIZ_DB_CONNECTION.
2. Configuration key ConnectionStrings:StreetBizDB, including user-secrets.
3. Legacy key ConnectionStrings:StreetBizDatabase.
4. Startup fails with a clear error when none is present.

PowerShell process-scoped environment variable:

~~~powershell
$env:STREETBIZ_DB_CONNECTION = "<sql-server-connection-string>"
~~~

User-secrets alternative:

~~~powershell
dotnet user-secrets --project src/StreetBiz.API set "ConnectionStrings:StreetBizDB" "<sql-server-connection-string>"
~~~

Never commit a real password, connection string, .env file, or secrets.json.
appsettings files intentionally contain no credentials.

Payment credentials and URLs follow the same rule. Configure them with
environment variables or user-secrets:

~~~powershell
dotnet user-secrets --project src/StreetBiz.API set "Payments:Momo:CheckoutUrlTemplate" "<provider-checkout-url-template>"
dotnet user-secrets --project src/StreetBiz.API set "Payments:Momo:CallbackSecret" "<merchant-callback-secret>"
dotnet user-secrets --project src/StreetBiz.API set "Payments:ZaloPay:CheckoutUrlTemplate" "<provider-checkout-url-template>"
dotnet user-secrets --project src/StreetBiz.API set "Payments:ZaloPay:CallbackSecret" "<merchant-callback-secret>"
~~~

Equivalent environment keys use double underscores, for example
`Payments__Momo__CallbackSecret`. Callback requests send the hexadecimal
HMAC-SHA256 in `X-Payment-Signature`.

## Reverse-engineer the existing database

Confirm that STREETBIZ_DB_CONNECTION is set, then run:

~~~powershell
dotnet ef dbcontext scaffold "$env:STREETBIZ_DB_CONNECTION" Microsoft.EntityFrameworkCore.SqlServer --project src/StreetBiz.Infrastructure --startup-project src/StreetBiz.API --context StreetBizDbContext --context-dir Persistence --output-dir Persistence/ScaffoldedModels --no-onconfiguring --use-database-names --force
~~~

This command overwrites generated DbContext/model files. Inspect local changes
first and extend generated types with partial classes instead of editing generated
files directly. See docs/database.md.

## Restore, build, and test

~~~powershell
dotnet restore StreetBiz.Backend.sln
dotnet build StreetBiz.Backend.sln --no-restore
dotnet test StreetBiz.Backend.sln --no-build
~~~

## Run the API

Set the connection string, then:

~~~powershell
dotnet run --project src/StreetBiz.API
~~~

In Development, open /swagger at the URL printed by ASP.NET Core. Check SQL
Server readiness with GET /health. HTTPS redirection is enabled.

## Docker

Copy .env.example to .env and replace both placeholder values. The Compose file
contains streetbiz-api and sqlserver services. The SQL Server container only
starts the engine; it does not create, overwrite, migrate, or seed StreetBizDB.

STREETBIZ_DB_CONNECTION may point to the Compose SQL Server or to an existing
SQL Server outside Docker.

~~~powershell
docker compose up --build
~~~

## Database

StreetBizDB is database-first: the whole database is built from two scripts in `db/`
and the API never creates or changes it.

- `db/StreetBiz_SQL_Server.sql`: schema, reference data and the EF history stamp
- `db/StreetBiz_Demo_Seed.sql`: demo accounts and scenario (development only)

First-time setup on an empty database (stop the API first):

~~~powershell
powershell -ExecutionPolicy Bypass -File scripts/setup-local-db.ps1 -Recreate
~~~

Schema changes are edited into those two files, never added as separate scripts. See
[docs/database.md](docs/database.md) for the full workflow, the demo accounts and the
EF Core rules (no EnsureCreated, Migrate or migrations against this database).

## Solution layout

~~~text
StreetBiz-BE/
|-- StreetBiz.Backend.sln
|-- src/
|   |-- StreetBiz.Domain/
|   |-- StreetBiz.Application/
|   |-- StreetBiz.Infrastructure/
|   +-- StreetBiz.API/
|-- tests/
|   |-- StreetBiz.Domain.Tests/
|   |-- StreetBiz.Application.Tests/
|   |-- StreetBiz.Infrastructure.Tests/
|   +-- StreetBiz.API.Tests/
|-- docs/
|-- Dockerfile
|-- docker-compose.yml
|-- Directory.Build.props
+-- README.md
~~~

Dependencies point inward:

- Application references Domain.
- Infrastructure references Application and Domain.
- API references Application and Infrastructure.
- Domain references no other project and has no EF Core or ASP.NET Core package.

## Ward implementation

WARD-16–18 and SYS-01–02 use the existing authentication/session module,
controller/MediatR request pipeline, shared Nominatim adapter and sidewalk
adjacency policy. Ward decisions are serializable, audited and create in-app
notifications. External push delivery and payment providers remain outside this scope.

## More documentation

- docs/architecture.md
- docs/database.md
- docs/auth-vendor-onboarding.md
- docs/sidewalk-slot-rental.md
- docs/ward-slot-workflows.md
- docs/community-vendor-workflows.md
- docs/platform-administration-workflows.md
- docs/commerce-order-workflows.md
- docs/customer-discovery.md
- docs/testing/ (manual test scripts and the Postman collection)
