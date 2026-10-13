using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using StreetBiz.Application.Common.Interfaces;
using StreetBiz.Infrastructure.Persistence;
using StreetBiz.Infrastructure.Persistence.ScaffoldedModels;
using StreetBiz.Infrastructure.Services;

namespace StreetBiz.Infrastructure.Tests;

public sealed class SecurityEventsTests : IAsyncLifetime
{
    private SqliteConnection connection = null!;

    public async Task InitializeAsync()
    {
        connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = NewDb();
        await db.Database.EnsureCreatedAsync();
        db.Roles.Add(new Role { role_code = "VENDOR", role_name = "Vendor" });
        db.UserAccounts.AddRange(
            new UserAccount { user_id = 1, phone_number = "0900000001", password_hash = "x", role_code = "VENDOR", account_status = "ACTIVE" },
            new UserAccount { user_id = 2, phone_number = "0900000002", password_hash = "x", role_code = "VENDOR", account_status = "ACTIVE" });
        await db.SaveChangesAsync();
    }

    public Task DisposeAsync() => connection.DisposeAsync().AsTask();

    private TestContext NewDb() =>
        new(new DbContextOptionsBuilder<StreetBizDbContext>().UseSqlite(connection).Options);

    private sealed class Clock : IDateTimeProvider
    {
        public DateTime UtcNow => new(2026, 10, 5, 8, 0, 0, DateTimeKind.Utc);
    }

    [Fact]
    public async Task History_lists_only_the_owners_security_events_newest_first_and_pages_backwards()
    {
        await using var db = NewDb();
        var events = new SecurityEvents(db, new Clock());
        await events.RecordAsync(1, SecurityActions.LoginSuccess, "a", null, default);
        await events.RecordAsync(2, SecurityActions.LoginSuccess, "someone else", null, default);
        await events.RecordAsync(1, SecurityActions.LoginFailed, "b", null, default);
        await events.RecordAsync(1, SecurityActions.PasswordChanged, "c", null, default);

        var page = await events.ListAsync(1, 2, null, default);

        Assert.Equal(["PASSWORD_CHANGED", "LOGIN_FAILED"], page.Select(e => e.Action));
        var older = await events.ListAsync(1, 2, page[^1].Id, default);
        Assert.Single(older);
        Assert.Equal("LOGIN_SUCCESS", older[0].Action);
        Assert.DoesNotContain(page.Concat(older), e => e.Details == "someone else");
    }

    [Fact]
    public async Task Registration_audit_rows_do_not_leak_into_the_login_history()
    {
        await using var db = NewDb();
        db.AuditLogs.Add(new AuditLog
        {
            actor_user_id = 1, action = "ENROLLMENT_APPROVED", entity_type = "BusinessRegistration",
            entity_id = 5, created_at = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();

        var page = await new SecurityEvents(db, new Clock()).ListAsync(1, 20, null, default);

        Assert.Empty(page);
    }

    [Fact]
    public async Task A_notification_is_created_only_when_asked_for()
    {
        await using var db = NewDb();
        var events = new SecurityEvents(db, new Clock());

        await events.RecordAsync(1, SecurityActions.LoginSuccess, null, null, default);
        Assert.Empty(await db.Notifications.ToListAsync());

        await events.RecordAsync(1, SecurityActions.PasswordChanged, null, ("Tiêu đề", "Nội dung"), default);
        var note = Assert.Single(await db.Notifications.ToListAsync());
        Assert.Equal(1, note.user_id);
        Assert.Equal("SECURITY_ALERT", note.notification_type);
    }

    private sealed class TestContext(DbContextOptions<StreetBizDbContext> options) : StreetBizDbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);
            foreach (var entity in modelBuilder.Model.GetEntityTypes())
            {
                foreach (var property in entity.GetProperties())
                {
                    if (property.GetComputedColumnSql() is not null)
                    {
                        property.SetComputedColumnSql(null);
                        property.ValueGenerated = ValueGenerated.Never;
                    }

                    property.SetDefaultValueSql(null);
                    if (property.ClrType == typeof(decimal) || property.ClrType == typeof(decimal?))
                    {
                        modelBuilder.Entity(entity.ClrType).Property(property.Name).HasConversion<double>();
                    }
                }
            }
        }
    }
}
