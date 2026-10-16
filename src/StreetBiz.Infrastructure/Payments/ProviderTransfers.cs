using Microsoft.EntityFrameworkCore;
using StreetBiz.Application.Common.Models;
using StreetBiz.Application.Common.Security;
using StreetBiz.Infrastructure.Persistence;

namespace StreetBiz.Infrastructure.Payments;

/// <summary>
/// Telling a repeated report from new money. MoMo reports one checkout, so a second success for
/// a settled transaction is the same report again. A bank transfer is different: the payer can
/// send the same payment code twice, or pay after the order was cancelled, and each of those is
/// real money. SePay gives every transfer its own id (<see cref="PaymentCallbackData.ProviderEventId"/>),
/// kept in the stored payload of the callback that settled the transaction.
/// </summary>
internal static class ProviderTransfers
{
    /// <summary>
    /// True when this success is a transfer that has not been applied to the transaction before:
    /// money that arrived for something already settled or closed. False for reports without a
    /// transfer id (MoMo, ZaloPay) and for the same transfer reported again.
    /// </summary>
    public static async Task<bool> IsNewTransferAsync(
        this StreetBizDbContext db,
        long transactionId,
        PaymentCallbackData callback,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(callback.ProviderEventId)
            || !string.Equals(callback.Status?.Trim(), PaymentStatuses.Success, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var applied = await db.PaymentCallbackEvents.AsNoTracking()
            .Where(row => row.transaction_id == transactionId
                && row.provider == callback.Provider
                && row.processing_result == CallbackResults.Applied)
            .Select(row => row.raw_payload)
            .ToListAsync(cancellationToken);
        return !applied.Any(raw => SePayGateway.EventIdIn(raw) == callback.ProviderEventId);
    }
}
