# Architecture

## Direction of dependencies

~~~text
StreetBiz.API
  +--> StreetBiz.Application
  +--> StreetBiz.Infrastructure
          +--> StreetBiz.Application
          +--> StreetBiz.Domain

StreetBiz.Application
  +--> StreetBiz.Domain

StreetBiz.Domain
  +--> no project dependency
~~~

All dependencies point toward the inner layers. There is no circular project
reference. Domain deliberately has no Entity Framework Core, ASP.NET Core, or SQL
Server package.

## Layer responsibilities

### Domain

Owns future entities, value objects, enums, domain events, domain exceptions, and
common domain primitives. The current foundation contains no business behavior.

### Application

Owns use-case contracts, DTOs, validators, MediatR handlers and feature folders.
DependencyInjection.cs registers MediatR, FluentValidation, AutoMapper and the
database-backed ward actor context. Features/WardSlots contains one handler per
WARD/SYS operation; handlers obtain the caller from ICurrentUser and never trust
a ward id or reviewer id supplied by the client.

### Infrastructure

Owns SQL Server persistence and future adapters for identity, payments,
notifications, file storage, and external services. StreetBizDbContext and all
database-first model types currently live here because they reflect persistence
concerns. Services/WardSlots implements ward review transactions against those
models. WardSlots implements serializable review transactions. WardGeolocation
only performs configured polygon containment; SYS-01 reuses the shared,
cached/throttled NominatimGeocodingService already used by SIDE workflows.

### API

Is the composition root. It configures dependency injection, Serilog, Swagger,
exception handling, HTTPS redirection, and the SQL Server health check. It
exposes authenticated controller routes for ward slot review and geolocation.
Controllers delegate to MediatR and share the normal JWT/session,
ProblemDetails, CORS and request-rate-limit pipeline.

## Runtime configuration

The database connection is resolved from STREETBIZ_DB_CONNECTION first, then
ConnectionStrings:StreetBizDB, and finally the legacy
ConnectionStrings:StreetBizDatabase key. Missing configuration fails startup.
Credentials are not stored in appsettings and the connection string is never
written to logs.

The API does not call EnsureCreated(), Database.Migrate(), or any automatic
migration operation.

## Persistence model strategy

The initial database-first model is isolated in
Infrastructure/Persistence/ScaffoldedModels. Generated classes and
StreetBizDbContext are partial so hand-written extensions can live in separate
files and survive future scaffolding.

During business implementation, migrate a model toward Domain only when its
invariants and behavior are understood:

1. Define a persistence-independent Domain entity.
2. Add an Application abstraction for the required persistence behavior.
3. Map the Domain entity in Infrastructure with an explicit EF configuration.
4. Keep database-specific names and relationships at the Infrastructure edge.
5. Add tests before removing or excluding the scaffolded type.

Do not move generated files mechanically into Domain.

## Project structure

### Source projects

- src/StreetBiz.Domain
  - Entities, Enums, ValueObjects, DomainEvents, Exceptions, Common
  - No outer-layer dependency
- src/StreetBiz.Application
  - Abstractions, Interfaces, DTOs, Features, Behaviors, Mappings, Validators
  - References Domain
- src/StreetBiz.Infrastructure
  - Persistence, Identity, Payments, Notifications, FileStorage, Services
  - References Application and Domain
- src/StreetBiz.API
  - Controllers, Middlewares, Filters, Extensions, Configuration
  - References Application and Infrastructure

### Test projects

- tests/StreetBiz.Domain.Tests references Domain
- tests/StreetBiz.Application.Tests references Application
- tests/StreetBiz.Infrastructure.Tests references Infrastructure (SQLite, schema built from the EF model)
- tests/StreetBiz.API.Tests references API

All projects target net8.0. Common compiler settings are in Directory.Build.props.

### Persistence files

~~~text
src/StreetBiz.Infrastructure/Persistence/
|-- StreetBizDbContext.cs
|-- ScaffoldedModels/      one model per table and 2 keyless view models
|-- Configurations/
|-- Repositories/
+-- Migrations/            InitialBaseline (empty Up/Down) and AddOrderStorefrontAddressSnapshot
~~~

The database itself is built from db/StreetBiz_SQL_Server.sql, not from these migrations;
see [database.md](database.md).
