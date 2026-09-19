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

Owns future use-case contracts, DTOs, validators, mapping profiles, MediatR
behaviors, and feature folders. DependencyInjection.cs registers MediatR,
FluentValidation, and AutoMapper. No command, query, or use case is present.

### Infrastructure

Owns SQL Server persistence and future adapters for identity, payments,
notifications, file storage, and external services. StreetBizDbContext and all
database-first model types currently live here because they reflect persistence
concerns.

### API

Is the composition root. It configures dependency injection, Serilog, Swagger,
exception handling, HTTPS redirection, and the SQL Server health check. It
contains no business controller.

## Runtime configuration

The database connection is resolved from STREETBIZ_DB_CONNECTION first, then
ConnectionStrings:StreetBizDatabase. Missing configuration fails startup.
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
