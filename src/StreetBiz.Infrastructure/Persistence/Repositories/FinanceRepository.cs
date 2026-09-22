using System.Data;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using StreetBiz.Application.Common.Exceptions;
using StreetBiz.Application.Common.Interfaces;
using StreetBiz.Application.Common.Models;
using StreetBiz.Application.Common.Security;
using StreetBiz.Infrastructure.Persistence.ScaffoldedModels;

namespace StreetBiz.Infrastructure.Persistence.Repositories;

public sealed class FinanceRepository(
    StreetBizDbContext db,
    TimeProvider clock) : IFinanceRepository
{
    private DateTime Now => clock.GetUtcNow().UtcDateTime;

    public Task<FeeScheduleContextRow?> GetFeeScheduleContextAsync(
        long contractId,
        CancellationToken cancellationToken) =>
        db.RentalContracts.AsNoTracking()
            .Where(contract => contract.contract_id == contractId)
            .Select(contract => new FeeScheduleContextRow(
                contract.contract_id,
                contract.slot_id,
                contract.slot.slot_code,
                contract.slot.zone.zone_id,
                contract.slot.zone.zone_name,
                contract.vendor_id,
                contract.vendor.user_id,
                contract.start_date,
                contract.end_date,
                contract.contract_status,
                contract.application.requested_term_days,
                contract.slot.zone.price_per_day,
                contract.slot.zone.ZoneFeeComponents
                    .OrderBy(component => component.sort_order)
                    .ThenBy(component => component.component_id)
                    .Select(component => new FeeComponentRow(
                        component.component_id,
                        component.component_name,
                        component.calc_basis,
                        component.unit_amount,
                        component.sort_order))
                    .ToList()))
            .FirstOrDefaultAsync(cancellationToken);

    public async Task<FeeScheduleRow> ReplaceFeeScheduleAsync(
        long contractId,
        long actorUserId,
        decimal total,
        IReadOnlyList<FeeInstalment> instalments,
        CancellationToken cancellationToken)
    {
        var strategy = db.Database.CreateExecutionStrategy();
        var scheduleId = await strategy.ExecuteAsync(async () =>
        {
            db.ChangeTracker.Clear();
            await using var transaction = await db.Database.BeginTransactionAsync(
                IsolationLevel.Serializable, cancellationToken);

            var current = await db.FeeSchedules
                .Include(schedule => schedule.FeeScheduleItems)
                .SingleOrDefaultAsync(
                    schedule => schedule.contract_id == contractId && schedule.superseded_at == null,
                    cancellationToken);

            if (current is not null
                && current.FeeScheduleItems.Any(item => item.item_status == FeeItemStatuses.Paid))
            {
                // Superseding a schedule the vendor has already paid into would hide those
                // payments from the current revision and leave their invoices pointing at a
                // closed schedule. Reworking a part-paid contract needs a proration decision
                // that BR-18 does not make, so it is refused here rather than guessed at.
                throw new ConflictException(FinanceMessages.ScheduleAlreadyPaidInto);
            }

            var now = Now;
            if (current is not null)
            {
                current.superseded_at = now;
            }

            // UQ_FeeSchedules_ContractRevision is over every revision, not just the open one, so
            // the next number comes from the highest ever used for this contract.
            var highestRevision = await db.FeeSchedules
                .Where(schedule => schedule.contract_id == contractId)
                .MaxAsync(schedule => (int?)schedule.revision, cancellationToken) ?? 0;

            var schedule = new FeeSchedule
            {
                contract_id = contractId,
                revision = highestRevision + 1,
                total_amount = total,
                generated_at = now,
            };

            foreach (var instalment in instalments.OrderBy(item => item.Ordinal))
            {
                schedule.FeeScheduleItems.Add(new FeeScheduleItem
                {
                    due_date = instalment.DueDate,
                    amount = instalment.Amount,
                    item_status = FeeItemStatuses.Pending,
                });
            }

            db.FeeSchedules.Add(schedule);

            db.AuditLogs.Add(new AuditLog
            {
                actor_user_id = actorUserId,
                action = "FEE_SCHEDULE_GENERATED",
                entity_type = "RentalContract",
                entity_id = contractId,
                details = JsonSerializer.Serialize(new
                {
                    revision = schedule.revision,
                    total,
                    instalments = instalments.Count,
                    supersededScheduleId = current?.fee_schedule_id,
                }),
                created_at = now,
            });

            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return schedule.fee_schedule_id;
        });

        return await ReadScheduleAsync(scheduleId, cancellationToken)
            ?? throw new NotFoundException(FinanceMessages.FeeScheduleNotFound);
    }

    public async Task<FeeScheduleRow?> GetCurrentFeeScheduleAsync(
        long contractId,
        CancellationToken cancellationToken)
    {
        var scheduleId = await db.FeeSchedules.AsNoTracking()
            .Where(schedule => schedule.contract_id == contractId && schedule.superseded_at == null)
            .Select(schedule => (long?)schedule.fee_schedule_id)
            .FirstOrDefaultAsync(cancellationToken);

        return scheduleId is null ? null : await ReadScheduleAsync(scheduleId.Value, cancellationToken);
    }

    private async Task<FeeScheduleRow?> ReadScheduleAsync(long scheduleId, CancellationToken cancellationToken)
    {
        var schedule = await db.FeeSchedules.AsNoTracking()
            .Where(row => row.fee_schedule_id == scheduleId)
            .Select(row => new
            {
                row.fee_schedule_id,
                row.contract_id,
                row.revision,
                row.total_amount,
                row.generated_at,
                row.superseded_at,
                Items = row.FeeScheduleItems
                    .OrderBy(item => item.due_date)
                    .ThenBy(item => item.fee_item_id)
                    .Select(item => new
                    {
                        item.fee_item_id,
                        item.due_date,
                        item.amount,
                        item.item_status,
                        item.paid_at,
                    })
                    .ToList(),
            })
            .FirstOrDefaultAsync(cancellationToken);

        if (schedule is null)
        {
            return null;
        }

        var count = schedule.Items.Count;
        var items = schedule.Items
            .Select((item, index) => new FeeItemRow(
                item.fee_item_id,
                schedule.fee_schedule_id,
                index + 1,
                count,
                item.due_date,
                item.amount,
                item.item_status,
                item.paid_at))
            .ToList();

        return new FeeScheduleRow(
            schedule.fee_schedule_id,
            schedule.contract_id,
            schedule.revision,
            schedule.total_amount,
            schedule.generated_at,
            schedule.superseded_at,
            items);
    }
}
