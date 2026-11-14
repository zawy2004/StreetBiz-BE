using Microsoft.EntityFrameworkCore;
using StreetBiz.Application.Common.Interfaces;
using StreetBiz.Application.Common.Security;
using StreetBiz.Infrastructure.Persistence.ScaffoldedModels;

namespace StreetBiz.Infrastructure.Persistence.Repositories;

/// <summary>Payee and stored-link bookkeeping on PaymentTransactions, shared by fees and orders.</summary>
public sealed class PaymentTransactionRepository(StreetBizDbContext db) : IPaymentTransactionRepository
{
    public async Task<PaymentCheckoutState?> GetAsync(long transactionId, CancellationToken cancellationToken) =>
        ToState(await db.PaymentTransactions.AsNoTracking()
            .SingleOrDefaultAsync(t => t.transaction_id == transactionId, cancellationToken));

    public async Task AttachPayeeAsync(long transactionId, long payeeAccountId, CancellationToken cancellationToken)
    {
        // Only while still open, and only one payee ever: a transaction never changes who it pays.
        await db.PaymentTransactions
            .Where(t => t.transaction_id == transactionId
                && t.transaction_status == PaymentStatuses.Pending
                && t.payee_account_id == null)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.payee_account_id, payeeAccountId), cancellationToken);
    }

    public async Task SaveCheckoutAsync(
        long transactionId, string providerReference, string? checkoutPayload, CancellationToken cancellationToken)
    {
        await db.PaymentTransactions
            .Where(t => t.transaction_id == transactionId && t.transaction_status == PaymentStatuses.Pending)
            .ExecuteUpdateAsync(s => s
                .SetProperty(t => t.provider_reference, providerReference)
                .SetProperty(t => t.checkout_payload, checkoutPayload), cancellationToken);
    }

    public async Task<IReadOnlyList<PaymentCheckoutState>> ListStalePayeeOrderPaymentsAsync(
        DateTime createdBefore, CancellationToken cancellationToken)
    {
        var rows = await db.PaymentTransactions.AsNoTracking()
            .Where(t => t.payment_purpose == PaymentPurposes.Order
                && t.transaction_status == PaymentStatuses.Pending
                && t.payee_account_id != null
                && t.provider_reference != null
                && t.created_at < createdBefore)
            .OrderBy(t => t.transaction_id)
            .Take(200)
            .ToListAsync(cancellationToken);
        return [.. rows.Select(row => ToState(row)!)];
    }

    private static PaymentCheckoutState? ToState(PaymentTransaction? row) => row is null ? null : new PaymentCheckoutState(
        row.transaction_id,
        row.payment_purpose,
        row.provider,
        row.provider_reference,
        row.idempotency_key,
        row.amount,
        row.transaction_status,
        row.payee_account_id,
        row.checkout_payload,
        row.order_id,
        row.created_at);
}
