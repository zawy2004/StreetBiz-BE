using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using StreetBiz.Application.Common.Interfaces;
using StreetBiz.Infrastructure.Persistence;
using StreetBiz.Infrastructure.Persistence.Repositories;
using StreetBiz.Infrastructure.Persistence.ScaffoldedModels;

namespace StreetBiz.Infrastructure.Tests;

/// <summary>
/// AUTH-04 / AUTH-08 / AUTH-09: the session table behind sign-out, "active sessions" and
/// "sign out everywhere". Revocation must be per user and must never touch another account.
/// </summary>
public sealed class SessionRepositoryTests : IAsyncLifetime
{
    private const long Alice = 1;
    private const long Bob = 2;

    private SqliteConnection connection = null!;
    private TestContext db = null!;
    private readonly MutableClock clock = new(new DateTime(2026, 10, 27, 8, 0, 0, DateTimeKind.Utc));

    public async Task InitializeAsync()
    {
        connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        db = new TestContext(new DbContextOptionsBuilder<StreetBizDbContext>().UseSqlite(connection).Options);
        await db.Database.EnsureCreatedAsync();

        db.Roles.Add(new Role { role_code = "CUSTOMER", role_name = "Customer" });
        db.UserAccounts.AddRange(User(Alice, "0900000011"), User(Bob, "0900000012"));
        await db.SaveChangesAsync();
    }

    public async Task DisposeAsync()
    {
        await db.DisposeAsync();
        await connection.DisposeAsync();
    }

    private SessionRepository Repository() => new(db, clock);

    private static byte[] Hash(byte seed) => Enumerable.Repeat(seed, 32).ToArray();

    private Task<long> Create(long userId, byte seed, TimeSpan? lifetime = null, string? device = "Chrome on Windows") =>
        Repository().CreateAsync(
            userId, Hash(seed), device, "203.0.113.7", clock.UtcNow.Add(lifetime ?? TimeSpan.FromDays(30)), default);

    [Fact]
    public async Task Create_stores_the_device_the_address_and_stamps_the_creation_time()
    {
        var id = await Create(Alice, 1);

        var session = await Repository().GetByIdAsync(id, default);

        Assert.NotNull(session);
        Assert.Equal(Alice, session.UserId);
        Assert.Equal("Chrome on Windows", session.DeviceInfo);
        Assert.Equal("203.0.113.7", session.IpAddress);
        Assert.Equal(clock.UtcNow, session.CreatedAt);
        Assert.Equal(clock.UtcNow, session.LastActiveAt);
        Assert.Equal(clock.UtcNow.AddDays(30), session.ExpiresAt);
        Assert.Null(session.RevokedAt);
    }

    [Fact]
    public async Task Looks_a_session_up_by_the_hash_of_its_refresh_token_and_by_nothing_else()
    {
        var id = await Create(Alice, 1);
        await Create(Bob, 2);

        var found = await Repository().GetActiveByRefreshHashAsync(Hash(1), default);
        var unknown = await Repository().GetActiveByRefreshHashAsync(Hash(9), default);

        Assert.Equal(id, found?.Id);
        Assert.Null(unknown);
    }

    [Fact]
    public async Task Unknown_session_id_is_null()
    {
        Assert.Null(await Repository().GetByIdAsync(404, default));
    }

    [Fact]
    public async Task Active_list_has_only_this_users_live_sessions_most_recently_used_first()
    {
        var older = await Create(Alice, 1);
        clock.Advance(TimeSpan.FromMinutes(5));
        var newer = await Create(Alice, 2);
        await Create(Bob, 3);
        var revoked = await Create(Alice, 4);
        await Repository().RevokeAsync(revoked, default);
        await Create(Alice, 5, lifetime: TimeSpan.FromSeconds(1));
        clock.Advance(TimeSpan.FromMinutes(1)); // the 1-second session has now expired

        var active = await Repository().ListActiveByUserAsync(Alice, default);

        Assert.Equal(new[] { newer, older }, active.Select(s => s.Id));
    }

