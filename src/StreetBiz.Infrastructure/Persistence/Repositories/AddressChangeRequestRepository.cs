using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using StreetBiz.Application.Common.Interfaces;
using StreetBiz.Application.Common.Models;
using StreetBiz.Application.Common.Security;
using StreetBiz.Infrastructure.Persistence.ScaffoldedModels;

namespace StreetBiz.Infrastructure.Persistence.Repositories;

public sealed class AddressChangeRequestRepository(StreetBizDbContext dbContext, TimeProvider clock)
    : IAddressChangeRequestRepository
{
    private DateTime Now => clock.GetUtcNow().UtcDateTime;

    public Task<bool> HasOpenAsync(long registrationId, CancellationToken cancellationToken) =>
        dbContext.AddressChangeRequests.AsNoTracking()
            .AnyAsync(r => r.registration_id == registrationId && AddressChangeStatuses.Open.Contains(r.change_status),
                cancellationToken);

    public async Task<long> CreateAsync(long registrationId, NewAddressChangeRequest data, CancellationToken cancellationToken)
    {
        var entity = new AddressChangeRequest
        {
            registration_id = registrationId,
            new_address = data.NewAddress,
            new_latitude = data.NewLatitude,
            new_longitude = data.NewLongitude,
            released_contract_id = data.ReleasedContractId,
            requested_new_slot_id = data.RequestedNewSlotId,
            // change_status and created_at are database defaults — never set here.
        };

        dbContext.AddressChangeRequests.Add(entity);
        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex)
        {
            var translated = SqlErrorTranslator.TryTranslate(ex);
            if (translated is not null)
            {
                throw translated;
            }

            throw;
        }

        return entity.address_change_id;
    }

    public async Task<AddressChangeRequestRow?> GetByIdAsync(long addressChangeId, CancellationToken cancellationToken)
    {
        var row = await dbContext.AddressChangeRequests.AsNoTracking()
            .Where(r => r.address_change_id == addressChangeId)
            .Select(ToRowExpression)
            .FirstOrDefaultAsync(cancellationToken);
        return row is null ? null : (await WithReleaseAsync([row], cancellationToken))[0];
    }

    public async Task<IReadOnlyList<AddressChangeRequestRow>> ListByVendorAsync(long vendorId, CancellationToken cancellationToken)
    {
        var rows = await dbContext.AddressChangeRequests.AsNoTracking()
            .Where(r => r.registration.vendor_id == vendorId)
            .OrderByDescending(r => r.created_at)
            .Select(ToRowExpression)
            .ToListAsync(cancellationToken);
        return await WithReleaseAsync(rows, cancellationToken);
    }

    public async Task<IReadOnlyList<PendingReleaseRow>> ListPendingReleasesAsync(CancellationToken cancellationToken)
    {
        var open = await dbContext.AddressChangeRequests.AsNoTracking()
            .Where(r => r.released_contract_id != null
                && AddressChangeStatuses.Open.Contains(r.change_status)
                && r.released_contract!.contract_status == ContractStatuses.Active)
            .Select(r => new
            {
                r.address_change_id,
                r.registration_id,
                VendorId = r.registration.vendor_id,
                VendorUserId = r.registration.vendor.user_id,
                ContractId = r.released_contract_id!.Value,
                SlotCode = r.released_contract!.slot.slot_code,
                r.created_at,
            })
            .ToListAsync(cancellationToken);
        var extensions = await AddressChangeReleaseQueries.ExtensionDaysAsync(
            dbContext, open.Select(r => r.address_change_id).ToArray(), cancellationToken);
        var debt = await AddressChangeReleaseQueries.DebtByContractAsync(
            dbContext, open.Select(r => r.ContractId).ToArray(), cancellationToken);
        return open
            .Select(r => new PendingReleaseRow(
                r.address_change_id,
                r.registration_id,
                r.VendorId,
                r.VendorUserId,
                r.ContractId,
                r.SlotCode,
                r.created_at,
                extensions.GetValueOrDefault(r.address_change_id),
                debt.GetValueOrDefault(r.ContractId) > 0m))
            .ToArray();
    }

    public async Task RecordReleaseAsync(PendingReleaseRow release, CancellationToken cancellationToken)
    {
        dbContext.Notifications.Add(Notice(
            release,
            AdjacentSlotRelease.ReleasedNotificationType,
            "Đã trả ô cũ",
            $"Ô {release.SlotCode} đã được trả sau thời gian ân hạn của yêu cầu đổi địa chỉ."));
        dbContext.AuditLogs.Add(new AuditLog
        {
            actor_user_id = release.VendorUserId,
            action = AdjacentSlotRelease.ReleaseAuditAction,
            entity_type = AdjacentSlotRelease.NotificationEntityType,
            entity_id = release.AddressChangeId,
            details = JsonSerializer.Serialize(new { release.ContractId, release.SlotCode, GraceEndsOn = release.GraceEndsOn.ToString("yyyy-MM-dd") }),
            created_at = Now,
        });
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task NotifyReleaseBlockedAsync(PendingReleaseRow release, DateOnly today, CancellationToken cancellationToken)
    {
        var startOfToday = BusinessCalendar.StartOfDayUtc(today);
        var alreadyToday = await dbContext.Notifications.AsNoTracking().AnyAsync(
            n => n.user_id == release.VendorUserId
                && n.notification_type == AdjacentSlotRelease.BlockedNotificationType
                && n.related_entity_type == AdjacentSlotRelease.NotificationEntityType
                && n.related_entity_id == release.AddressChangeId
                && n.sent_at >= startOfToday,
            cancellationToken);
        if (alreadyToday)
        {
            return;
        }

        dbContext.Notifications.Add(Notice(
            release,
            AdjacentSlotRelease.BlockedNotificationType,
            "Chưa thể trả ô cũ",
            $"Đã hết thời gian ân hạn nhưng ô {release.SlotCode} còn phí quá hạn hoặc tiền phạt chưa nộp. Hãy thanh toán để ô được trả."));
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    private Notification Notice(PendingReleaseRow release, string type, string title, string body) => new()
    {
        user_id = release.VendorUserId,
        notification_type = type,
        title = title,
        body = body,
        related_entity_type = AdjacentSlotRelease.NotificationEntityType,
        related_entity_id = release.AddressChangeId,
        sent_at = Now,
    };

    /// <summary>Fills in where the slot being given up stands: waiting out its grace, held back by a debt, or already released.</summary>
    private async Task<IReadOnlyList<AddressChangeRequestRow>> WithReleaseAsync(
        IReadOnlyList<AddressChangeRequestRow> rows,
        CancellationToken cancellationToken)
    {
        var named = rows.Where(r => r.ReleasedContractId.HasValue).ToArray();
        if (named.Length == 0)
        {
            return rows;
        }

        var contractIds = named.Select(r => r.ReleasedContractId!.Value).Distinct().ToArray();
        var contracts = await dbContext.RentalContracts.AsNoTracking()
            .Where(c => contractIds.Contains(c.contract_id))
            .Select(c => new { c.contract_id, c.contract_status, c.slot.slot_code })
            .ToDictionaryAsync(c => c.contract_id, cancellationToken);
        var extensions = await AddressChangeReleaseQueries.ExtensionDaysAsync(
            dbContext, named.Select(r => r.AddressChangeId).ToArray(), cancellationToken);
        var debt = await AddressChangeReleaseQueries.DebtByContractAsync(dbContext, contractIds, cancellationToken);

        return rows.Select(row =>
        {
            if (row.ReleasedContractId is not { } contractId || !contracts.TryGetValue(contractId, out var contract))
            {
                return row;
            }

            if (contract.contract_status == ContractStatuses.Cancelled)
            {
                return row with { ReleasedSlotCode = contract.slot_code, ReleaseState = AdjacentSlotRelease.Released };
            }

            var waiting = contract.contract_status == ContractStatuses.Active
                && AddressChangeStatuses.Open.Contains(row.ChangeStatus);
            return waiting
                ? row with
                {
                    ReleasedSlotCode = contract.slot_code,
                    ReleaseState = AdjacentSlotRelease.PendingRelease,
                    ReleaseGraceEndsOn = AdjacentSlotRelease.GraceEndsOn(
                        row.CreatedAt, extensions.GetValueOrDefault(row.AddressChangeId)),
                    ReleaseBlockedByDebt = debt.GetValueOrDefault(contractId) > 0m,
                }
                : row with { ReleasedSlotCode = contract.slot_code };
        }).ToArray();
    }

    private static readonly System.Linq.Expressions.Expression<Func<AddressChangeRequest, AddressChangeRequestRow>> ToRowExpression =
        r => new AddressChangeRequestRow(
            r.address_change_id, r.registration_id, r.new_address, r.new_latitude, r.new_longitude,
            r.released_contract_id, r.requested_new_slot_id, r.change_status,
            r.conflict_resolution_note, r.reviewed_at, r.created_at,
            null, null, null, false);
}
