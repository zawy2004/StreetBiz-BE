-- Brings a database created from db/StreetBiz_SQL_Server.sql up to what the EF model
-- expects, then stamps __EFMigrationsHistory so EF never believes it has work to do.
--
-- Why this file exists: StreetBizDB is database-first (docs/migration-guide.md). The schema
-- file is the authoritative DDL owned outside this repo, and it currently trails the code by
-- exactly one column -- Orders.storefront_address_snapshot, added by migration
-- 20260919163132_AddOrderStorefrontAddressSnapshot. Rather than edit the schema file and lose
-- its provenance, the reviewed migration is replayed here. When the schema file is next
-- reissued with that column included, the first block below becomes a no-op and can be dropped.
--
-- Idempotent: safe to run more than once.
-- Run after db/StreetBiz_SQL_Server.sql; scripts/setup-local-db.ps1 does this for you.

SET NOCOUNT ON;
SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
GO

/* ------------------------------------------------------------------
   20260919163132_AddOrderStorefrontAddressSnapshot
   Verbatim from the migration's Up(); see
   src/StreetBiz.Infrastructure/Persistence/Migrations/20260919163132_AddOrderStorefrontAddressSnapshot.cs
   ------------------------------------------------------------------ */
IF COL_LENGTH(N'dbo.Orders', N'storefront_address_snapshot') IS NULL
BEGIN
    EXEC(N'ALTER TABLE dbo.Orders
        ADD storefront_address_snapshot nvarchar(500) NULL;');
END;
GO

-- Existing orders predate the snapshot column. Backfill once from
-- the registration address; later address changes no longer alter them.
EXEC(N'UPDATE orders
    SET orders.storefront_address_snapshot = registrations.declared_address
    FROM dbo.Orders AS orders
    INNER JOIN dbo.Storefronts AS storefronts
        ON storefronts.storefront_id = orders.storefront_id
    INNER JOIN dbo.BusinessRegistrations AS registrations
        ON registrations.registration_id = storefronts.registration_id
    WHERE orders.storefront_address_snapshot IS NULL;');
GO

/* ------------------------------------------------------------------
   Migration history
   The schema above already contains everything both migrations describe, so the rows are
   inserted rather than the migrations applied. InitialBaseline has an intentionally empty
   Up() (the schema predates EF); AddOrderStorefrontAddressSnapshot was replayed above.
   ProductVersion must match the EF Core version in Directory.Build.props / the csproj.
   ------------------------------------------------------------------ */
IF OBJECT_ID(N'[__EFMigrationsHistory]') IS NULL
BEGIN
    CREATE TABLE [__EFMigrationsHistory] (
        [MigrationId]    nvarchar(150) NOT NULL,
        [ProductVersion] nvarchar(32)  NOT NULL,
        CONSTRAINT [PK___EFMigrationsHistory] PRIMARY KEY ([MigrationId])
    );
END;
GO

MERGE [__EFMigrationsHistory] AS target
USING (VALUES
    (N'20260909122414_InitialBaseline',                 N'8.0.31'),
    (N'20260919163132_AddOrderStorefrontAddressSnapshot', N'8.0.31')
) AS source ([MigrationId], [ProductVersion])
ON target.[MigrationId] = source.[MigrationId]
WHEN NOT MATCHED THEN
    INSERT ([MigrationId], [ProductVersion])
    VALUES (source.[MigrationId], source.[ProductVersion]);
GO

SELECT
    (SELECT COUNT(*) FROM sys.tables)                                AS [tables],
    (SELECT COUNT(*) FROM sys.triggers WHERE is_ms_shipped = 0)      AS [triggers],
    (SELECT COUNT(*) FROM sys.views    WHERE is_ms_shipped = 0)      AS [views],
    (SELECT COUNT(*) FROM sys.check_constraints)                     AS [checks],
    (SELECT COUNT(*) FROM [__EFMigrationsHistory])                   AS [migrations];
GO
