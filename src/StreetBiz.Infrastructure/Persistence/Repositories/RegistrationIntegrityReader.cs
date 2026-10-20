using Microsoft.EntityFrameworkCore;
using StreetBiz.Application.Common.Security;
using StreetBiz.Application.Features.RegistrationIntegrity;

namespace StreetBiz.Infrastructure.Persistence.Repositories;

/// <summary>
/// AIC-03 facts. A vendor with several storefronts legitimately files several registrations under
/// one CCCD (BR-06), so everything here compares against OTHER vendors' registrations only.
///
/// Addresses are compared in memory after normalising away case, accents and punctuation: SQL
/// Server cannot strip Vietnamese diacritics in a query, and one ward's live registrations are a
/// small set (SCA-05 does not ask for more).
/// </summary>
public sealed class RegistrationIntegrityReader(StreetBizDbContext db) : IRegistrationIntegrityReader
{
    private const int MaxRelated = 10;

    private static readonly string[] OpenApplication =
    [
        ApplicationStatuses.Pending,
        ApplicationStatuses.UnderReview,
        ApplicationStatuses.MoreInformationRequired,
    ];

    private static readonly string[] LiveRegistration =
    [
        RegistrationStatuses.Submitted,
        RegistrationStatuses.UnderReview,
        RegistrationStatuses.MoreInformationRequired,
        RegistrationStatuses.Approved,
    ];

    public async Task<RegistrationIntegrityFacts?> GetFactsAsync(
        int wardId, long registrationId, CancellationToken cancellationToken)
    {
        var registration = await db.BusinessRegistrations.AsNoTracking()
            .Where(r => r.registration_id == registrationId && r.ward_unit_id == wardId)
            .Select(r => new { r.registration_id, r.vendor_id, r.id_number, r.declared_address, r.created_at })
            .SingleOrDefaultAsync(cancellationToken);
        if (registration is null)
        {
            return null;
        }

        var vendorId = registration.vendor_id;
        var idNumber = registration.id_number?.Trim();
        var hasId = !string.IsNullOrEmpty(idNumber);

        var sameId = new List<Candidate>();
        if (hasId)
        {
            sameId = await db.BusinessRegistrations.AsNoTracking()
                .Where(r => r.id_number == idNumber && r.vendor_id != vendorId)
                .OrderByDescending(r => r.created_at)
                .Take(MaxRelated)
                .Select(r => new Candidate(r.registration_id, r.display_name, r.ward_unit_id, r.registration_status, null))
                .ToListAsync(cancellationToken);
        }

        var sameAddress = new List<Candidate>();
        var addressKey = AddressKey.Normalize(registration.declared_address);
        if (addressKey.Length > 0)
        {
            var neighbours = await db.BusinessRegistrations.AsNoTracking()
                .Where(r => r.ward_unit_id == wardId && r.vendor_id != vendorId && r.declared_address != null
                    && LiveRegistration.Contains(r.registration_status))
                .Select(r => new
                {
                    r.registration_id, r.display_name, r.ward_unit_id, r.registration_status, r.declared_address,
                })
                .ToListAsync(cancellationToken);
            sameAddress = neighbours
                .Where(r => AddressKey.Normalize(r.declared_address) == addressKey)
                .Take(MaxRelated)
                .Select(r => new Candidate(r.registration_id, r.display_name, r.ward_unit_id, r.registration_status, null))
                .ToList();
        }

        var windowStart = registration.created_at.AddDays(-RegistrationIntegrityRules.BurstWindowDays);
        var windowEnd = registration.created_at.AddDays(RegistrationIntegrityRules.BurstWindowDays);
        var aroundThisOne = await db.BusinessRegistrations.AsNoTracking()
            .CountAsync(r => r.vendor_id == vendorId && r.created_at >= windowStart && r.created_at <= windowEnd,
                cancellationToken);

        var openSlotIds = await db.RentalApplications.AsNoTracking()
            .Where(a => a.registration_id == registrationId && OpenApplication.Contains(a.application_status))
            .Select(a => a.slot_id)
            .ToListAsync(cancellationToken);

        var contested = new List<Candidate>();
        if (hasId && openSlotIds.Count > 0)
        {
            contested = await db.RentalApplications.AsNoTracking()
                .Where(a => openSlotIds.Contains(a.slot_id)
                    && a.registration.id_number == idNumber
                    && a.registration.vendor_id != vendorId
                    && (OpenApplication.Contains(a.application_status)
                        || a.application_status == ApplicationStatuses.Approved))
                .Take(MaxRelated)
                .Select(a => new Candidate(
                    a.registration_id,
                    a.registration.display_name,
                    a.registration.ward_unit_id,
                    a.registration.registration_status,
                    "Ô " + a.slot.slot_code))
                .ToListAsync(cancellationToken);
        }

        var wardIds = sameId.Concat(sameAddress).Concat(contested).Select(c => c.WardId).Distinct().ToList();
        var wardNames = wardIds.Count == 0
            ? new Dictionary<int, string>()
            : await db.AdministrativeUnits.AsNoTracking()
                .Where(u => wardIds.Contains(u.unit_id))
                .ToDictionaryAsync(u => u.unit_id, u => u.unit_name, cancellationToken);

        RelatedRegistrationFact ToFact(Candidate c) => new(
            c.RegistrationId, c.BusinessName, c.WardId,
            wardNames.TryGetValue(c.WardId, out var name) ? name : "Phường khác",
            c.Status, c.Note);

        return new RegistrationIntegrityFacts(
            registration.registration_id,
            registration.id_number,
            registration.declared_address,
            sameId.Select(ToFact).ToList(),
            sameAddress.Select(ToFact).ToList(),
            aroundThisOne,
            openSlotIds.Count,
            contested.Select(ToFact).ToList());
    }

    private sealed record Candidate(long RegistrationId, string BusinessName, int WardId, string Status, string? Note);
}
