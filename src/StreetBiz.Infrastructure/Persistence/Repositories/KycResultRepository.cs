using Microsoft.EntityFrameworkCore;
using StreetBiz.Application.Common.Interfaces;
using StreetBiz.Application.Features.VendorKyc;
using StreetBiz.Infrastructure.Persistence.ScaffoldedModels;

namespace StreetBiz.Infrastructure.Persistence.Repositories;

public sealed class KycResultRepository(
    StreetBizDbContext dbContext,
    IDateTimeProvider clock) : IKycResultRepository
{
    private const string Provider = "FPT.AI";

    /// <summary>A failed/unconfigured provider call is not a check result -- recording it would
    /// show the officer a meaningless 0%.</summary>
    public async Task RecordIdCardCheckAsync(long userId, KycIdCardExtraction extraction, CancellationToken ct)
    {
        if (!extraction.IsAiGenerated)
        {
            return;
        }

        dbContext.KycVerificationResults.Add(new KycVerificationResult
        {
            user_id = userId,
            check_type = KycCheckTypes.IdCardOcr,
            provider = Provider,
            confidence_percent = extraction.ConfidencePercent,
            extracted_id_number = ValidIdNumber(extraction.IdNumber),
            warnings = Join(extraction.Warnings),
            created_at = clock.UtcNow,
        });

        await dbContext.SaveChangesAsync(ct);
    }

    public async Task RecordFaceMatchAsync(long userId, KycFaceMatchResult result, CancellationToken ct)
    {
        if (!result.IsAiGenerated)
        {
            return;
        }

        dbContext.KycVerificationResults.Add(new KycVerificationResult
        {
            user_id = userId,
            check_type = KycCheckTypes.FaceMatch,
            provider = Provider,
            is_match = result.IsMatch,
            similarity_percent = (decimal)result.SimilarityPercent,
            warnings = Join(result.Warnings),
            created_at = clock.UtcNow,
        });

        await dbContext.SaveChangesAsync(ct);
    }

    public async Task LinkPendingResultsAsync(long userId, long registrationId, CancellationToken ct)
    {
        var pending = await dbContext.KycVerificationResults
            .Where(r => r.user_id == userId && r.registration_id == null)
            .ToListAsync(ct);

        if (pending.Count == 0)
        {
            return;
        }

        foreach (var row in pending)
        {
            row.registration_id = registrationId;
        }

        // The registration has no CCCD number field of its own: the number read off the card is what
        // AIC-03 compares across accounts, so it must reach BusinessRegistrations.id_number here and not
        // only after an officer re-runs the document check. The newest scan wins (the applicant may have
        // swapped the card), and only the owner's own registration is touched. The officer still verifies
        // identity by hand (BR-41); a number only ever feeds an advisory duplicate warning.
        var scanned = pending
            .Where(r => r.check_type == KycCheckTypes.IdCardOcr && r.extracted_id_number != null)
            .OrderByDescending(r => r.created_at)
            .Select(r => r.extracted_id_number)
            .FirstOrDefault();
        if (scanned is not null)
        {
            var registration = await dbContext.BusinessRegistrations
                .FirstOrDefaultAsync(r => r.registration_id == registrationId && r.vendor.user_id == userId, ct);
            if (registration is not null)
            {
                registration.id_number = scanned;
            }
        }

        await dbContext.SaveChangesAsync(ct);
    }

    /// <summary>The most recent check of each type, since the applicant may retry a scan.</summary>
    public async Task<IReadOnlyList<KycCheckRecord>> ListForRegistrationAsync(long registrationId, CancellationToken ct)
    {
        var rows = await dbContext.KycVerificationResults.AsNoTracking()
            .Where(r => r.registration_id == registrationId)
            .ToListAsync(ct);

        return rows
            .GroupBy(r => r.check_type)
            .Select(g => g.OrderByDescending(r => r.created_at).First())
            .OrderBy(r => r.check_type)
            .Select(r => new KycCheckRecord(
                r.check_type, r.provider, r.is_match, r.similarity_percent,
                r.confidence_percent, r.warnings, r.created_at))
            .ToList();
    }

    /// <summary>A CCCD number is exactly 12 digits; anything else (a misread, a passport) is not stored,
    /// which also keeps it inside the NVARCHAR(12) column instead of failing the scan with a 500.</summary>
    internal static string? ValidIdNumber(string? idNumber)
    {
        var trimmed = idNumber?.Trim();
        return trimmed is { Length: 12 } && trimmed.All(char.IsDigit) ? trimmed : null;
    }

    private static string? Join(IReadOnlyList<string> warnings)
    {
        if (warnings.Count == 0)
        {
            return null;
        }

        var joined = string.Join(" | ", warnings);
        return joined.Length <= 1000 ? joined : joined[..1000];
    }
}
