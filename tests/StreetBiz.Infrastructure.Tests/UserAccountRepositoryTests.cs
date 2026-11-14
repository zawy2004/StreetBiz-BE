using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using StreetBiz.Application.Common.Interfaces;
using StreetBiz.Application.Common.Models;
using StreetBiz.Infrastructure.Persistence;
using StreetBiz.Infrastructure.Persistence.Repositories;
using StreetBiz.Infrastructure.Persistence.ScaffoldedModels;

namespace StreetBiz.Infrastructure.Tests;

/// <summary>
/// AUTH-01 / AUTH-03 / AUTH-06 / AUTH-07: the account table. The phone number is the login, so
/// every lookup must treat the local form (09…) and the international form (+849…) as one number.
/// </summary>
public sealed class UserAccountRepositoryTests : IAsyncLifetime
{
    private static readonly DateTime Now = new(2026, 10, 27, 8, 0, 0, DateTimeKind.Utc);

    private SqliteConnection connection = null!;
    private TestContext db = null!;

    public async Task InitializeAsync()
    {
        connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        db = new TestContext(new DbContextOptionsBuilder<StreetBizDbContext>().UseSqlite(connection).Options);
        await db.Database.EnsureCreatedAsync();
        db.Roles.AddRange(
            new Role { role_code = "CUSTOMER", role_name = "Customer" },
            new Role { role_code = "VENDOR", role_name = "Vendor" });
        await db.SaveChangesAsync();
    }

    public async Task DisposeAsync()
    {
        await db.DisposeAsync();
        await connection.DisposeAsync();
    }

    private UserAccountRepository Repository() => new(db, new FixedClock());

    private static NewUser NewCustomer(string phone = "0912345678", string role = "CUSTOMER") =>
        new(phone, "hash-1", "Nguyễn Văn A", role, null, "ACTIVE", Now);

    [Fact]
    public async Task A_new_account_is_found_by_phone_and_by_id_with_the_fields_it_was_created_with()
    {
        var id = await Repository().CreateAsync(NewCustomer(), default);

        var byPhone = await Repository().GetByPhoneAsync("0912345678", default);
        var byId = await Repository().GetByIdAsync(id, default);

        Assert.NotNull(byPhone);
        Assert.Equal(id, byPhone.Id);
        Assert.Equal("hash-1", byPhone.PasswordHash);
        Assert.Equal("Nguyễn Văn A", byPhone.FullName);
        Assert.Equal("CUSTOMER", byPhone.RoleCode);
        Assert.Equal("ACTIVE", byPhone.AccountStatus);
        Assert.Equal(Now, byPhone.PhoneVerifiedAt);
        Assert.Null(byPhone.LockoutUntil);
        Assert.Equal(byPhone, byId);
    }

    [Fact]
    public async Task The_international_form_of_a_number_finds_the_same_account()
    {
        var id = await Repository().CreateAsync(NewCustomer("+84912345678"), default);

        Assert.Equal("0912345678", (await Repository().GetByIdAsync(id, default))!.PhoneNumber);
        Assert.Equal(id, (await Repository().GetByPhoneAsync("0912345678", default))!.Id);
        Assert.Equal(id, (await Repository().GetByPhoneAsync("+84912345678", default))!.Id);
        Assert.True(await Repository().PhoneExistsAsync("+84912345678", default));
        Assert.True(await Repository().PhoneExistsAsync("0912345678", default));
        Assert.True(await Repository().PhoneExistsAsync("  0912345678 ", default));
    }

    [Fact]
    public async Task An_unknown_phone_or_id_is_not_found()
    {
        Assert.False(await Repository().PhoneExistsAsync("0999999999", default));
        Assert.Null(await Repository().GetByPhoneAsync("0999999999", default));
        Assert.Null(await Repository().GetByIdAsync(404, default));
    }

    [Fact]
    public async Task A_vendor_account_gets_its_vendor_row_in_the_same_unit_and_a_customer_does_not()
    {
        var vendorId = await Repository().CreateAsync(NewCustomer("0911111111", "VENDOR"), default);
        var customerId = await Repository().CreateAsync(NewCustomer("0922222222"), default);

        Assert.Equal(1, await db.Vendors.CountAsync(v => v.user_id == vendorId));
        Assert.Equal(0, await db.Vendors.CountAsync(v => v.user_id == customerId));
        Assert.Equal(Now, (await db.Vendors.SingleAsync(v => v.user_id == vendorId)).created_at);
    }

