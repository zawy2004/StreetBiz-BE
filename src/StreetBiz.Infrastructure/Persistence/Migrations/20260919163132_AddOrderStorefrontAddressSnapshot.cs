using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace StreetBiz.Infrastructure.Persistence.Migrations;

/// <inheritdoc />
public partial class AddOrderStorefrontAddressSnapshot : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            IF COL_LENGTH(N'dbo.Orders', N'storefront_address_snapshot') IS NULL
            BEGIN
                EXEC(N'ALTER TABLE dbo.Orders
                    ADD storefront_address_snapshot nvarchar(500) NULL;');
            END;

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
            """);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            IF COL_LENGTH(N'dbo.Orders', N'storefront_address_snapshot') IS NOT NULL
            BEGIN
                ALTER TABLE dbo.Orders DROP COLUMN storefront_address_snapshot;
            END;
            """);
    }
}
