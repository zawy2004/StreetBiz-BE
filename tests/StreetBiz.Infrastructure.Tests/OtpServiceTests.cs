using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.Extensions.Options;
using StreetBiz.Application.Common.Exceptions;
using StreetBiz.Application.Common.Interfaces;
using StreetBiz.Infrastructure.Notifications;
using StreetBiz.Infrastructure.Persistence;
using StreetBiz.Infrastructure.Security;

namespace StreetBiz.Infrastructure.Tests;

public sealed class OtpServiceTests : IAsyncLifetime
{
    private const string Phone = "0905000001";
    private const string Purpose = "REGISTRATION";

    private SqliteConnection connection = null!;
    private readonly FakeClock clock = new();
    private readonly CapturingSms sms = new();

    public async Task InitializeAsync()
    {
        connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = NewDb();
        await db.Database.EnsureCreatedAsync();
    }

    public Task DisposeAsync() => connection.DisposeAsync().AsTask();

    private TestContext NewDb() =>
        new(new DbContextOptionsBuilder<StreetBizDbContext>().UseSqlite(connection).Options);

    private OtpService NewService(TestContext db, string key = "test-key") =>
        new(db, sms, clock, Options.Create(new OtpSettings { HashKey = key }));

    private string LastCode() => Regex.Match(sms.Messages[^1], @"\d{6}").Value;

    [Fact]
    public async Task A_correct_code_is_accepted_once_and_then_rejected()
    {
        await using var db = NewDb();
        var otp = NewService(db);
        await otp.IssueAsync(Phone, Purpose, default);

        await otp.ConsumeAsync(Phone, Purpose, LastCode(), default);

        await Assert.ThrowsAsync<AuthenticationException>(
            () => otp.ConsumeAsync(Phone, Purpose, LastCode(), default));
    }

    [Fact]
    public async Task The_stored_hash_depends_on_the_server_key()
    {
        await using var db = NewDb();
        await NewService(db, "key-one").IssueAsync(Phone, Purpose, default);
        var code = LastCode();

        // A service holding a different key (or an attacker hashing with plain SHA-256) cannot verify the code.
        await Assert.ThrowsAsync<AuthenticationException>(
            () => NewService(db, "key-two").ConsumeAsync(Phone, Purpose, code, default));
    }

    [Fact]
    public async Task The_attempt_cap_locks_the_challenge_even_for_the_right_code()
    {
        await using var db = NewDb();
        var otp = NewService(db);
        await otp.IssueAsync(Phone, Purpose, default);
        var code = LastCode();
        var wrong = code == "000000" ? "111111" : "000000";

        for (var i = 0; i < 5; i++)
        {
            await Assert.ThrowsAsync<AuthenticationException>(() => otp.ConsumeAsync(Phone, Purpose, wrong, default));
        }

        await Assert.ThrowsAsync<AuthenticationException>(() => otp.ConsumeAsync(Phone, Purpose, code, default));
    }

    [Fact]
    public async Task A_new_code_cannot_be_requested_inside_the_cooldown()
    {
        await using var db = NewDb();
        var otp = NewService(db);
        await otp.IssueAsync(Phone, Purpose, default);

        await Assert.ThrowsAsync<TooManyRequestsException>(() => otp.IssueAsync(Phone, Purpose, default));
    }

    [Fact]
    public async Task A_phone_may_request_at_most_five_codes_an_hour()
    {
        await using var db = NewDb();
        var otp = NewService(db);
        for (var i = 0; i < 5; i++)
        {
            await otp.IssueAsync(Phone, Purpose, default);
            clock.Advance(TimeSpan.FromSeconds(61));
        }

        await Assert.ThrowsAsync<TooManyRequestsException>(() => otp.IssueAsync(Phone, Purpose, default));

        clock.Advance(TimeSpan.FromHours(1));
        await otp.IssueAsync(Phone, Purpose, default);
    }

    [Fact]
    public async Task An_expired_code_is_rejected()
    {
        await using var db = NewDb();
        var otp = NewService(db);
        await otp.IssueAsync(Phone, Purpose, default);
        clock.Advance(TimeSpan.FromMinutes(6));

        await Assert.ThrowsAsync<AuthenticationException>(() => otp.ConsumeAsync(Phone, Purpose, LastCode(), default));
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

    private sealed class FakeClock : IDateTimeProvider
    {
        public DateTime UtcNow { get; private set; } = new(2026, 10, 5, 8, 0, 0, DateTimeKind.Utc);
        public void Advance(TimeSpan by) => UtcNow += by;
    }

    private sealed class CapturingSms : ISmsSender
    {
        public List<string> Messages { get; } = [];

        public Task SendAsync(string phoneNumber, string message, CancellationToken cancellationToken)
        {
            Messages.Add(message);
            return Task.CompletedTask;
        }
    }
}