    [Fact]
    public async Task Touch_moves_the_last_active_time_forward_and_reorders_the_list()
    {
        var first = await Create(Alice, 1);
        clock.Advance(TimeSpan.FromMinutes(1));
        var second = await Create(Alice, 2);
        clock.Advance(TimeSpan.FromMinutes(1));

        await Repository().TouchAsync(first, default);

        var active = await Repository().ListActiveByUserAsync(Alice, default);
        Assert.Equal(new[] { first, second }, active.Select(s => s.Id));
        Assert.Equal(clock.UtcNow, active[0].LastActiveAt);
    }

    [Fact]
    public async Task Touching_a_session_that_does_not_exist_does_nothing()
    {
        await Repository().TouchAsync(404, default);
    }

    [Fact]
    public async Task Revoke_sets_the_time_once_and_keeps_the_first_time_on_a_second_call()
    {
        var id = await Create(Alice, 1);
        await Repository().RevokeAsync(id, default);
        var firstRevokedAt = (await Repository().GetByIdAsync(id, default))!.RevokedAt;
        clock.Advance(TimeSpan.FromHours(1));

        await Repository().RevokeAsync(id, default);
        await Repository().RevokeAsync(404, default);

        Assert.Equal(clock.UtcNow.AddHours(-1), firstRevokedAt);
        Assert.Equal(firstRevokedAt, (await Repository().GetByIdAsync(id, default))!.RevokedAt);
    }

    [Fact]
    public async Task TryRevoke_wins_exactly_once_so_a_refresh_token_cannot_be_used_twice()
    {
        var id = await Create(Alice, 1);

        var first = await Repository().TryRevokeAsync(id, default);
        var second = await Repository().TryRevokeAsync(id, default);
        var missing = await Repository().TryRevokeAsync(404, default);

        Assert.True(first);
        Assert.False(second);
        Assert.False(missing);
    }

    [Fact]
    public async Task Sign_out_everywhere_revokes_every_session_of_that_user_and_only_that_user()
    {
        var a1 = await Create(Alice, 1);
        var a2 = await Create(Alice, 2);
        var b1 = await Create(Bob, 3);

        await Repository().RevokeAllForUserAsync(Alice, default);

        Assert.NotNull((await Repository().GetByIdAsync(a1, default))!.RevokedAt);
        Assert.NotNull((await Repository().GetByIdAsync(a2, default))!.RevokedAt);
        Assert.Null((await Repository().GetByIdAsync(b1, default))!.RevokedAt);
        Assert.Empty(await Repository().ListActiveByUserAsync(Alice, default));
        Assert.Single(await Repository().ListActiveByUserAsync(Bob, default));
    }

    [Fact]
    public async Task Sign_out_other_devices_keeps_the_current_session_alive()
    {
        var current = await Create(Alice, 1);
        var other = await Create(Alice, 2);
        var bobs = await Create(Bob, 3);

        await Repository().RevokeAllForUserExceptAsync(Alice, current, default);

        Assert.Null((await Repository().GetByIdAsync(current, default))!.RevokedAt);
        Assert.NotNull((await Repository().GetByIdAsync(other, default))!.RevokedAt);
        Assert.Null((await Repository().GetByIdAsync(bobs, default))!.RevokedAt);
        Assert.Equal(new[] { current }, (await Repository().ListActiveByUserAsync(Alice, default)).Select(s => s.Id));
    }

    [Fact]
    public async Task Sign_out_everywhere_does_not_change_the_time_of_a_session_that_was_already_revoked()
    {
        var id = await Create(Alice, 1);
        await Repository().RevokeAsync(id, default);
        var original = (await Repository().GetByIdAsync(id, default))!.RevokedAt;
        clock.Advance(TimeSpan.FromHours(2));

        await Repository().RevokeAllForUserAsync(Alice, default);

        Assert.Equal(original, (await Repository().GetByIdAsync(id, default))!.RevokedAt);
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

    private sealed class MutableClock(DateTime start) : IDateTimeProvider
    {
        public DateTime UtcNow { get; private set; } = start;

        public void Advance(TimeSpan by) => UtcNow = UtcNow.Add(by);
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
