using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using StreetBiz.Application.Common.Exceptions;
using StreetBiz.Application.Common.Interfaces;
using StreetBiz.Application.Common.Security;
using StreetBiz.Infrastructure.Notifications;
using StreetBiz.Infrastructure.Persistence;
using StreetBiz.Infrastructure.Persistence.ScaffoldedModels;

namespace StreetBiz.Infrastructure.Security;

/// <summary>
/// Phone OTP: single-use, 6 digits, 5-minute validity, 60s resend cooldown, 5-attempt lock (BR-61),
/// plus per-phone volume caps (5/hour, 10/day), a per-address cap against SMS pumping and an HMAC-hashed
/// code so a leaked table is useless. The code goes last in the text so a lock-screen preview that cuts
/// the message short does not show it, and the text stays plain ASCII within one 160-character SMS.
/// </summary>
public sealed class OtpService(
    StreetBizDbContext dbContext,
    ISmsSender smsSender,
    IDateTimeProvider clock,
    IOptions<OtpSettings> options,
    IClientContext? client = null,
    OtpIpGuard? ipGuard = null) : IOtpService
{
    private static readonly TimeSpan Validity = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan ResendCooldown = TimeSpan.FromSeconds(60);
    private const byte MaxAttempts = 5;
    private const int MaxPerHour = 5;
    private const int MaxPerDay = 10;
    private static readonly TimeSpan IpWindow = TimeSpan.FromHours(1);

    /// <summary>One SMS segment is 160 GSM characters; accents would switch it to the 70-character UCS-2 form.</summary>
    public const int MaxMessageLength = 160;

    public static string BuildMessage(string code) =>
        $"StreetBiz: Ma xac thuc OTP (hieu luc 5 phut, khong chia se cho ai): {code}";

    private readonly byte[] hashKey = Encoding.UTF8.GetBytes(options.Value.HashKey);

    public async Task IssueAsync(string rawPhoneNumber, string purpose, CancellationToken cancellationToken)
    {
        var now = clock.UtcNow;
        // The code is bound to the phone number, so issue and consume must agree on its
        // form; normalizing both ends lets a code requested as +84… be entered as 0… (CR-06).
        var phoneNumber = AuthValidationRules.NormalizePhone(rawPhoneNumber);

        var latest = await dbContext.OtpChallenges
            .Where(c => c.phone_number == phoneNumber && c.purpose == purpose)
            .OrderByDescending(c => c.created_at)
            .FirstOrDefaultAsync(cancellationToken);

        if (latest is not null && latest.consumed_at is null && now - latest.created_at < ResendCooldown)
        {
            var wait = ResendCooldown - (now - latest.created_at);
            throw new TooManyRequestsException(AppMessages.OtpCooldown, (int)Math.Ceiling(wait.TotalSeconds));
        }

        // The cooldown alone still allows a steady stream of fresh guesses; cap the volume per phone.
        var dayAgo = now.AddDays(-1);
        var hourAgo = now.AddHours(-1);
        var sent = await dbContext.OtpChallenges
            .Where(c => c.phone_number == phoneNumber && c.created_at > dayAgo)
            .Select(c => c.created_at)
            .ToListAsync(cancellationToken);
        if (sent.Count >= MaxPerDay || sent.Count(t => t > hourAgo) >= MaxPerHour)
        {
            throw new TooManyRequestsException(AppMessages.OtpCooldown, 3600);
        }

        // Counted last, so a request already refused above never uses up the address's allowance.
        var ip = client?.IpAddress;
        if (ipGuard is not null && !ipGuard.TryAcquire(ip, now, options.Value.MaxPerIpPerHour, IpWindow))
        {
            throw new TooManyRequestsException(AppMessages.OtpCooldown, 3600);
        }

        var code = RandomNumberGenerator.GetInt32(0, 1_000_000).ToString("D6");

        var challenge = new OtpChallenge
        {
            phone_number = phoneNumber,
            purpose = purpose,
            code_hash = HashCode(phoneNumber, purpose, code),
            attempt_count = 0,
            max_attempts = MaxAttempts,
            expires_at = now.Add(Validity),
            created_at = now,
        };
        dbContext.OtpChallenges.Add(challenge);

        await dbContext.SaveChangesAsync(cancellationToken);
        try
        {
            await smsSender.SendAsync(phoneNumber, BuildMessage(code), cancellationToken);
        }
        catch
        {
            // The text never left, so the code is worthless and must not hold the resend cooldown or
            // use up the phone's and the address's allowance: the person can simply ask again.
            ipGuard?.Release(ip);
            dbContext.OtpChallenges.Remove(challenge);
            await dbContext.SaveChangesAsync(CancellationToken.None);

            // The sender already logged why (provider code only, never the text or keys); the caller
            // gets a plain 503 instead of an unhandled 500 with a stack trace.
            throw new SmsDeliveryException(AppMessages.SmsUnavailable);
        }
    }

    public async Task ConsumeAsync(string rawPhoneNumber, string purpose, string code, CancellationToken cancellationToken)
    {
        var now = clock.UtcNow;
        var phoneNumber = AuthValidationRules.NormalizePhone(rawPhoneNumber);

        var challenge = await dbContext.OtpChallenges
            .AsNoTracking()
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

        var challengeId = challenge.challenge_id;
        var provided = HashCode(phoneNumber, purpose, code);
        if (!CryptographicOperations.FixedTimeEquals(provided, challenge.code_hash))
        {
            // Atomic, conditional increment: parallel guesses cannot slip past the attempt cap.
            await dbContext.OtpChallenges
                .Where(c => c.challenge_id == challengeId && c.attempt_count < c.max_attempts)
                .ExecuteUpdateAsync(
                    set => set.SetProperty(c => c.attempt_count, c => (byte)(c.attempt_count + 1)),
                    cancellationToken);
            throw new AuthenticationException(AppMessages.OtpIncorrect);
        }

        // Single-use even under races: only the request that flips consumed_at wins.
        var consumed = await dbContext.OtpChallenges
            .Where(c => c.challenge_id == challengeId && c.consumed_at == null)
            .ExecuteUpdateAsync(set => set.SetProperty(c => c.consumed_at, now), cancellationToken);
        if (consumed == 0)
        {
            throw new AuthenticationException(AppMessages.OtpExpired);
        }
    }

    private byte[] HashCode(string phoneNumber, string purpose, string code) =>
        HMACSHA256.HashData(hashKey, Encoding.UTF8.GetBytes($"{phoneNumber}|{purpose}|{code}"));
}
