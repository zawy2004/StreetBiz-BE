using System.Data;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using StreetBiz.Application.Common.Exceptions;
using StreetBiz.Application.Common.Interfaces;
using StreetBiz.Application.Common.Models;
using StreetBiz.Application.Common.Security;
using StreetBiz.Application.DTOs.Finance;
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

    // ------------------------------------------------------------------
    // FEE-01 checkout
    // ------------------------------------------------------------------

    public Task<FeeItemCheckoutRow?> GetFeeItemForCheckoutAsync(
        long vendorId, long feeItemId, CancellationToken cancellationToken) =>
        db.FeeScheduleItems.AsNoTracking()
            .Where(item => item.fee_item_id == feeItemId && item.fee_schedule.contract.vendor_id == vendorId)
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
                    row => row.fee_item_id == feeItemId && row.fee_schedule.contract.vendor_id == vendorId,
                    cancellationToken)
                ?? throw new NotFoundException(FinanceMessages.FeeItemNotFound);

            if (!FeeItemStatuses.IsOutstanding(item.item_status))
            {
                // Re-checked here, inside the write transaction, because the vendor's own
                // GetFeeItemForCheckoutAsync read (which showed the "Thanh toán" button) happened
                // outside it and could be stale by the time this runs.
                throw new DomainRuleException(FinanceMessages.FeeItemNoLongerPayable);
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

            if (!PenaltyStatuses.IsPayable(penalty.penalty_status))
            {
                throw new DomainRuleException(FinanceMessages.PenaltyNoLongerPayable);
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

            if (payment.transaction_status is PaymentStatuses.Success or PaymentStatuses.Failed)
            {
                return await Finish(PaymentCallbackOutcome.Duplicate, CallbackResults.Duplicate);
            }

            if (!IsStillPayable(payment))
            {
                // Moved on since this transaction was opened — most likely a different
                // transaction on the same instalment/penalty already succeeded.
                return await Finish(PaymentCallbackOutcome.Rejected, CallbackResults.Rejected);
            }

            payment.provider_reference ??= callback.ProviderReference;
            payment.callback_received_at = Now;
            payment.transaction_status = normalizedStatus;

            if (normalizedStatus == PaymentStatuses.Success)
            {
                await ApplySuccessAsync(payment, cancellationToken);
            }

            return await Finish(PaymentCallbackOutcome.Applied, CallbackResults.Applied);
        });
    }

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

            if (payment.transaction_status is PaymentStatuses.Success or PaymentStatuses.Failed)
            {
                throw new ConflictException(FinanceMessages.TransactionAlreadyProcessed);
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

    /// <summary>True while nobody has already paid the instalment/penalty this transaction targets.</summary>
    private static bool IsStillPayable(PaymentTransaction payment) =>
        payment.fee_item is { } item ? FeeItemStatuses.IsOutstanding(item.item_status)
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
                $"Kỳ phí {periodLabel} đã được ghi nhận. Hoá đơn đã phát hành.",
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

    private async Task<long> VendorUserIdAsync(long vendorId, CancellationToken cancellationToken) =>
        await db.Vendors.AsNoTracking()
            .Where(vendor => vendor.vendor_id == vendorId)
            .Select(vendor => vendor.user_id)
            .FirstAsync(cancellationToken);

    /// <summary>
    /// HD-{year}-{6 digits}, matching the format the demo seed already uses. The UNIQUE constraint
    /// on invoice_number is the real backstop against a race producing the same number twice — a
    /// collision fails this write outright rather than issuing a duplicate invoice.
    /// </summary>
    private async Task<string> NextInvoiceNumberAsync(DateTime issuedAt, CancellationToken cancellationToken)
    {
        var prefix = $"HD-{issuedAt:yyyy}-";
        var existing = await db.Invoices.AsNoTracking()
            .Where(invoice => invoice.invoice_number.StartsWith(prefix))
            .Select(invoice => invoice.invoice_number)
            .ToListAsync(cancellationToken);

        var next = existing
            .Select(number => int.TryParse(number.AsSpan(prefix.Length), out var sequence) ? sequence : 0)
            .DefaultIfEmpty(0)
            .Max() + 1;

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
}