    [Fact]
    public async Task Changing_the_password_replaces_the_hash_and_stamps_the_update_time()
    {
        var id = await Repository().CreateAsync(NewCustomer(), default);

        await Repository().UpdatePasswordHashAsync(id, "hash-2", default);
        db.ChangeTracker.Clear();

        var user = await Repository().GetByIdAsync(id, default);
        Assert.Equal("hash-2", user!.PasswordHash);
        Assert.Equal(Now, (await db.UserAccounts.AsNoTracking().SingleAsync(u => u.user_id == id)).updated_at);
    }

    [Fact]
    public async Task Changing_the_password_of_an_account_that_does_not_exist_does_nothing()
    {
        await Repository().UpdatePasswordHashAsync(404, "hash-2", default);
        Assert.Empty(db.UserAccounts);
    }

    [Fact]
    public async Task Wrong_passwords_below_the_limit_only_count_and_do_not_lock()
    {
        var id = await Repository().CreateAsync(NewCustomer(), default);

        var first = await Repository().RecordFailedLoginAsync(id, 5, TimeSpan.FromMinutes(15), default);
        var second = await Repository().RecordFailedLoginAsync(id, 5, TimeSpan.FromMinutes(15), default);

        Assert.False(first);
        Assert.False(second);
        var row = await db.UserAccounts.AsNoTracking().SingleAsync(u => u.user_id == id);
        Assert.Equal(2, row.failed_login_count);
        Assert.Null(row.lockout_until);
    }

    [Fact]
    public async Task The_failure_that_reaches_the_limit_locks_the_account_and_resets_the_counter()
    {
        var id = await Repository().CreateAsync(NewCustomer(), default);

        var results = new List<bool>();
        for (var i = 0; i < 3; i++)
        {
            results.Add(await Repository().RecordFailedLoginAsync(id, 3, TimeSpan.FromMinutes(15), default));
        }

        Assert.Equal(new[] { false, false, true }, results);
        var user = await Repository().GetByIdAsync(id, default);
        Assert.Equal(Now.AddMinutes(15), user!.LockoutUntil);
        Assert.Equal(0, (await db.UserAccounts.AsNoTracking().SingleAsync(u => u.user_id == id)).failed_login_count);
    }

    [Fact]
    public async Task A_failure_on_one_account_never_counts_against_another()
    {
        var alice = await Repository().CreateAsync(NewCustomer("0911111111"), default);
        var bob = await Repository().CreateAsync(NewCustomer("0922222222"), default);

        await Repository().RecordFailedLoginAsync(alice, 1, TimeSpan.FromMinutes(15), default);

        Assert.NotNull((await Repository().GetByIdAsync(alice, default))!.LockoutUntil);
        Assert.Null((await Repository().GetByIdAsync(bob, default))!.LockoutUntil);
    }

    [Fact]
    public async Task A_successful_sign_in_clears_the_counter_and_the_lockout()
    {
        var id = await Repository().CreateAsync(NewCustomer(), default);
        await Repository().RecordFailedLoginAsync(id, 5, TimeSpan.FromMinutes(15), default);
        await Repository().RecordFailedLoginAsync(id, 1, TimeSpan.FromMinutes(15), default);
        Assert.NotNull((await Repository().GetByIdAsync(id, default))!.LockoutUntil);

        await Repository().ClearFailedLoginsAsync(id, default);

        var row = await db.UserAccounts.AsNoTracking().SingleAsync(u => u.user_id == id);
        Assert.Equal(0, row.failed_login_count);
        Assert.Null(row.lockout_until);
    }

    [Fact]
    public async Task Clearing_an_account_that_has_nothing_to_clear_is_harmless()
    {
        var id = await Repository().CreateAsync(NewCustomer(), default);

        await Repository().ClearFailedLoginsAsync(id, default);
        await Repository().ClearFailedLoginsAsync(404, default);

        Assert.Equal(0, (await db.UserAccounts.AsNoTracking().SingleAsync(u => u.user_id == id)).failed_login_count);
    }

    private sealed class FixedClock : IDateTimeProvider
    {
        public DateTime UtcNow => Now;
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
