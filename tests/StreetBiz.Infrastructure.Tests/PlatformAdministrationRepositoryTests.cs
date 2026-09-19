using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using StreetBiz.Infrastructure.Persistence;
using StreetBiz.Infrastructure.Persistence.Repositories;
using StreetBiz.Infrastructure.Persistence.ScaffoldedModels;

namespace StreetBiz.Infrastructure.Tests;

public sealed class PlatformAdministrationRepositoryTests
{
    [Fact]
    public async Task Food_categories_are_translated_and_sorted_by_name()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<StreetBizDbContext>()
            .UseSqlite(connection)
            .Options;
        await using var db = new TestContext(options);
        await db.Database.EnsureCreatedAsync();

        db.Roles.Add(new Role { role_code = "PLATFORM_ADMIN", role_name = "Admin" });
        db.UserAccounts.Add(new UserAccount
        {
            user_id = 1,
            phone_number = "0900000001",
            password_hash = "test-only",
            full_name = "Test Admin",
            role_code = "PLATFORM_ADMIN",
            account_status = "ACTIVE"
        });
        db.FoodCategories.AddRange(
            new FoodCategory
            {
                category_id = 1,
                category_name = "Z category",
                created_by = 1,
                creator_role = "PLATFORM_ADMIN"
            },
            new FoodCategory
            {
                category_id = 2,
                category_name = "A category",
                created_by = 1,
                creator_role = "PLATFORM_ADMIN"
            });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var repository = new PlatformAdministrationRepository(db, TimeProvider.System);
        var rows = await repository.ListFoodCategoriesAsync(CancellationToken.None);

        Assert.Equal(new[] { "A category", "Z category" }, rows.Select(row => row.CategoryName));
        Assert.All(rows, row => Assert.Equal(0, row.ItemCount));
        Assert.All(rows, row => Assert.Equal("Test Admin", row.CreatedByName));

        var created = await repository.CreateFoodCategoryAsync(
            "Created category", 1, CancellationToken.None);
        Assert.NotNull(created);
        Assert.Equal("Created category", created.CategoryName);

        var renamed = await repository.RenameFoodCategoryAsync(
            created.CategoryId, "Renamed category", 1, CancellationToken.None);
        Assert.NotNull(renamed);
        Assert.Equal("Renamed category", renamed.CategoryName);
    }

    private sealed class TestContext(DbContextOptions<StreetBizDbContext> options)
        : StreetBizDbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);
            foreach (var entity in modelBuilder.Model.GetEntityTypes())
                foreach (var property in entity.GetProperties())
                {
                    if (property.GetComputedColumnSql() is not null)
                    {
                        property.SetComputedColumnSql(null);
                        property.ValueGenerated = ValueGenerated.Never;
                    }

                    property.SetDefaultValueSql(null);
                }
        }
    }
}
