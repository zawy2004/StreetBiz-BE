# Migration guide

## Current baseline

StreetBizDB predates this EF Core codebase and had no
__EFMigrationsHistory table at inspection time. The generated InitialBaseline:

- retains its Designer metadata;
- retains StreetBizDbContextModelSnapshot;
- has an intentionally empty Up method;
- has an intentionally empty Down method;
- has not been applied to StreetBizDB.

docs/InitialBaseline.sql is inspection output only. It contains no CREATE, ALTER,
or DROP operation for any existing StreetBiz table. Because migration history was
absent, the idempotent script can include creation of __EFMigrationsHistory and a
baseline history insert after the migration assembly is built. Do not execute it
without explicit approval.

## Prohibited automatic operations

The API must not call:

- Database.EnsureCreated()
- Database.Migrate()
- dotnet ef database update during startup or deployment
- custom startup SQL that creates or modifies StreetBizDB

Never empty, recreate, or edit __EFMigrationsHistory without a reviewed plan.

## Creating a future migration

First set STREETBIZ_DB_CONNECTION and make an intentional model/configuration
change. Then run from the solution root:

~~~powershell
dotnet ef migrations add <MigrationName> --project src/StreetBiz.Infrastructure --startup-project src/StreetBiz.API --context StreetBizDbContext --output-dir Persistence/Migrations
dotnet ef migrations script --idempotent --project src/StreetBiz.Infrastructure --startup-project src/StreetBiz.API --context StreetBizDbContext --output docs/<MigrationName>.sql
~~~

## Mandatory review

Before any database update:

1. Read Up and Down in full.
2. Review the SQL script in full.
3. Confirm every CREATE, ALTER, DROP, rename, and data statement is intended.
4. Check table locks, nullability, defaults, indexes, constraints, and data
   conversion risks.
5. Back up and test against a representative non-production copy.
6. Obtain explicit approval for database execution.

Stop immediately when generated SQL would recreate existing tables, drop or
truncate objects, narrow a type unsafely, remove data, or modify migration history
unexpectedly.

## Applying an approved migration

No update command is part of this foundation. Once a migration and its rollback
plan have been reviewed and explicitly approved, apply the reviewed SQL through
the team's controlled database release process. Do not make API startup
responsible for schema deployment.
