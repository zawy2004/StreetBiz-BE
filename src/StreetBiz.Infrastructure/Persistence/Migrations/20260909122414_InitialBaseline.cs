using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace StreetBiz.Infrastructure.Persistence.Migrations;

/// <summary>
/// Establishes the EF Core model baseline for the pre-existing StreetBiz database.
/// This migration intentionally performs no schema operations.
/// </summary>
public partial class InitialBaseline : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        // Intentionally empty. The database schema existed before EF migrations.
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        // Intentionally empty. Never drop the pre-existing database schema.
    }
}
