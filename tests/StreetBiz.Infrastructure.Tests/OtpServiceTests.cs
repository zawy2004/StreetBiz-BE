using System.Text.RegularExpressions;
using FluentAssertions;
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

    private OtpService NewGuardedService(
        TestContext db, OtpIpGuard guard, string? ip, int limit, ISmsSender? sender = null) =>
        new(db, sender ?? sms, clock, Options.Create(new OtpSettings { HashKey = "test-key", MaxPerIpPerHour = limit }),
            new FixedClient(ip), guard);

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

    [Fact]
    public async Task The_text_is_plain_ascii_within_one_sms_and_the_code_comes_last()
    {
        await using var db = NewDb();
        await NewService(db).IssueAsync(Phone, Purpose, default);

        var text = sms.Messages[^1];
        var code = LastCode();
        text.Length.Should().BeLessThanOrEqualTo(OtpService.MaxMessageLength);
        text.Should().MatchRegex("^[\\x20-\\x7E]+$", "accents would turn it into a 70-character unicode SMS");
        text.Should().EndWith(code, "a lock-screen preview that cuts the text short must not show the code");
    }

    [Fact]
    public async Task One_address_may_not_ask_for_codes_to_many_numbers()
    {
        await using var db = NewDb();
        var guard = new OtpIpGuard();
        var otp = NewGuardedService(db, guard, "203.0.113.9", limit: 3);

        for (var i = 0; i < 3; i++)
        {
            await otp.IssueAsync($"090500000{i}", Purpose, default);
        }

        var act = () => otp.IssueAsync("0905000009", Purpose, default);
        await Assert.ThrowsAsync<TooManyRequestsException>(act);
        sms.Messages.Should().HaveCount(3, "the refused request must not cost an SMS");

        // Another address is not affected, and the window slides.
        await NewGuardedService(db, guard, "198.51.100.4", limit: 3).IssueAsync("0905000009", Purpose, default);
        clock.Advance(TimeSpan.FromHours(1) + TimeSpan.FromSeconds(1));
        await otp.IssueAsync("0905000008", Purpose, default);
    }

    [Fact]
    public async Task A_limit_of_zero_or_an_unknown_address_turns_the_address_check_off()
    {
        await using var db = NewDb();
        var guard = new OtpIpGuard();

        for (var i = 0; i < 12; i++)
        {
            await NewGuardedService(db, guard, "203.0.113.9", limit: 0).IssueAsync($"09050001{i:00}", Purpose, default);
            await NewGuardedService(db, guard, null, limit: 1).IssueAsync($"09050002{i:00}", Purpose, default);
        }

        sms.Messages.Should().HaveCount(24);
    }

    [Fact]
    public async Task A_text_that_could_not_be_sent_leaves_nothing_behind_so_the_person_can_ask_again()
    {
        await using var db = NewDb();
        var guard = new OtpIpGuard();
        var otp = NewGuardedService(db, guard, "203.0.113.9", limit: 1, sender: new FailingSms());

        var failure = await Assert.ThrowsAsync<SmsDeliveryException>(() => otp.IssueAsync(Phone, Purpose, default));
        failure.StatusCode.Should().Be(503);
        failure.Message.Should().NotContain("provider down", "the gateway's own wording must not reach the client");

        (await db.OtpChallenges.CountAsync()).Should().Be(0);
        // Neither the 60 s cooldown nor the address allowance (1 an hour) was used up by the failed send.
        await NewGuardedService(db, guard, "203.0.113.9", limit: 1).IssueAsync(Phone, Purpose, default);
        sms.Messages.Should().HaveCount(1);
    }

    private sealed class FixedClient(string? ip) : IClientContext
    {
        public string? IpAddress => ip;
    }

    private sealed class FailingSms : ISmsSender
    {
        public Task SendAsync(string phoneNumber, string message, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("provider down");
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
