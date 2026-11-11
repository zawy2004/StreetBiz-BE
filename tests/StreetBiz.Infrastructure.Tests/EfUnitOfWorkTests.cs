using System.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using StreetBiz.Infrastructure.Persistence;
using StreetBiz.Infrastructure.Persistence.ScaffoldedModels;

namespace StreetBiz.Infrastructure.Tests;

public sealed class EfUnitOfWorkTests : IAsyncLifetime
{
    private SqliteConnection connection = null!;
    private TestContext db = null!;
    private EfUnitOfWork uow = null!;

    public async Task InitializeAsync()
    {
        connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        db = new TestContext(new DbContextOptionsBuilder<StreetBizDbContext>().UseSqlite(connection).Options);
        await db.Database.EnsureCreatedAsync();
        db.Roles.Add(new Role { role_code = "CUSTOMER", role_name = "Customer" });
        db.UserAccounts.Add(new UserAccount
        {
            user_id = 1,
            phone_number = "0900000001",
            password_hash = "test-only",
            full_name = "User 1",
            role_code = "CUSTOMER",
            account_status = "ACTIVE",
        });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        uow = new EfUnitOfWork(db);
    }

    public async Task DisposeAsync()
    {
        await db.DisposeAsync();
        await connection.DisposeAsync();
    }

    private static Notification Row(long id) => new()
    {
        notification_id = id,
        user_id = 1,
        notification_type = "ORDER_STATUS",
        title = $"Thông báo {id}",
        body = "Nội dung",
        related_entity_type = "ORDER",
        related_entity_id = id,
        sent_at = new DateTime(2026, 9, 30, 10, 0, 0),
    };

    private Task<int> Stored() => db.Notifications.AsNoTracking().CountAsync();

    [Fact]
    public async Task Commit_keeps_everything_the_work_saved()
    {
        await uow.ExecuteInTransactionAsync(async transaction =>
        {
            db.Notifications.Add(Row(1));
            await db.SaveChangesAsync();
            db.Notifications.Add(Row(2));
            await db.SaveChangesAsync();
            await transaction.CommitAsync(default);
        }, IsolationLevel.Serializable, default);

        Assert.Equal(2, await Stored());
    }

    [Fact]
    public async Task Returning_without_committing_rolls_back()
    {
        await uow.ExecuteInTransactionAsync(async _ =>
        {
            db.Notifications.Add(Row(1));
            await db.SaveChangesAsync();
        }, IsolationLevel.Serializable, default);

        Assert.Equal(0, await Stored());
    }

    [Fact]
    public async Task A_failure_rolls_back_and_reaches_the_caller()
    {
        var act = () => uow.ExecuteInTransactionAsync(async _ =>
        {
            db.Notifications.Add(Row(1));
            await db.SaveChangesAsync();
            throw new InvalidOperationException("boom");
        }, IsolationLevel.Serializable, default);

        await Assert.ThrowsAsync<InvalidOperationException>(act);
        Assert.Equal(0, await Stored());
    }

    [Fact]
    public async Task Work_returns_its_value()
    {
        var value = await uow.ExecuteInTransactionAsync(async transaction =>
        {
            await transaction.CommitAsync(default);
            return 42;
        }, IsolationLevel.Serializable, default);

        Assert.Equal(42, value);
    }

    [Fact]
    public async Task Nested_work_joins_the_outer_transaction_and_only_the_outer_commit_counts()
    {
        await uow.ExecuteInTransactionAsync(async _ =>
        {
            await uow.ExecuteInTransactionAsync(async inner =>
            {
                db.Notifications.Add(Row(1));
                await db.SaveChangesAsync();
                await inner.CommitAsync(default);
            }, IsolationLevel.Serializable, default);

            Assert.NotNull(db.Database.CurrentTransaction);
        }, IsolationLevel.Serializable, default);

        Assert.Equal(0, await Stored());
    }

    [Fact]
    public async Task Nested_work_keeps_what_the_outer_work_has_staged()
    {
        await uow.ExecuteInTransactionAsync(async outer =>
        {
            db.Notifications.Add(Row(1));
            await uow.ExecuteInTransactionAsync(_ => Task.CompletedTask, IsolationLevel.Serializable, default);
            await db.SaveChangesAsync();
            await outer.CommitAsync(default);
        }, IsolationLevel.Serializable, default);

        Assert.Equal(1, await Stored());
    }

    [Fact]
    public async Task Each_top_level_call_starts_from_an_empty_tracker()
    {
        db.Notifications.Add(Row(1));

        await uow.ExecuteInTransactionAsync(async transaction =>
        {
            Assert.Empty(db.ChangeTracker.Entries());
            await transaction.CommitAsync(default);
        }, IsolationLevel.Serializable, default);

        db.Notifications.Add(Row(2));
        await uow.ExecuteInTransactionAsync(async transaction =>
        {
            Assert.Single(db.ChangeTracker.Entries());
            await transaction.CommitAsync(default);
        }, IsolationLevel.Serializable, default, clearTracker: false);
    }

    [Fact]
    public async Task Save_changes_commits_what_the_repositories_staged()
    {
        db.Notifications.Add(Row(1));
        db.Notifications.Add(Row(2));

        await uow.SaveChangesAsync(default);

        Assert.Equal(2, await Stored());
    }

    private sealed class TestContext(DbContextOptions<StreetBizDbContext> options) : StreetBizDbContext(options)
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
