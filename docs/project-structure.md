# Project structure

## Source projects

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

## Test projects

- tests/StreetBiz.Domain.Tests references Domain
- tests/StreetBiz.Application.Tests references Application
- tests/StreetBiz.Infrastructure.Tests references Infrastructure
- tests/StreetBiz.API.Tests references API

All projects target net8.0. Common compiler settings are in
Directory.Build.props.

## Persistence files

~~~text
src/StreetBiz.Infrastructure/Persistence/
|-- StreetBizDbContext.cs
|-- ScaffoldedModels/
|   +-- 46 table models and 2 keyless view models
|-- Configurations/
+-- Migrations/
    |-- 20260909122414_InitialBaseline.cs
    |-- 20260909122414_InitialBaseline.Designer.cs
    +-- StreetBizDbContextModelSnapshot.cs
~~~

The timestamped migration name reflects the environment time at generation.
InitialBaseline has empty Up and Down methods by design.

## Empty feature directories

Some directories are intentionally empty until business implementation begins.
Their presence documents the intended ownership boundary; do not add placeholder
business logic merely to populate them.
