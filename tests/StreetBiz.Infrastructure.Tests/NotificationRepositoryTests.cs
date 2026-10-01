using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using StreetBiz.Infrastructure.Persistence;
using StreetBiz.Infrastructure.Persistence.Repositories;
using StreetBiz.Infrastructure.Persistence.ScaffoldedModels;

namespace StreetBiz.Infrastructure.Tests;

public sealed class NotificationRepositoryTests : IAsyncLifetime
{
    private const long Alice = 1;
    private const long Bob = 2;

    private SqliteConnection connection = null!;
    private TestContext db = null!;

    public async Task InitializeAsync()
    {
        connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        db = new TestContext(new DbContextOptionsBuilder<StreetBizDbContext>()
            .UseSqlite(connection)
            .Options);
        await db.Database.EnsureCreatedAsync();

        db.Roles.Add(new Role { role_code = "CUSTOMER", role_name = "Customer" });
        db.UserAccounts.AddRange(User(Alice, "0900000011"), User(Bob, "0900000012"));

        // Alice: ids 1..5, the first two already read. Bob: id 6, unread.
        for (var id = 1; id <= 5; id++)
        {
            db.Notifications.Add(Notification(id, Alice, isRead: id <= 2));
        }

        db.Notifications.Add(Notification(6, Bob, isRead: false));
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
    }

    public async Task DisposeAsync()
    {
        await db.DisposeAsync();
        await connection.DisposeAsync();
    }

    [Fact]
    public async Task Pages_newest_first_and_only_returns_the_owners_rows()
    {
        var repository = new NotificationRepository(db);

        var first = await repository.ListAsync(Alice, null, 2, CancellationToken.None);
        var second = await repository.ListAsync(
            Alice, first.Items[^1].NotificationId, 10, CancellationToken.None);

        Assert.Equal(new long[] { 5, 4 }, first.Items.Select(x => x.NotificationId));
        Assert.True(first.HasMore);
        Assert.Equal(new long[] { 3, 2, 1 }, second.Items.Select(x => x.NotificationId));
        Assert.False(second.HasMore);
        Assert.All(first.Items, x => Assert.Equal(DateTimeKind.Utc, x.SentAt.Kind));
    }

    [Fact]
    public async Task Orders_by_sent_time_even_when_ids_are_not_chronological()
    {
        // A back-dated row inserted last (id 7, the oldest), and one sharing id 4's time.
        db.Notifications.Add(Notification(7, Alice, isRead: false, sentAtMinute: 0));
        db.Notifications.Add(Notification(8, Alice, isRead: false, sentAtMinute: 4));
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        var repository = new NotificationRepository(db);

        var first = await repository.ListAsync(Alice, null, 3, CancellationToken.None);
        var rest = await repository.ListAsync(
            Alice, first.Items[^1].NotificationId, 10, CancellationToken.None);

        Assert.Equal(new long[] { 5, 8, 4 }, first.Items.Select(x => x.NotificationId));
        Assert.Equal(new long[] { 3, 2, 1, 7 }, rest.Items.Select(x => x.NotificationId));
        Assert.False(rest.HasMore);
    }

    [Fact]
    public async Task A_cursor_from_another_user_ends_the_listing()
    {
        var page = await new NotificationRepository(db).ListAsync(Alice, 6, 10, CancellationToken.None);

        Assert.Empty(page.Items);
        Assert.False(page.HasMore);
    }

    [Fact]
    public async Task Nobody_can_read_or_mark_another_users_notification()
    {
        var repository = new NotificationRepository(db);

        Assert.False(await repository.MarkReadAsync(Alice, 6, CancellationToken.None));
        Assert.Equal(1, await repository.CountUnreadAsync(Bob, CancellationToken.None));

        Assert.True(await repository.MarkReadAsync(Alice, 5, CancellationToken.None));
        // Marking an already-read notification again is not an error.
        Assert.True(await repository.MarkReadAsync(Alice, 5, CancellationToken.None));
        Assert.Equal(2, await repository.CountUnreadAsync(Alice, CancellationToken.None));
    }

    [Fact]
    public async Task Mark_all_read_only_touches_the_callers_rows()
    {
        var repository = new NotificationRepository(db);

        var changed = await repository.MarkAllReadAsync(Alice, CancellationToken.None);

        Assert.Equal(3, changed);
        Assert.Equal(0, await repository.CountUnreadAsync(Alice, CancellationToken.None));
        Assert.Equal(1, await repository.CountUnreadAsync(Bob, CancellationToken.None));
    }

    private static UserAccount User(long id, string phone) => new()
    {
        user_id = id,
        phone_number = phone,
        password_hash = "test-only",
        full_name = $"User {id}",
        role_code = "CUSTOMER",
        account_status = "ACTIVE",
    };

    private static Notification Notification(long id, long userId, bool isRead, int? sentAtMinute = null) => new()
    {
        notification_id = id,
        user_id = userId,
        notification_type = "ORDER_STATUS",
        title = $"Thông báo {id}",
        body = "Nội dung",
        related_entity_type = "ORDER",
        related_entity_id = id,
        is_read = isRead,
        sent_at = new DateTime(2026, 9, 30, 10, 0, 0).AddMinutes(sentAtMinute ?? id),
    };

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
