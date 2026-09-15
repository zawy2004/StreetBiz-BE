using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using StreetBiz.Application.Common.Exceptions;
using StreetBiz.Application.Common.Interfaces;
using StreetBiz.Application.Common.Security;
using StreetBiz.Infrastructure.Notifications;
using StreetBiz.Infrastructure.Persistence;
using StreetBiz.Infrastructure.Persistence.ScaffoldedModels;

namespace StreetBiz.Infrastructure.Security;

/// <summary>Phone OTP: single-use, 6 digits, 5-minute validity, 60s resend cooldown, 5-attempt lock (BR-61).</summary>
public sealed class OtpService(
    StreetBizDbContext dbContext,
    ISmsSender smsSender,
    IDateTimeProvider clock) : IOtpService
{
    private static readonly TimeSpan Validity = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan ResendCooldown = TimeSpan.FromSeconds(60);
    private const byte MaxAttempts = 5;

    public async Task IssueAsync(string phoneNumber, string purpose, CancellationToken cancellationToken)
    {
        var now = clock.UtcNow;

        var latest = await dbContext.OtpChallenges
            .Where(c => c.phone_number == phoneNumber && c.purpose == purpose)
            .OrderByDescending(c => c.created_at)
            .FirstOrDefaultAsync(cancellationToken);

        if (latest is not null && latest.consumed_at is null && now - latest.created_at < ResendCooldown)
        {
            throw new DomainRuleException(AppMessages.OtpCooldown);
        }

        var code = RandomNumberGenerator.GetInt32(0, 1_000_000).ToString("D6");

        dbContext.OtpChallenges.Add(new OtpChallenge
        {
            phone_number = phoneNumber,
            purpose = purpose,
            code_hash = HashCode(phoneNumber, purpose, code),
            attempt_count = 0,
            max_attempts = MaxAttempts,
            expires_at = now.Add(Validity),
            created_at = now,
        });

        await dbContext.SaveChangesAsync(cancellationToken);
        await smsSender.SendAsync(phoneNumber, $"Your StreetBiz verification code is {code}.", cancellationToken);
    }

    public async Task ConsumeAsync(string phoneNumber, string purpose, string code, CancellationToken cancellationToken)
    {
        var now = clock.UtcNow;

        var challenge = await dbContext.OtpChallenges
            .Where(c => c.phone_number == phoneNumber && c.purpose == purpose && c.consumed_at == null)
            .OrderByDescending(c => c.created_at)
            .FirstOrDefaultAsync(cancellationToken);

        if (challenge is null || challenge.expires_at <= now)
        {
            throw new AuthenticationException(AppMessages.OtpExpired);
        }

        if (challenge.attempt_count >= challenge.max_attempts)
        {
            throw new AuthenticationException(AppMessages.OtpLocked);
        }

        var provided = HashCode(phoneNumber, purpose, code);
        if (!CryptographicOperations.FixedTimeEquals(provided, challenge.code_hash))
        {
            challenge.attempt_count = (byte)(challenge.attempt_count + 1);
            await dbContext.SaveChangesAsync(cancellationToken);
            throw new AuthenticationException(AppMessages.OtpIncorrect);
        }

        challenge.consumed_at = now;
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    private static byte[] HashCode(string phoneNumber, string purpose, string code) =>
        SHA256.HashData(Encoding.UTF8.GetBytes($"{phoneNumber}|{purpose}|{code}"));
}
