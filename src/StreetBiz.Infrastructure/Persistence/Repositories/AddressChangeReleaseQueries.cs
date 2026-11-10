using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using StreetBiz.Application.Common.Security;

namespace StreetBiz.Infrastructure.Persistence.Repositories;

/// <summary>
/// SIDE-10 reads shared by the vendor's tracking list, the ward's case view and the release sweep,
/// so all three agree on what is owed and how long the grace period has been extended.
/// </summary>
internal static class AddressChangeReleaseQueries
{
    /// <summary>The kind the ward service files address-change cases under (its audit entity_type).</summary>
    public const string WardCaseKind = "conflicts";

    /// <summary>Days of extra grace the ward has granted, per address change (from the audit trail).</summary>
    public static async Task<Dictionary<long, int>> ExtensionDaysAsync(
        StreetBizDbContext db,
        IReadOnlyCollection<long> addressChangeIds,
        CancellationToken cancellationToken)
    {
        if (addressChangeIds.Count == 0)
        {
            return [];
        }

        var rows = await db.AuditLogs.AsNoTracking()
            .Where(log => log.action == AdjacentSlotRelease.ExtendAuditAction
                && log.entity_type == WardCaseKind
                && addressChangeIds.Contains(log.entity_id))
            .Select(log => new { log.entity_id, log.details })
            .ToListAsync(cancellationToken);
        return rows
            .GroupBy(row => row.entity_id)
            .ToDictionary(group => group.Key, group => group.Sum(row => ReadDays(row.details)));
    }

    /// <summary>"Owed" is what the database itself refuses to cancel a contract over: an overdue fee or an unpaid penalty.</summary>
    public static async Task<Dictionary<long, decimal>> DebtByContractAsync(
        StreetBizDbContext db,
        IReadOnlyCollection<long> contractIds,
        CancellationToken cancellationToken)
    {
        if (contractIds.Count == 0)
        {
            return [];
        }

        var fees = await db.FeeScheduleItems.AsNoTracking()
            .Where(item => contractIds.Contains(item.fee_schedule.contract_id) && item.item_status == DebtStatuses.FeeItemOverdue)
            .Select(item => new { ContractId = item.fee_schedule.contract_id, item.amount })
            .ToListAsync(cancellationToken);
        var penalties = await db.Penalties.AsNoTracking()
            .Where(penalty => penalty.violation.contract_id != null
                && contractIds.Contains(penalty.violation.contract_id.Value)
                && penalty.penalty_status == DebtStatuses.PenaltyUnpaid)
            .Select(penalty => new { ContractId = penalty.violation.contract_id!.Value, penalty.amount })
            .ToListAsync(cancellationToken);
        return fees.Concat(penalties)
            .GroupBy(row => row.ContractId)
            .ToDictionary(group => group.Key, group => group.Sum(row => row.amount));
    }

    private static int ReadDays(string? details)
    {
        try
        {
            using var document = JsonDocument.Parse(details ?? "{}");
            return document.RootElement.TryGetProperty("Days", out var days) && days.TryGetInt32(out var value)
                ? value
                : AdjacentSlotRelease.ExtensionDays;
        }
        catch (JsonException)
        {
            return AdjacentSlotRelease.ExtensionDays;
        }
    }
}
