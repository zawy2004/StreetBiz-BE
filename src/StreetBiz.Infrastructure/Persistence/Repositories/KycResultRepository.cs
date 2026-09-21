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
            extracted_id_number = extraction.IdNumber,
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
