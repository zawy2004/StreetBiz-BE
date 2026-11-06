using Microsoft.EntityFrameworkCore;
using StreetBiz.Application.Common.Models;
using StreetBiz.Application.Common.Security;
using StreetBiz.Infrastructure.Persistence;

namespace StreetBiz.Infrastructure.Payments;

/// <summary>
/// Telling a repeated report from new money, and who may report it. MoMo reports one checkout, so a second success for
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
        return !applied.Any(raw => EventIdIn(callback.Provider, raw) == callback.ProviderEventId);
    }

    /// <summary>
    /// A payment to a payee (PAYOS) is settled only by that payee's own channel; a payment to no
    /// payee is settled only by a report that names none.
    /// </summary>
    public static bool FromItsPayee(long? paymentPayeeAccountId, PaymentCallbackData callback) =>
        paymentPayeeAccountId == callback.PayeeAccountId;

    /// <summary>
    /// The provider's id for the transfer inside a stored payload: SePay's "id"; for payOS the bank
    /// reference, in a webhook's "data.reference" or a status answer's "transactions[].reference".
    /// </summary>
    public static string? EventIdIn(string provider, string? rawPayload)
    {
        if (provider != PaymentProviders.PayOs)
        {
            return SePayGateway.EventIdIn(rawPayload);
        }

        if (string.IsNullOrWhiteSpace(rawPayload))
        {
            return null;
        }

        try
        {
            using var json = System.Text.Json.JsonDocument.Parse(rawPayload);
            var root = json.RootElement;
            if (root.ValueKind != System.Text.Json.JsonValueKind.Object)
            {
                return null;
            }

            if (root.TryGetProperty("data", out var data)
                && data.ValueKind == System.Text.Json.JsonValueKind.Object
                && data.TryGetProperty("reference", out var reference)
                && reference.ValueKind == System.Text.Json.JsonValueKind.String)
            {
                return reference.GetString();
            }

            if (root.TryGetProperty("transactions", out var transactions)
                && transactions.ValueKind == System.Text.Json.JsonValueKind.Array)
            {
                foreach (var transfer in transactions.EnumerateArray())
                {
                    if (transfer.ValueKind == System.Text.Json.JsonValueKind.Object
                        && transfer.TryGetProperty("reference", out var item)
                        && item.ValueKind == System.Text.Json.JsonValueKind.String)
                    {
                        return item.GetString();
                    }
                }
            }
        }
        catch (System.Text.Json.JsonException)
        {
        }

        return null;
    }
}
