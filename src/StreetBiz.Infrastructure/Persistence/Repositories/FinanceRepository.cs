using System.Data;
using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using StreetBiz.Application.Common.Exceptions;
using StreetBiz.Application.Common.Interfaces;
using StreetBiz.Application.Common.Models;
using StreetBiz.Application.Common.Security;
using StreetBiz.Application.DTOs.Finance;
using StreetBiz.Infrastructure.Payments;
using StreetBiz.Infrastructure.Persistence.ScaffoldedModels;

namespace StreetBiz.Infrastructure.Persistence.Repositories;

public sealed class FinanceRepository(
    StreetBizDbContext db,
    TimeProvider clock,
    IOptions<PaymentGatewaySettings> paymentSettings) : IFinanceRepository
{
    private static readonly CultureInfo Vietnamese = CultureInfo.GetCultureInfo("vi-VN");

    private DateTime Now => clock.GetUtcNow().UtcDateTime;

    /// <summary>"1.040.000 đ" whatever the server's own culture is.</summary>
    private static string Vnd(decimal amount) => $"{amount.ToString("N0", Vietnamese)} đ";

    private static string Day(DateOnly date) => date.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture);

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
                    .ToList(),
                contract.slot.zone.price_per_month,
                contract.slot.zone.rental_mode,
                contract.slot.zone.quarter_discount_percent,
                contract.slot.price_override))
            .FirstOrDefaultAsync(cancellationToken);

    public async Task<FeeScheduleRow> ReplaceFeeScheduleAsync(
        long contractId,
        long actorUserId,
        decimal total,
        IReadOnlyList<FeeInstalment> instalments,
        CancellationToken cancellationToken)
    {
        long scheduleId;
        if (db.Database.CurrentTransaction is not null)
        {
            // Published from inside the approver's own transaction (WARD-08/09 raising
            // RentalContractApprovedEvent, as that event requires): join it. Clearing the change
            // tracker would discard the approver's pending writes, and opening a second
            // transaction on the same connection throws.
            scheduleId = await WriteScheduleAsync(contractId, actorUserId, total, instalments, cancellationToken);
        }
        else
        {
            var strategy = db.Database.CreateExecutionStrategy();
            scheduleId = await strategy.ExecuteAsync(async () =>
            {
                db.ChangeTracker.Clear();
                await using var transaction = await db.Database.BeginTransactionAsync(
                    IsolationLevel.Serializable, cancellationToken);
                var id = await WriteScheduleAsync(contractId, actorUserId, total, instalments, cancellationToken);
                await transaction.CommitAsync(cancellationToken);
                return id;
            });
        }

        return await ReadScheduleAsync(scheduleId, cancellationToken)
            ?? throw new NotFoundException(FinanceMessages.FeeScheduleNotFound);
    }

    private async Task<long> WriteScheduleAsync(
        long contractId,
        long actorUserId,
        decimal total,
        IReadOnlyList<FeeInstalment> instalments,
        CancellationToken cancellationToken)
    {
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
                deposit_amount = instalment.Deposit,
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
        return schedule.fee_schedule_id;
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

    public async Task<FeeScheduleRow> AppendInstalmentsAsync(
        long contractId, long actorUserId, DateOnly firstDay, FeePlan plan, CancellationToken cancellationToken)
    {
        long scheduleId;
        if (db.Database.CurrentTransaction is not null)
        {
            // Published from inside the renewal's own transaction: join it, as ReplaceFeeScheduleAsync does.
            scheduleId = await WriteExtensionAsync(contractId, actorUserId, firstDay, plan, cancellationToken);
        }
        else
        {
            var strategy = db.Database.CreateExecutionStrategy();
            scheduleId = await strategy.ExecuteAsync(async () =>
            {
                db.ChangeTracker.Clear();
                await using var transaction = await db.Database.BeginTransactionAsync(
                    IsolationLevel.Serializable, cancellationToken);
                var id = await WriteExtensionAsync(contractId, actorUserId, firstDay, plan, cancellationToken);
                await transaction.CommitAsync(cancellationToken);
                return id;
            });
        }

        return await ReadScheduleAsync(scheduleId, cancellationToken)
            ?? throw new NotFoundException(FinanceMessages.FeeScheduleNotFound);
    }

    private async Task<long> WriteExtensionAsync(
        long contractId, long actorUserId, DateOnly firstDay, FeePlan plan, CancellationToken cancellationToken)
    {
        var current = await db.FeeSchedules
            .Include(schedule => schedule.FeeScheduleItems)
            .SingleOrDefaultAsync(
                schedule => schedule.contract_id == contractId && schedule.superseded_at == null,
                cancellationToken)
            ?? throw new NotFoundException(FinanceMessages.FeeScheduleNotFound);

        // Every instalment of the current term is due before the renewal's first day, so one due
        // on or after it means these days were billed already (a renewal published twice).
        if (current.FeeScheduleItems.Any(item =>
                item.due_date >= firstDay && item.item_status != FeeItemStatuses.Cancelled))
        {
            throw new ConflictException(FinanceMessages.ExtensionAlreadyBilled);
        }

        foreach (var instalment in plan.Instalments.OrderBy(item => item.Ordinal))
        {
            current.FeeScheduleItems.Add(new FeeScheduleItem
            {
                due_date = instalment.DueDate,
                amount = instalment.Amount,
                item_status = FeeItemStatuses.Pending,
            });
        }

        current.total_amount += plan.Total;
        db.AuditLogs.Add(new AuditLog
        {
            actor_user_id = actorUserId,
            action = "FEE_SCHEDULE_EXTENDED",
            entity_type = "RentalContract",
            entity_id = contractId,
            details = JsonSerializer.Serialize(new
            {
                scheduleId = current.fee_schedule_id,
                firstDay,
                lastDay = plan.Instalments.Max(item => item.PeriodEnd),
                total = plan.Total,
                instalments = plan.Instalments.Count,
            }),
            created_at = Now,
        });

        await db.SaveChangesAsync(cancellationToken);
        return current.fee_schedule_id;
    }

    public async Task<IReadOnlyList<UnbilledContractRow>> ListUnbilledContractsAsync(
        DateOnly today, CancellationToken cancellationToken) =>
        await db.RentalContracts.AsNoTracking()
            .Where(contract => contract.contract_status == ContractStatuses.Active
                && contract.end_date >= today
                && contract.application.reviewed_by != null
                && !db.FeeSchedules.Any(schedule => schedule.contract_id == contract.contract_id
                    && schedule.superseded_at == null
                    && schedule.FeeScheduleItems.Any())
                && !db.FeeScheduleItems.Any(item => item.fee_schedule.contract_id == contract.contract_id
                    && item.item_status == FeeItemStatuses.Paid))
            .OrderBy(contract => contract.contract_id)
            .Select(contract => new UnbilledContractRow(contract.contract_id, contract.application.reviewed_by!.Value))
            .ToListAsync(cancellationToken);

    public async Task<int> CancelInstalmentsAfterEarlyEndAsync(CancellationToken cancellationToken)
    {
        var strategy = db.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            db.ChangeTracker.Clear();
            await using var transaction = await db.Database.BeginTransactionAsync(
                IsolationLevel.Serializable, cancellationToken);

            var open = await db.FeeScheduleItems
                .Where(item => (item.item_status == FeeItemStatuses.Pending || item.item_status == FeeItemStatuses.Overdue)
                    && item.fee_schedule.superseded_at == null
                    && (item.fee_schedule.contract.contract_status == ContractStatuses.Cancelled
                        || item.fee_schedule.contract.contract_status == ContractStatuses.Revoked))
                .Select(item => new
                {
                    Item = item,
                    item.fee_schedule.contract_id,
                    item.fee_schedule.contract.contract_status,
                    item.fee_schedule.contract.cancelled_at,
                    VendorUserId = item.fee_schedule.contract.vendor.user_id,
                    SlotCode = item.fee_schedule.contract.slot.slot_code,
                    FirstDue = item.fee_schedule.FeeScheduleItems.Min(sibling => sibling.due_date),
                })
                .ToListAsync(cancellationToken);
            if (open.Count == 0)
            {
                return 0;
            }

            var revokedContracts = open.Where(row => row.contract_status == ContractStatuses.Revoked)
                .Select(row => row.contract_id).Distinct().ToList();
            var revokedAt = (await db.DigitalPermits.AsNoTracking()
                    .Where(permit => revokedContracts.Contains(permit.contract_id) && permit.revoked_at != null)
                    .Select(permit => new { permit.contract_id, permit.revoked_at })
                    .ToListAsync(cancellationToken))
                .GroupBy(permit => permit.contract_id)
                .ToDictionary(group => group.Key, group => group.Max(permit => permit.revoked_at!.Value));

            var now = Now;
            var closed = 0;
            foreach (var contract in open.GroupBy(row => row.contract_id))
            {
                var first = contract.First();
                DateTime? endedAt = first.contract_status == ContractStatuses.Cancelled
                    ? first.cancelled_at
                    : revokedAt.TryGetValue(contract.Key, out var at) ? at : null;
                if (endedAt is null)
                {
                    continue;
                }

                // An instalment is owed for a period that began while the contract was in force.
                // Each one is due the day its period starts, so a due date after the last day
                // means a period that never began — except the first instalment, whose due date
                // may sit a grace period past its start.
                var lastDay = BusinessCalendar.DateOf(endedAt.Value);
                var toClose = contract
                    .Where(row => row.Item.due_date > lastDay && row.Item.due_date != row.FirstDue)
                    .ToList();
                if (toClose.Count == 0)
                {
                    continue;
                }

                foreach (var row in toClose)
                {
                    row.Item.item_status = FeeItemStatuses.Cancelled;
                }

                closed += toClose.Count;
                Notify(first.VendorUserId, FinanceNotificationTypes.Fee,
                    "Đã huỷ các kỳ phí sau khi kết thúc hợp đồng",
                    $"Hợp đồng ô {first.SlotCode} đã kết thúc ngày {Day(lastDay)}. {toClose.Count} kỳ phí bắt đầu sau ngày "
                        + $"này ({Vnd(toClose.Sum(row => row.Item.amount))}) đã được huỷ, bạn không phải thanh toán.",
                    "RentalContract", contract.Key, now);
            }

            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return closed;
        });
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

    // ------------------------------------------------------------------
    // FEE-01 checkout
    // ------------------------------------------------------------------

    public Task<FeeItemCheckoutRow?> GetFeeItemForCheckoutAsync(
        long vendorId, long feeItemId, CancellationToken cancellationToken) =>
        db.FeeScheduleItems.AsNoTracking()
            .Where(item => item.fee_item_id == feeItemId
                && item.fee_schedule.contract.vendor_id == vendorId
                && item.fee_schedule.superseded_at == null)
            .Select(item => new FeeItemCheckoutRow(
                item.fee_item_id,
                item.fee_schedule.contract.vendor_id,
                item.fee_schedule.contract.vendor.user_id,
                item.fee_schedule.contract.slot.slot_code,
                item.fee_schedule.FeeScheduleItems.Count(sibling => sibling.due_date < item.due_date) + 1,
                item.fee_schedule.FeeScheduleItems.Count(),
                item.due_date,
                item.amount,
                item.item_status))
            .FirstOrDefaultAsync(cancellationToken);

    public async Task<FinanceCheckoutTransactionRow> CreateFeeCheckoutAsync(
        long vendorId, long feeItemId, string provider, string idempotencyKey, CancellationToken cancellationToken)
    {
        var strategy = db.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            db.ChangeTracker.Clear();
            await using var transaction = await db.Database.BeginTransactionAsync(
                IsolationLevel.Serializable, cancellationToken);

            var item = await db.FeeScheduleItems
                .SingleOrDefaultAsync(
                    row => row.fee_item_id == feeItemId
                        && row.fee_schedule.contract.vendor_id == vendorId
                        && row.fee_schedule.superseded_at == null,
                    cancellationToken)
                ?? throw new NotFoundException(FinanceMessages.FeeItemNotFound);

            var replay = await FindCheckoutReplayAsync(idempotencyKey, feeItemId, null, provider, cancellationToken);
            if (replay is not null)
            {
                return replay;
            }

            if (!FeeItemStatuses.IsOutstanding(item.item_status))
            {
                // Re-checked here, inside the write transaction, because the vendor's own
                // GetFeeItemForCheckoutAsync read (which showed the "Thanh toán" button) happened
                // outside it and could be stale by the time this runs.
                throw new DomainRuleException(FinanceMessages.FeeItemNoLongerPayable);
            }

            // Paid in due-date order, so an overdue instalment is never skipped while a later one
            // is settled — the renewal and slot-return debt checks look for overdue ones.
            var earlier = await db.FeeScheduleItems.AsNoTracking()
                .Where(sibling => sibling.fee_schedule_id == item.fee_schedule_id
                    && sibling.due_date < item.due_date
                    && (sibling.item_status == FeeItemStatuses.Pending || sibling.item_status == FeeItemStatuses.Overdue))
                .OrderBy(sibling => sibling.due_date)
                .Select(sibling => (DateOnly?)sibling.due_date)
                .FirstOrDefaultAsync(cancellationToken);
            if (earlier is { } earlierDue)
            {
                var ordinal = await db.FeeScheduleItems.CountAsync(
                    sibling => sibling.fee_schedule_id == item.fee_schedule_id && sibling.due_date < earlierDue,
                    cancellationToken) + 1;
                throw new DomainRuleException(FinanceMessages.EarlierInstalmentFirst(ordinal, earlierDue));
            }

            var open = await TakeOverOpenPaymentAsync(
                db.PaymentTransactions.Where(payment => payment.fee_item_id == feeItemId), provider, cancellationToken);
            if (open is not null)
            {
                await transaction.CommitAsync(cancellationToken);
                return open;
            }

            var payment = new PaymentTransaction
            {
                idempotency_key = idempotencyKey,
                payment_purpose = PaymentPurposes.RentalFee,
                fee_item_id = feeItemId,
                provider = provider,
                amount = item.amount,
                transaction_status = PaymentStatuses.Pending,
                created_at = Now,
            };
            db.PaymentTransactions.Add(payment);
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return new FinanceCheckoutTransactionRow(payment.transaction_id, idempotencyKey, provider, item.amount);
        });
    }

    // ------------------------------------------------------------------
    // FEE-04 checkout
    // ------------------------------------------------------------------

    public Task<PenaltyCheckoutRow?> GetPenaltyForCheckoutAsync(
        long vendorId, long penaltyId, CancellationToken cancellationToken) =>
        db.Penalties.AsNoTracking()
            .Where(penalty => penalty.penalty_id == penaltyId && penalty.violation.vendor_id == vendorId)
            .Select(penalty => new PenaltyCheckoutRow(
                penalty.penalty_id,
                penalty.violation.vendor_id!.Value,
                penalty.violation.vendor!.user_id,
                penalty.violation.violation_typeNavigation.description,
                penalty.violation.slot != null ? penalty.violation.slot.slot_code : null,
                penalty.amount,
                penalty.penalty_status))
            .FirstOrDefaultAsync(cancellationToken);

    public async Task<FinanceCheckoutTransactionRow> CreatePenaltyCheckoutAsync(
        long vendorId, long penaltyId, string provider, string idempotencyKey, CancellationToken cancellationToken)
    {
        var strategy = db.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            db.ChangeTracker.Clear();
            await using var transaction = await db.Database.BeginTransactionAsync(
                IsolationLevel.Serializable, cancellationToken);

            var penalty = await db.Penalties
                .SingleOrDefaultAsync(
                    row => row.penalty_id == penaltyId && row.violation.vendor_id == vendorId,
                    cancellationToken)
                ?? throw new NotFoundException(FinanceMessages.PenaltyNotFound);

            var replay = await FindCheckoutReplayAsync(idempotencyKey, null, penaltyId, provider, cancellationToken);
            if (replay is not null)
            {
                return replay;
            }

            if (!PenaltyStatuses.IsPayable(penalty.penalty_status))
            {
                throw new DomainRuleException(FinanceMessages.PenaltyNoLongerPayable);
            }

            var open = await TakeOverOpenPaymentAsync(
                db.PaymentTransactions.Where(payment => payment.penalty_id == penaltyId), provider, cancellationToken);
            if (open is not null)
            {
                await transaction.CommitAsync(cancellationToken);
                return open;
            }

            var payment = new PaymentTransaction
            {
                idempotency_key = idempotencyKey,
                payment_purpose = PaymentPurposes.Penalty,
                penalty_id = penaltyId,
                provider = provider,
                amount = penalty.amount,
                transaction_status = PaymentStatuses.Pending,
                created_at = Now,
            };
            db.PaymentTransactions.Add(payment);
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return new FinanceCheckoutTransactionRow(payment.transaction_id, idempotencyKey, provider, penalty.amount);
        });
    }

    /// <summary>
    /// Idempotency-Key replay, mirroring Commerce's checkout: the same key for the same
    /// instalment/penalty and provider is a client retry after a lost response and gets the
    /// original transaction back. The key reused for anything else is a conflict — without this
    /// the retry would hit the UNIQUE constraint on idempotency_key and surface as a 500.
    /// Callers have already scoped the target to the calling vendor, so a match on the target id
    /// is also a match on ownership.
    /// </summary>
    private async Task<FinanceCheckoutTransactionRow?> FindCheckoutReplayAsync(
        string idempotencyKey, long? feeItemId, long? penaltyId, string provider, CancellationToken cancellationToken)
    {
        var existing = await db.PaymentTransactions.AsNoTracking()
            .Where(payment => payment.idempotency_key == idempotencyKey)
            .Select(payment => new
            {
                payment.transaction_id,
                payment.fee_item_id,
                payment.penalty_id,
                payment.provider,
                payment.amount,
            })
            .SingleOrDefaultAsync(cancellationToken);

        if (existing is null)
        {
            return null;
        }

        if (existing.fee_item_id == feeItemId && existing.penalty_id == penaltyId && existing.provider == provider)
        {
            return new FinanceCheckoutTransactionRow(existing.transaction_id, idempotencyKey, provider, existing.amount);
        }

        throw new ConflictException(FinanceMessages.IdempotencyKeyReused);
    }

    /// <summary>
    /// One PENDING payment per instalment/penalty: two open at once is how a vendor ends up paying
    /// twice (two tabs, or MoMo abandoned for ZaloPay). An open one through the same provider is
    /// handed back — the gateway then opens a fresh page for it — and any other is closed first.
    /// Closing is safe: a closed payment that is paid anyway is still applied or owed back.
    /// </summary>
    private async Task<FinanceCheckoutTransactionRow?> TakeOverOpenPaymentAsync(
        IQueryable<PaymentTransaction> targetPayments, string provider, CancellationToken cancellationToken)
    {
        var open = await targetPayments
            .Where(payment => payment.transaction_status == PaymentStatuses.Pending)
            .OrderByDescending(payment => payment.created_at)
            .ThenByDescending(payment => payment.transaction_id)
            .ToListAsync(cancellationToken);
        if (open.Count == 0)
        {
            return null;
        }

        var kept = open.FirstOrDefault(payment => payment.provider == provider);
        foreach (var other in open.Where(payment => payment != kept))
        {
            other.transaction_status = PaymentStatuses.Failed;
        }

        await db.SaveChangesAsync(cancellationToken);
        return kept is null
            ? null
            : new FinanceCheckoutTransactionRow(kept.transaction_id, kept.idempotency_key, kept.provider, kept.amount);
    }

    public async Task SetPaymentProviderReferenceAsync(
        long transactionId, string providerReference, CancellationToken cancellationToken)
    {
        var payment = await db.PaymentTransactions.FindAsync([transactionId], cancellationToken);
        if (payment is not null)
        {
            payment.provider_reference = providerReference;
            await db.SaveChangesAsync(cancellationToken);
        }
    }

    /// <summary>
    /// The receipt's extra facts, each read on its own: one projection spanning the fee and the
    /// penalty navigation chains together is the shape EF Core failed to translate before (see
    /// RawInvoiceQuery), and a receipt is read one at a time, so a few small queries cost nothing.
    /// </summary>
    public async Task<InvoiceDocumentRow?> GetInvoiceDocumentAsync(
        long vendorId, long invoiceId, CancellationToken cancellationToken)
    {
        var invoice = await GetInvoiceAsync(vendorId, invoiceId, cancellationToken);
        if (invoice is null)
        {
            return null;
        }

        long? slotId;
        string? businessName;
        string? decisionNumber = null;
        string? providerReference;
        if (invoice.FeeItemId is { } feeItemId)
        {
            var fee = await db.FeeScheduleItems.AsNoTracking()
                .Where(item => item.fee_item_id == feeItemId)
                .Select(item => new
                {
                    item.fee_schedule.contract.slot_id,
                    item.fee_schedule.contract.application.registration.display_name,
                })
                .FirstAsync(cancellationToken);
            slotId = fee.slot_id;
            businessName = fee.display_name;
            providerReference = await SuccessfulReferenceAsync(
                db.PaymentTransactions.Where(payment => payment.fee_item_id == feeItemId), cancellationToken);
        }
        else
        {
            var penaltyId = invoice.PenaltyId!.Value;
            var penalty = await db.Penalties.AsNoTracking()
                .Where(row => row.penalty_id == penaltyId)
                .Select(row => new
                {
                    row.violation.slot_id,
                    ContractSlotId = row.violation.contract != null ? (long?)row.violation.contract.slot_id : null,
                    BusinessName = row.violation.contract != null
                        ? row.violation.contract.application.registration.display_name
                        : null,
                    row.decision_number,
                })
                .FirstAsync(cancellationToken);
            slotId = penalty.slot_id ?? penalty.ContractSlotId;
            businessName = penalty.BusinessName;
            decisionNumber = penalty.decision_number;
            providerReference = await SuccessfulReferenceAsync(
                db.PaymentTransactions.Where(payment => payment.penalty_id == penaltyId), cancellationToken);
        }

        var wardUnitId = slotId is null
            ? null
            : await db.SidewalkSlots.AsNoTracking()
                .Where(slot => slot.slot_id == slotId)
                .Select(slot => (int?)slot.zone.ward_unit_id)
                .FirstOrDefaultAsync(cancellationToken);
        var wardName = wardUnitId is null
            ? null
            : await db.AdministrativeUnits.AsNoTracking()
                .Where(unit => unit.unit_id == wardUnitId)
                .Select(unit => unit.unit_name)
                .FirstOrDefaultAsync(cancellationToken);
        var payer = await db.Vendors.AsNoTracking()
            .Where(vendor => vendor.vendor_id == vendorId)
            .Select(vendor => new { vendor.user.full_name, vendor.user.phone_number })
            .FirstAsync(cancellationToken);

        return new InvoiceDocumentRow(
            invoice.InvoiceNumber,
            invoice.Kind,
            invoice.Amount,
            invoice.IssuedAt,
            wardName,
            payer.full_name,
            payer.phone_number,
            businessName,
            invoice.SlotCode,
            invoice.PeriodLabel,
            invoice.ViolationLabel,
            decisionNumber,
            invoice.PaymentProvider,
            providerReference,
            invoice.PaidAt);
    }

    private static Task<string?> SuccessfulReferenceAsync(
        IQueryable<PaymentTransaction> payments, CancellationToken cancellationToken) =>
        payments.AsNoTracking()
            .Where(payment => payment.transaction_status == PaymentStatuses.Success)
            .OrderByDescending(payment => payment.callback_received_at)
            .Select(payment => payment.provider_reference)
            .FirstOrDefaultAsync(cancellationToken);

    /// <summary>
    /// Only while no provider order was ever opened for it (no provider_reference): with no
    /// payment page, nobody can pay it, so FAILED is the truth. A transaction an earlier attempt
    /// did open stays PENDING — its page may still be paid, and a FAILED status would make that
    /// payment's callback a DUPLICATE, taking money without applying it.
    /// </summary>
    public Task AbandonUnopenedCheckoutAsync(long transactionId, CancellationToken cancellationToken) =>
        db.PaymentTransactions
            .Where(payment => payment.transaction_id == transactionId
                && payment.transaction_status == PaymentStatuses.Pending
                && payment.provider_reference == null)
            .ExecuteUpdateAsync(
                setters => setters.SetProperty(payment => payment.transaction_status, PaymentStatuses.Failed),
                cancellationToken);

    public async Task<IReadOnlyList<StalePaymentRow>> ListStalePaymentsAsync(
        DateTime openedBeforeUtc, CancellationToken cancellationToken) =>
        await db.PaymentTransactions.AsNoTracking()
            .Where(payment => payment.transaction_status == PaymentStatuses.Pending
                && (payment.payment_purpose == PaymentPurposes.RentalFee || payment.payment_purpose == PaymentPurposes.Penalty)
                && payment.created_at < openedBeforeUtc)
            .OrderBy(payment => payment.created_at)
            .Select(payment => new StalePaymentRow(
                payment.transaction_id,
                payment.provider,
                payment.provider_reference,
                payment.idempotency_key,
                payment.created_at))
            .ToListAsync(cancellationToken);

    public async Task<bool> ClosePendingPaymentAsync(long transactionId, CancellationToken cancellationToken) =>
        await db.PaymentTransactions
            .Where(payment => payment.transaction_id == transactionId
                && payment.transaction_status == PaymentStatuses.Pending
                && (payment.payment_purpose == PaymentPurposes.RentalFee || payment.payment_purpose == PaymentPurposes.Penalty))
            .ExecuteUpdateAsync(
                setters => setters.SetProperty(payment => payment.transaction_status, PaymentStatuses.Failed),
                cancellationToken) > 0;

    // ------------------------------------------------------------------
    // SYS-04 callback
    // ------------------------------------------------------------------

    public async Task<string?> FindPaymentPurposeAsync(
        string? providerReference, string? idempotencyKey, CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(providerReference))
        {
            var byReference = await db.PaymentTransactions.AsNoTracking()
                .Where(row => row.provider_reference == providerReference)
                .Select(row => (string?)row.payment_purpose)
                .FirstOrDefaultAsync(cancellationToken);
            if (byReference is not null)
            {
                return byReference;
            }
        }

        if (string.IsNullOrWhiteSpace(idempotencyKey))
        {
            return null;
        }

        return await db.PaymentTransactions.AsNoTracking()
            .Where(row => row.idempotency_key == idempotencyKey)
            .Select(row => (string?)row.payment_purpose)
            .FirstOrDefaultAsync(cancellationToken);
    }

    public async Task<FinanceCallbackMutationResult> ApplyPaymentCallbackAsync(
        PaymentCallbackData callback, CancellationToken cancellationToken)
    {
        var strategy = db.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            db.ChangeTracker.Clear();
            await using var transaction = await db.Database.BeginTransactionAsync(
                IsolationLevel.Serializable, cancellationToken);

            PaymentTransaction? payment = null;
            if (!string.IsNullOrWhiteSpace(callback.ProviderReference))
            {
                payment = await db.PaymentTransactions
                    .Include(row => row.fee_item).ThenInclude(item => item!.fee_schedule).ThenInclude(schedule => schedule.contract)
                    .Include(row => row.penalty).ThenInclude(p => p!.violation)
                    .SingleOrDefaultAsync(row => row.provider_reference == callback.ProviderReference, cancellationToken);
            }

            if (payment is null && !string.IsNullOrWhiteSpace(callback.IdempotencyKey))
            {
                payment = await db.PaymentTransactions
                    .Include(row => row.fee_item).ThenInclude(item => item!.fee_schedule).ThenInclude(schedule => schedule.contract)
                    .Include(row => row.penalty).ThenInclude(p => p!.violation)
                    .SingleOrDefaultAsync(row => row.idempotency_key == callback.IdempotencyKey, cancellationToken);
            }

            var callbackEvent = new PaymentCallbackEvent
            {
                provider = callback.Provider,
                provider_reference = callback.ProviderReference,
                transaction_id = payment?.transaction_id,
                raw_payload = callback.RawPayload,
                signature_valid = callback.SignatureValid,
                processing_result = CallbackResults.Rejected,
                received_at = Now,
            };
            db.PaymentCallbackEvents.Add(callbackEvent);

            async Task<FinanceCallbackMutationResult> Finish(PaymentCallbackOutcome outcome, string databaseResult)
            {
                callbackEvent.processing_result = databaseResult;
                await db.SaveChangesAsync(cancellationToken);
                await transaction.CommitAsync(cancellationToken);
                return new FinanceCallbackMutationResult(outcome, callbackEvent.callback_event_id, payment?.transaction_id);
            }

            if (!callback.SignatureValid)
            {
                return await Finish(PaymentCallbackOutcome.Rejected, CallbackResults.Rejected);
            }

            if (payment is null)
            {
                return await Finish(PaymentCallbackOutcome.Unmatched, CallbackResults.Unmatched);
            }

            var normalizedStatus = callback.Status?.Trim().ToUpperInvariant();
            if (!string.Equals(payment.provider, callback.Provider, StringComparison.Ordinal)
                || callback.Amount != payment.amount
                || normalizedStatus is not (PaymentStatuses.Success or PaymentStatuses.Failed))
            {
                return await Finish(PaymentCallbackOutcome.Rejected, CallbackResults.Rejected);
            }

            // Money already applied or already owed back: a repeated report changes nothing.
            if (PaymentStatuses.IsTerminal(payment.transaction_status))
            {
                return await Finish(PaymentCallbackOutcome.Duplicate, CallbackResults.Duplicate);
            }

            if (normalizedStatus == PaymentStatuses.Failed)
            {
                if (payment.transaction_status == PaymentStatuses.Failed)
                {
                    return await Finish(PaymentCallbackOutcome.Duplicate, CallbackResults.Duplicate);
                }

                if (IsEarlierAttempt(payment.provider_reference, callback.ProviderReference))
                {
                    // A retry opens a fresh MoMo order, so one transaction can have several; only
                    // the latest may fail it. A failure on an earlier, abandoned attempt is recorded
                    // but must not fail a transaction whose newer attempt can still be paid.
                    return await Finish(PaymentCallbackOutcome.Rejected, CallbackResults.Rejected);
                }

                payment.callback_received_at = Now;
                payment.transaction_status = PaymentStatuses.Failed;
                return await Finish(PaymentCallbackOutcome.Applied, CallbackResults.Applied);
            }

            // The provider took the money. That holds even for a payment StreetBiz had closed as
            // FAILED (its page was still open), so the success is never dropped as a duplicate.
            // Keep the reference of the order actually settled — it may be an earlier attempt's —
            // so the receipt and a later reconciliation with the provider name the right one.
            payment.provider_reference = callback.ProviderReference ?? payment.provider_reference;
            payment.callback_received_at = Now;

            if (IsStillPayable(payment))
            {
                payment.transaction_status = PaymentStatuses.Success;
                await ApplySuccessAsync(payment, cancellationToken);
            }
            else
            {
                // Moved on since this payment was opened — another payment settled the same
                // instalment/penalty, or it was closed. Nothing to apply the money to: owed back.
                await MarkRefundRequiredAsync(payment, cancellationToken);
            }

            return await Finish(PaymentCallbackOutcome.Applied, CallbackResults.Applied);
        });
    }

    public async Task<FinancePaymentStateRow?> GetVendorPaymentAsync(
        long vendorId, long transactionId, CancellationToken cancellationToken) =>
        await db.PaymentTransactions.AsNoTracking()
            .Where(row => row.transaction_id == transactionId
                && ((row.fee_item != null && row.fee_item.fee_schedule.contract.vendor_id == vendorId)
                    || (row.penalty != null && row.penalty.violation.vendor_id == vendorId)))
            .Select(row => new FinancePaymentStateRow(
                row.transaction_id, row.provider, row.provider_reference, row.idempotency_key, row.transaction_status))
            .SingleOrDefaultAsync(cancellationToken);

    public async Task<FinanceCallbackMutationResult> ConfirmSandboxSuccessAsync(
        long vendorId, long transactionId, CancellationToken cancellationToken)
    {
        var strategy = db.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            db.ChangeTracker.Clear();
            await using var transaction = await db.Database.BeginTransactionAsync(
                IsolationLevel.Serializable, cancellationToken);

            var payment = await db.PaymentTransactions
                .Include(row => row.fee_item).ThenInclude(item => item!.fee_schedule).ThenInclude(schedule => schedule.contract)
                .Include(row => row.penalty).ThenInclude(p => p!.violation)
                .SingleOrDefaultAsync(row => row.transaction_id == transactionId, cancellationToken)
                ?? throw new NotFoundException(FinanceMessages.TransactionNotFound);

            var owner = payment.fee_item?.fee_schedule.contract.vendor_id ?? payment.penalty?.violation.vendor_id;
            if (owner != vendorId)
            {
                throw new NotFoundException(FinanceMessages.TransactionNotFound);
            }

            // With MoMo's merchant keys configured, a MoMo transaction is only ever settled by
            // MoMo itself (the IPN, or the sync that queries MoMo). This Development shortcut
            // would otherwise mark a real, unpaid MoMo order as received.
            if (payment.provider == PaymentProviders.Momo && paymentSettings.Value.Momo.IsMomoConfigured)
            {
                throw new DomainRuleException(FinanceMessages.SandboxNotAvailableForRealProvider);
            }

            if (payment.transaction_status != PaymentStatuses.Pending)
            {
                throw new ConflictException(FinanceMessages.TransactionAlreadyProcessed);
            }

            if (!IsStillPayable(payment))
            {
                // Same guard as the real callback: a second PENDING attempt on an instalment or
                // penalty another attempt already paid (two tabs, a double tap after a lost
                // response) must not mark it PAID again and issue a second invoice — Invoices has
                // no unique constraint on fee_item_id/penalty_id to stop that at the database.
                throw new DomainRuleException(payment.fee_item is not null
                    ? FinanceMessages.FeeItemNoLongerPayable
                    : FinanceMessages.PenaltyNoLongerPayable);
            }

            payment.transaction_status = PaymentStatuses.Success;
            payment.callback_received_at = Now;
            await ApplySuccessAsync(payment, cancellationToken);

            var callbackEvent = new PaymentCallbackEvent
            {
                provider = payment.provider,
                provider_reference = payment.provider_reference,
                transaction_id = payment.transaction_id,
                raw_payload = "{\"source\":\"development-sandbox-confirm\"}",
                signature_valid = true,
                processing_result = CallbackResults.Applied,
                received_at = Now,
            };
            db.PaymentCallbackEvents.Add(callbackEvent);

            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return new FinanceCallbackMutationResult(
                PaymentCallbackOutcome.Applied, callbackEvent.callback_event_id, payment.transaction_id);
        });
    }

    /// <summary>The callback is about an attempt older than the one provider_reference now points to.</summary>
    private static bool IsEarlierAttempt(string? latestReference, string? reportedReference) =>
        latestReference is not null && reportedReference is not null && latestReference != reportedReference;

    /// <summary>
    /// True while nobody has already paid the instalment/penalty this transaction targets, and
    /// (for an instalment) its schedule has not been superseded since the checkout was opened.
    /// Assumes <c>fee_item.fee_schedule</c> is loaded.
    /// </summary>
    private static bool IsStillPayable(PaymentTransaction payment) =>
        payment.fee_item is { } item
            ? FeeItemStatuses.IsOutstanding(item.item_status) && item.fee_schedule.superseded_at == null
        : payment.penalty is { } penalty ? PenaltyStatuses.IsPayable(penalty.penalty_status)
        : false;

    /// <summary>
    /// Shared by the real callback and the Development sandbox confirm: marks the instalment or
    /// penalty PAID and issues its invoice (SYS-05, BR-32 — only ever after a confirmed payment).
    /// Assumes <c>payment.fee_item</c>/<c>payment.penalty</c> are already loaded.
    /// </summary>
    private async Task ApplySuccessAsync(PaymentTransaction payment, CancellationToken cancellationToken)
    {
        var now = Now;
        long vendorId;
        string? periodLabel = null;

        if (payment.fee_item is { } item)
        {
            item.item_status = FeeItemStatuses.Paid;
            item.paid_at = now;
            vendorId = item.fee_schedule.contract.vendor_id;
            var ordinal = await db.FeeScheduleItems.AsNoTracking()
                .CountAsync(sibling => sibling.fee_schedule_id == item.fee_schedule_id && sibling.due_date < item.due_date, cancellationToken) + 1;
            var ofCount = await db.FeeScheduleItems.AsNoTracking()
                .CountAsync(sibling => sibling.fee_schedule_id == item.fee_schedule_id, cancellationToken);
            periodLabel = FinanceMapper.PeriodLabel(ordinal, ofCount, item.due_date);

            var invoice = new Invoice
            {
                invoice_number = await NextInvoiceNumberAsync(now, cancellationToken),
                fee_item_id = item.fee_item_id,
                vendor_id = vendorId,
                amount = item.amount,
                issued_at = now,
            };
            db.Invoices.Add(invoice);

            Notify(await VendorUserIdAsync(vendorId, cancellationToken), FinanceNotificationTypes.Fee,
                "Thanh toán phí thuê ô thành công",
                $"Đã ghi nhận thanh toán phí thuê ô ({periodLabel}). Hoá đơn đã phát hành.",
                "FeeScheduleItem", item.fee_item_id, now);
        }
        else if (payment.penalty is { } penalty)
        {
            penalty.penalty_status = PenaltyStatuses.Paid;
            penalty.paid_at = now;
            vendorId = penalty.violation.vendor_id
                ?? throw new InvalidOperationException("A penalty being paid must have an identified vendor.");

            var invoice = new Invoice
            {
                invoice_number = await NextInvoiceNumberAsync(now, cancellationToken),
                penalty_id = penalty.penalty_id,
                vendor_id = vendorId,
                amount = penalty.amount,
                issued_at = now,
            };
            db.Invoices.Add(invoice);

            Notify(await VendorUserIdAsync(vendorId, cancellationToken), FinanceNotificationTypes.Penalty,
                "Thanh toán tiền phạt thành công",
                "Biên bản phạt đã được thanh toán. Hoá đơn đã phát hành.",
                "Penalty", penalty.penalty_id, now);
        }
        else
        {
            throw new InvalidOperationException(
                "A RENTAL_FEE or PENALTY payment transaction must carry a fee item or a penalty.");
        }
    }

    /// <summary>
    /// The provider took money that has nothing left to pay: the payment is owed back
    /// (REFUND_REQUIRED, no invoice). The vendor is told, and the ward sees it in its refund list;
    /// the audit trail keeps the provider's reference for the refund.
    /// Assumes <c>payment.fee_item</c>/<c>payment.penalty</c> are already loaded.
    /// </summary>
    private async Task MarkRefundRequiredAsync(PaymentTransaction payment, CancellationToken cancellationToken)
    {
        var now = Now;
        payment.transaction_status = PaymentStatuses.RefundRequired;

        var vendorId = payment.fee_item?.fee_schedule.contract.vendor_id
            ?? payment.penalty?.violation.vendor_id
            ?? throw new InvalidOperationException("A fee or penalty payment must belong to a vendor.");
        var vendorUserId = await VendorUserIdAsync(vendorId, cancellationToken);
        var subject = payment.fee_item is { } item
            ? $"kỳ phí thuê ô hạn {Day(item.due_date)}"
            : "biên bản phạt";

        Notify(vendorUserId,
            payment.fee_item is not null ? FinanceNotificationTypes.Fee : FinanceNotificationTypes.Penalty,
            "Khoản thanh toán sẽ được hoàn lại",
            $"Đã nhận {Vnd(payment.amount)} qua {payment.provider} cho {subject}, nhưng khoản này đã được thanh toán "
                + "trước đó. UBND phường sẽ hoàn lại số tiền này cho bạn.",
            "PaymentTransaction", payment.transaction_id, now);

        db.AuditLogs.Add(new AuditLog
        {
            actor_user_id = vendorUserId,
            action = "FINANCE_PAYMENT_REFUND_REQUIRED",
            entity_type = "PaymentTransaction",
            entity_id = payment.transaction_id,
            details = JsonSerializer.Serialize(new
            {
                payment.provider,
                payment.provider_reference,
                payment.amount,
                feeItemId = payment.fee_item_id,
                penaltyId = payment.penalty_id,
            }),
            created_at = now,
        });
    }

    private async Task<long> VendorUserIdAsync(long vendorId, CancellationToken cancellationToken) =>
        await db.Vendors.AsNoTracking()
            .Where(vendor => vendor.vendor_id == vendorId)
            .Select(vendor => vendor.user_id)
            .FirstAsync(cancellationToken);

    /// <summary>
    /// HD-{year}-{6 digits}, matching the format the demo seed already uses. The sequence is
    /// zero-padded to a fixed width, so the highest number sorts last as a string and one indexed
    /// TOP 1 lookup (UNIQUE on invoice_number) finds it — instead of reading every invoice of the
    /// year. Holds while a year stays under 1,000,000 invoices. The UNIQUE constraint remains the
    /// backstop against a race: under Serializable the loser deadlocks and EnableRetryOnFailure
    /// retries it, rather than a duplicate number ever being written.
    /// </summary>
    private async Task<string> NextInvoiceNumberAsync(DateTime issuedAt, CancellationToken cancellationToken)
    {
        // The Vietnamese calendar year: an invoice issued at 05:00 on 1 January in Đà Nẵng is
        // still 31 December in UTC.
        var year = TimeZoneInfo.ConvertTimeFromUtc(issuedAt, BusinessCalendar.TimeZone).Year;
        var prefix = $"HD-{year}-";
        var last = await db.Invoices.AsNoTracking()
            .Where(invoice => invoice.invoice_number.StartsWith(prefix))
            .OrderByDescending(invoice => invoice.invoice_number)
            .Select(invoice => invoice.invoice_number)
            .FirstOrDefaultAsync(cancellationToken);

        var next = last is not null && int.TryParse(last.AsSpan(prefix.Length), out var sequence)
            ? sequence + 1
            : 1;

        return $"{prefix}{next:D6}";
    }

    private void Notify(
        long userId, string type, string title, string body, string entityType, long entityId, DateTime now) =>
        db.Notifications.Add(new Notification
        {
            user_id = userId,
            notification_type = type,
            title = title,
            body = body,
            related_entity_type = entityType,
            related_entity_id = entityId,
            sent_at = now,
        });

    // ------------------------------------------------------------------
    // FEE-03 invoices
    // ------------------------------------------------------------------

    public async Task<IReadOnlyList<InvoiceRow>> ListInvoicesAsync(long vendorId, CancellationToken cancellationToken)
    {
        var raw = await RawInvoiceQuery(invoice => invoice.vendor_id == vendorId).ToListAsync(cancellationToken);
        return raw.OrderByDescending(row => row.IssuedAt).Select(ToInvoiceRow).ToList();
    }

    public async Task<InvoiceRow?> GetInvoiceAsync(long vendorId, long invoiceId, CancellationToken cancellationToken)
    {
        var raw = await RawInvoiceQuery(invoice => invoice.invoice_id == invoiceId && invoice.vendor_id == vendorId)
            .FirstOrDefaultAsync(cancellationToken);
        return raw is null ? null : ToInvoiceRow(raw);
    }

    /// <summary>
    /// Projects each invoice's join fields in one SQL-translatable shape. The final InvoiceRow
    /// (with its formatted PeriodLabel) is assembled from this in memory, the same way
    /// ReadScheduleAsync builds FeeItemRow — a hand-formatted label does not translate to SQL.
    /// <paramref name="filter"/> must run before this projection: EF Core cannot push a Where
    /// back through this many LEFT JOINs once it has already been applied to the projected shape.
    /// </summary>
    private IQueryable<RawInvoice> RawInvoiceQuery(
        System.Linq.Expressions.Expression<Func<Invoice, bool>> filter) =>
        db.Invoices.AsNoTracking().Where(filter).Select(invoice => new RawInvoice(
            invoice.invoice_id,
            invoice.invoice_number,
            invoice.vendor_id,
            invoice.fee_item_id,
            invoice.penalty_id,
            invoice.amount,
            invoice.issued_at,
            invoice.fee_item != null ? invoice.fee_item.due_date : (DateOnly?)null,
            invoice.fee_item != null
                ? invoice.fee_item.fee_schedule.FeeScheduleItems.Count(sibling => sibling.due_date < invoice.fee_item.due_date) + 1
                : (int?)null,
            invoice.fee_item != null ? invoice.fee_item.fee_schedule.FeeScheduleItems.Count() : (int?)null,
            invoice.fee_item != null ? invoice.fee_item.fee_schedule.contract.slot.slot_code
                : invoice.penalty != null ? invoice.penalty.violation.slot!.slot_code : null,
            invoice.penalty != null ? invoice.penalty.violation.violation_typeNavigation.description : null,
            invoice.fee_item != null
                ? invoice.fee_item.PaymentTransactions
                    .Where(t => t.transaction_status == PaymentStatuses.Success).Select(t => t.provider).FirstOrDefault()
                : invoice.penalty!.PaymentTransactions
                    .Where(t => t.transaction_status == PaymentStatuses.Success).Select(t => t.provider).FirstOrDefault(),
            invoice.fee_item != null ? invoice.fee_item.paid_at : invoice.penalty!.paid_at));

    private static InvoiceRow ToInvoiceRow(RawInvoice row) => new(
        row.InvoiceId,
        row.InvoiceNumber,
        row.FeeItemId is not null ? "FEE" : "PENALTY",
        row.Amount,
        DateTime.SpecifyKind(row.IssuedAt, DateTimeKind.Utc),
        row.FeeItemId,
        row.PenaltyId,
        row is { FeeItemOrdinal: { } ordinal, FeeItemOfCount: { } ofCount, FeeItemDueDate: { } dueDate }
            ? FinanceMapper.PeriodLabel(ordinal, ofCount, dueDate)
            : null,
        row.SlotCode,
        row.ViolationLabel,
        row.PaymentProvider,
        row.PaidAt is null ? null : DateTime.SpecifyKind(row.PaidAt.Value, DateTimeKind.Utc));

    private sealed record RawInvoice(
        long InvoiceId,
        string InvoiceNumber,
        long VendorId,
        long? FeeItemId,
        long? PenaltyId,
        decimal Amount,
        DateTime IssuedAt,
        DateOnly? FeeItemDueDate,
        int? FeeItemOrdinal,
        int? FeeItemOfCount,
        string? SlotCode,
        string? ViolationLabel,
        string? PaymentProvider,
        DateTime? PaidAt);

    // ------------------------------------------------------------------
    // FEE-02 / SYS-06 reminder sweep
    // ------------------------------------------------------------------

    public async Task<FeeReminderSweepResult> RunFeeReminderSweepAsync(
        DateOnly today, CancellationToken cancellationToken)
    {
        var strategy = db.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            db.ChangeTracker.Clear();
            await using var transaction = await db.Database.BeginTransactionAsync(
                IsolationLevel.Serializable, cancellationToken);
            var now = Now;

            // Superseded revisions keep their items as PENDING (the CHECK constraint has no
            // "superseded" status), so every live-debt query must exclude them explicitly.
            var overdue = await db.FeeScheduleItems
                .Include(item => item.fee_schedule).ThenInclude(schedule => schedule.contract).ThenInclude(contract => contract.slot)
                .Include(item => item.fee_schedule).ThenInclude(schedule => schedule.contract).ThenInclude(contract => contract.vendor)
                .Where(item => item.item_status == FeeItemStatuses.Pending
                    && item.due_date < today
                    && item.fee_schedule.superseded_at == null)
                .ToListAsync(cancellationToken);
            foreach (var item in overdue)
            {
                item.item_status = FeeItemStatuses.Overdue;
                var contract = item.fee_schedule.contract;
                Notify(contract.vendor.user_id, FinanceNotificationTypes.Fee,
                    "Phí thuê ô đã quá hạn",
                    $"Khoản phí {Vnd(item.amount)} của ô {contract.slot.slot_code}, hạn {Day(item.due_date)}, "
                        + "đã quá hạn thanh toán.",
                    "FeeScheduleItem", item.fee_item_id, now);
            }

            await db.SaveChangesAsync(cancellationToken);

            var reminderCutoff = today.AddDays(FeeReminderPolicy.ReminderWindowDays);
            var upcoming = await db.FeeScheduleItems
                .Include(item => item.fee_schedule).ThenInclude(schedule => schedule.contract).ThenInclude(contract => contract.slot)
                .Include(item => item.fee_schedule).ThenInclude(schedule => schedule.contract).ThenInclude(contract => contract.vendor)
                .Where(item => item.item_status == FeeItemStatuses.Pending
                    && item.due_date >= today && item.due_date <= reminderCutoff
                    && item.fee_schedule.superseded_at == null
                    // "Sắp đến hạn" is for a contract still running; one that ended early has its
                    // later instalments closed by CancelInstalmentsAfterEarlyEndAsync instead.
                    && (item.fee_schedule.contract.contract_status == ContractStatuses.Active
                        || item.fee_schedule.contract.contract_status == ContractStatuses.Suspended))
                .ToListAsync(cancellationToken);

            // One query for "reminded already today" across every candidate, not one per item.
            var todayStart = BusinessCalendar.StartOfDayUtc(today);
            var upcomingIds = upcoming.Select(item => item.fee_item_id).ToList();
            var remindedToday = (await db.Notifications.AsNoTracking()
                .Where(notification => notification.related_entity_type == "FeeScheduleItem"
                    && notification.notification_type == FinanceNotificationTypes.Fee
                    && notification.sent_at >= todayStart
                    && notification.related_entity_id != null
                    && upcomingIds.Contains(notification.related_entity_id.Value))
                .Select(notification => notification.related_entity_id!.Value)
                .ToListAsync(cancellationToken))
                .ToHashSet();

            var reminderCount = 0;
            foreach (var item in upcoming.Where(item => !remindedToday.Contains(item.fee_item_id)))
            {
                var contract = item.fee_schedule.contract;
                var daysLeft = item.due_date.DayNumber - today.DayNumber;
                Notify(contract.vendor.user_id, FinanceNotificationTypes.Fee,
                    "Sắp đến hạn đóng phí",
                    daysLeft <= 0
                        ? $"Khoản phí {Vnd(item.amount)} của ô {contract.slot.slot_code} đến hạn hôm nay."
                        : $"Khoản phí {Vnd(item.amount)} của ô {contract.slot.slot_code} sẽ đến hạn trong {daysLeft} ngày "
                            + $"({Day(item.due_date)}).",
                    "FeeScheduleItem", item.fee_item_id, now);
                reminderCount++;
            }

            // Phase A: remind a vendor about an UNPAID penalty once it has sat past the grace
            // period their ward configured (WardCompliancePolicies.unpaid_penalty_grace_days).
            // Wards that have not configured a grace period are skipped entirely -- this sweep
            // never invents a deadline a ward did not opt into.
            var penaltyReminderCount = 0;
            var gracePeriods = await db.WardCompliancePolicies.AsNoTracking()
                .Where(p => p.unpaid_penalty_grace_days != null)
                .Select(p => new { p.ward_unit_id, GraceDays = p.unpaid_penalty_grace_days!.Value })
                .ToListAsync(cancellationToken);
            if (gracePeriods.Count > 0)
            {
                var unpaid = await db.Penalties
                    .Include(p => p.penalty_schedule)
                    .Include(p => p.violation)
                    .Where(p => p.penalty_status == PenaltyStatuses.Unpaid)
                    .ToListAsync(cancellationToken);

                var remindedPenaltyIdsToday = (await db.Notifications.AsNoTracking()
                    .Where(n => n.related_entity_type == "Penalty"
                        && n.notification_type == FinanceNotificationTypes.PenaltyOverdue
                        && n.sent_at >= todayStart
                        && n.related_entity_id != null)
                    .Select(n => n.related_entity_id!.Value)
                    .ToListAsync(cancellationToken))
                    .ToHashSet();

                foreach (var penalty in unpaid)
                {
                    if (remindedPenaltyIdsToday.Contains(penalty.penalty_id)
                        || penalty.violation.vendor_id is not { } vendorId)
                    {
                        continue;
                    }
                    var grace = gracePeriods.FirstOrDefault(g => g.ward_unit_id == penalty.penalty_schedule.ward_unit_id);
                    if (grace is null)
                    {
                        continue;
                    }
                    var overdueSince = BusinessCalendar.Today(penalty.created_at);
                    var daysSince = today.DayNumber - overdueSince.DayNumber;
                    if (daysSince < grace.GraceDays)
                    {
                        continue;
                    }

                    var vendorUserId = await db.Vendors.AsNoTracking()
                        .Where(v => v.vendor_id == vendorId).Select(v => v.user_id).FirstOrDefaultAsync(cancellationToken);
                    if (vendorUserId <= 0)
                    {
                        continue;
                    }

                    Notify(vendorUserId, FinanceNotificationTypes.PenaltyOverdue,
                        "Khoản phạt vi phạm chưa nộp",
                        $"Khoản phạt {Vnd(penalty.amount)} (biên bản {penalty.violation.bien_ban_so ?? penalty.violation_id.ToString()}) "
                            + $"đã quá {daysSince} ngày chưa nộp. Vui lòng thanh toán để tránh ảnh hưởng tới việc xem xét gia hạn/duy trì giấy phép.",
                        "Penalty", penalty.penalty_id, now);
                    penaltyReminderCount++;
                }
            }

            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return new FeeReminderSweepResult(overdue.Count, reminderCount, penaltyReminderCount);
        });
    }

    // ------------------------------------------------------------------
    // FinanceHome (summary, fee list, penalty list) and FEE-05 (payments, violations)
    // ------------------------------------------------------------------

    public async Task<FinanceSummaryRow> GetSummaryAsync(long vendorId, CancellationToken cancellationToken)
    {
        var feeDue = await CurrentFeeItems(vendorId)
            .Where(item => item.item_status == FeeItemStatuses.Pending || item.item_status == FeeItemStatuses.Overdue)
            .SumAsync(item => (decimal?)item.amount, cancellationToken) ?? 0m;

        var penaltyDue = await VendorPenalties(vendorId)
            .Where(penalty => penalty.penalty_status == PenaltyStatuses.Unpaid)
            .SumAsync(penalty => (decimal?)penalty.amount, cancellationToken) ?? 0m;

        var overdueCount = await CurrentFeeItems(vendorId)
            .CountAsync(item => item.item_status == FeeItemStatuses.Overdue, cancellationToken);

        var nextDueDate = await CurrentFeeItems(vendorId)
            .Where(item => item.item_status == FeeItemStatuses.Pending)
            .OrderBy(item => item.due_date)
            .Select(item => (DateOnly?)item.due_date)
            .FirstOrDefaultAsync(cancellationToken);

        return new FinanceSummaryRow(feeDue, penaltyDue, overdueCount, nextDueDate);
    }

    public async Task<IReadOnlyList<FeeItemListRow>> ListFeeItemsAsync(
        long vendorId, string? status, CancellationToken cancellationToken)
    {
        // The status filter is applied after Ordinal/OfCount are computed (not pushed into the
        // SQL query): filtering the schedule's items first would recompute each item's position
        // relative to the filtered subset instead of its actual schedule, e.g. "Kỳ 2/2" for an
        // item that is really the 3rd of 3 instalments once the other two are excluded.
        var raw = await CurrentFeeItems(vendorId)
            .Select(item => new
            {
                item.fee_item_id,
                item.fee_schedule_id,
                item.due_date,
                item.amount,
                item.item_status,
                item.paid_at,
                item.fee_schedule.contract_id,
                SlotCode = item.fee_schedule.contract.slot.slot_code,
            })
            .ToListAsync(cancellationToken);

        // Ordinal/OfCount are the item's position within its own schedule, computed in memory —
        // the same reason ReadScheduleAsync does it there instead of as a correlated subquery.
        var rows = raw.GroupBy(row => row.fee_schedule_id)
            .SelectMany(group =>
            {
                var ordered = group.OrderBy(row => row.due_date).ToList();
                return ordered.Select((row, index) => new FeeItemListRow(
                    row.fee_item_id, row.contract_id, row.SlotCode,
                    index + 1, ordered.Count, row.due_date, row.amount, row.item_status, row.paid_at));
            });

        if (status is not null)
        {
            rows = rows.Where(row => row.ItemStatus == status);
        }

        return rows.OrderByDescending(row => row.DueDate).ToList();
    }

    public async Task<IReadOnlyList<PenaltyListRow>> ListPenaltiesAsync(
        long vendorId, string? status, CancellationToken cancellationToken)
    {
        var query = VendorPenalties(vendorId);
        if (status is not null)
        {
            query = query.Where(penalty => penalty.penalty_status == status);
        }

        return await query.OrderByDescending(penalty => penalty.created_at)
            .Select(penalty => new PenaltyListRow(
                penalty.penalty_id,
                penalty.violation_id,
                penalty.violation.violation_type,
                penalty.violation.violation_typeNavigation.description,
                penalty.violation.slot != null ? penalty.violation.slot.slot_code : null,
                penalty.amount,
                penalty.penalty_status,
                penalty.created_at,
                penalty.paid_at))
            .ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<PaymentTransactionRow>> ListPaymentTransactionsAsync(
        long vendorId, CancellationToken cancellationToken)
    {
        // Two simple, separately-executed queries rather than one combined by purpose: the same
        // lesson from the FEE-03 invoice list — a single projection spanning both the fee and
        // penalty navigation chains is exactly the shape EF Core previously failed to translate.
        // The period label is formatted in memory (a hand-formatted string does not translate);
        // its ordinal uses the same correlated count as the FEE-01 checkout read.
        var feeTransactions = (await db.PaymentTransactions.AsNoTracking()
            .Where(t => t.payment_purpose == PaymentPurposes.RentalFee
                && t.fee_item != null && t.fee_item.fee_schedule.contract.vendor_id == vendorId)
            .Select(t => new
            {
                t.transaction_id,
                t.payment_purpose,
                t.provider,
                t.amount,
                t.transaction_status,
                t.fee_item!.due_date,
                Ordinal = t.fee_item.fee_schedule.FeeScheduleItems.Count(sibling => sibling.due_date < t.fee_item.due_date) + 1,
                OfCount = t.fee_item.fee_schedule.FeeScheduleItems.Count(),
                t.fee_item.fee_schedule.contract.slot.slot_code,
                t.created_at,
                t.callback_received_at,
            })
            .ToListAsync(cancellationToken))
            .Select(t => new PaymentTransactionRow(
                t.transaction_id, t.payment_purpose, t.provider, t.amount, t.transaction_status,
                FinanceMapper.PeriodLabel(t.Ordinal, t.OfCount, t.due_date), t.slot_code,
                t.created_at, t.callback_received_at));

        var penaltyTransactions = await db.PaymentTransactions.AsNoTracking()
            .Where(t => t.payment_purpose == PaymentPurposes.Penalty
                && t.penalty != null && t.penalty.violation.vendor_id == vendorId)
            .Select(t => new PaymentTransactionRow(
                t.transaction_id, t.payment_purpose, t.provider, t.amount, t.transaction_status,
                t.penalty!.violation.violation_typeNavigation.description,
                t.penalty.violation.slot != null ? t.penalty.violation.slot.slot_code : null,
                t.created_at, t.callback_received_at))
            .ToListAsync(cancellationToken);

        return feeTransactions.Concat(penaltyTransactions)
            .OrderByDescending(row => row.CreatedAt)
            .ThenByDescending(row => row.TransactionId)
            .ToList();
    }

    public async Task<IReadOnlyList<VendorViolationRow>> ListVendorViolationsAsync(
        long vendorId, CancellationToken cancellationToken) =>
        await db.Violations.AsNoTracking()
            .Where(violation => violation.vendor_id == vendorId)
            .OrderByDescending(violation => violation.recorded_at)
            .Select(violation => new VendorViolationRow(
                violation.violation_id,
                violation.violation_type,
                violation.violation_typeNavigation.description,
                violation.description,
                violation.evidence_url,
                violation.source,
                violation.recorded_at,
                violation.slot != null ? violation.slot.slot_code : null,
                violation.Penalty != null ? violation.Penalty.amount : (decimal?)null,
                violation.Penalty != null ? violation.Penalty.penalty_status : null))
            .ToListAsync(cancellationToken);

    /// <summary>The vendor's fee instalments on each contract's current (non-superseded) schedule.</summary>
    private IQueryable<ScaffoldedModels.FeeScheduleItem> CurrentFeeItems(long vendorId) =>
        db.FeeScheduleItems.AsNoTracking()
            .Where(item => item.fee_schedule.contract.vendor_id == vendorId && item.fee_schedule.superseded_at == null);

    public async Task<long> RecordOfflinePaymentAsync(
        int wardId,
        long actorUserId,
        long? feeItemId,
        long? penaltyId,
        string method,
        string? reference,
        string? note,
        CancellationToken cancellationToken)
    {
        if ((feeItemId is null) == (penaltyId is null))
        {
            throw new ArgumentException("Exactly one of feeItemId and penaltyId must be given.");
        }

        var strategy = db.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            db.ChangeTracker.Clear();
            await using var transaction = await db.Database.BeginTransactionAsync(
                IsolationLevel.Serializable, cancellationToken);
            var now = Now;

            PaymentTransaction payment;
            if (feeItemId is { } itemId)
            {
                var item = await db.FeeScheduleItems
                    .Include(i => i.fee_schedule).ThenInclude(s => s.contract).ThenInclude(c => c.slot).ThenInclude(s => s.zone)
                    .SingleOrDefaultAsync(i => i.fee_item_id == itemId, cancellationToken);
                if (item is null || item.fee_schedule.contract.slot.zone.ward_unit_id != wardId)
                {
                    throw new NotFoundException(FinanceMessages.FeeItemNotFound);
                }

                if (!FeeItemStatuses.IsOutstanding(item.item_status) || item.fee_schedule.superseded_at is not null)
                {
                    throw new DomainRuleException(FinanceMessages.FeeItemNoLongerPayable);
                }

                payment = new PaymentTransaction
                {
                    payment_purpose = PaymentPurposes.RentalFee,
                    fee_item_id = itemId,
                    amount = item.amount,
                    fee_item = item,
                };
            }
            else
            {
                var id = penaltyId!.Value;
                var penalty = await db.Penalties
                    .Include(p => p.violation).ThenInclude(v => v.slot).ThenInclude(s => s!.zone)
                    .Include(p => p.violation).ThenInclude(v => v.contract).ThenInclude(c => c!.slot).ThenInclude(s => s.zone)
                    .Include(p => p.violation).ThenInclude(v => v.UserAccount)
                    .SingleOrDefaultAsync(p => p.penalty_id == id, cancellationToken);
                var violation = penalty?.violation;
                var penaltyWard = violation?.slot?.zone.ward_unit_id
                    ?? violation?.contract?.slot.zone.ward_unit_id
                    ?? violation?.UserAccount?.ward_unit_id;
                if (penalty is null || penaltyWard != wardId)
                {
                    throw new NotFoundException(FinanceMessages.PenaltyNotFound);
                }

                if (!PenaltyStatuses.IsPayable(penalty.penalty_status))
                {
                    throw new DomainRuleException(FinanceMessages.PenaltyNoLongerPayable);
                }

                payment = new PaymentTransaction
                {
                    payment_purpose = PaymentPurposes.Penalty,
                    penalty_id = id,
                    amount = penalty.amount,
                    penalty = penalty,
                };
            }

            // A checkout the vendor opened on this target must not be completed later on top of
            // the payment the officer is recording now.
            var openAttempts = await db.PaymentTransactions
                .Where(t => t.transaction_status == PaymentStatuses.Pending
                    && ((feeItemId != null && t.fee_item_id == feeItemId)
                        || (penaltyId != null && t.penalty_id == penaltyId)))
                .ToListAsync(cancellationToken);
            foreach (var attempt in openAttempts)
            {
                attempt.transaction_status = PaymentStatuses.Failed;
                attempt.callback_received_at = now;
            }

            payment.idempotency_key = $"OFFLINE-{Guid.NewGuid():N}";
            payment.provider = method;
            payment.provider_reference = reference;
            payment.transaction_status = PaymentStatuses.Success;
            payment.callback_received_at = now;
            payment.created_at = now;
            db.PaymentTransactions.Add(payment);

            await ApplySuccessAsync(payment, cancellationToken);

            db.AuditLogs.Add(new AuditLog
            {
                actor_user_id = actorUserId,
                action = "OFFLINE_PAYMENT_RECORDED",
                entity_type = feeItemId is not null ? "FeeScheduleItem" : "Penalty",
                entity_id = feeItemId ?? penaltyId!.Value,
                details = JsonSerializer.Serialize(new { method, reference, note, amount = payment.amount }),
                created_at = now,
            });

            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return payment.transaction_id;
        });
    }

    /// <summary>Every penalty raised against a violation identified to this vendor.</summary>
    private IQueryable<ScaffoldedModels.Penalty> VendorPenalties(long vendorId) =>
        db.Penalties.AsNoTracking().Where(penalty => penalty.violation.vendor_id == vendorId);
}
