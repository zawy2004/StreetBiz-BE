using StreetBiz.Application.Common.Exceptions;
using StreetBiz.Application.Common.Interfaces;
using StreetBiz.Application.Common.Security;

namespace StreetBiz.Application.Features.PayoutAccounts;

/// <summary>
/// A payee's channel refused its keys: the account goes to ERROR (its owner is told by the
/// repository) and the team gets one payment alert. Other failures are left to the caller.
/// </summary>
public sealed class PayeeChannelFailures(
    IPayeeAccountRepository payees,
    TimeProvider clock,
    IPaymentAlertNotifier? alerts = null)
{
    public async Task ReportAsync(
        long payeeAccountId, PayeeChannelException exception, long? transactionId, CancellationToken cancellationToken)
    {
        if (!exception.Unauthorized)
        {
            return;
        }

        var marked = await payees.MarkErrorAsync(
            payeeAccountId, exception.Message, clock.GetUtcNow().UtcDateTime, cancellationToken);
        if (marked && alerts is not null)
        {
            await alerts.NotifyAsync(
                new PaymentAlert(
                    PaymentProviders.PayOs,
                    "CHANNEL_ERROR",
                    $"payOS từ chối khoá của tài khoản nhận tiền #{payeeAccountId}. Tài khoản đã chuyển sang trạng thái lỗi " +
                    "và chủ tài khoản đã được báo kết nối lại; trong lúc đó không mở được thanh toán cho người nhận này.",
                    null,
                    null,
                    null,
                    transactionId,
                    null),
                cancellationToken);
        }
    }
}

/// <summary>
/// Opens the payOS payment link of a PENDING transaction on its payee's own channel: the payee is
/// recorded on the transaction first (only that payee's webhook can settle it), a link that is still
/// payable is shown again instead of opening a second one, and refused keys are reported.
/// </summary>
public sealed class PayeeCheckouts(
    IPayeeAccountRepository payees,
    IPaymentTransactionRepository transactions,
    IPaymentGateway gateway,
    PayeeChannelFailures failures)
{
    public async Task<PaymentGatewayCheckoutResult> OpenAsync(
        PayeeSummary payee, PaymentGatewayCheckoutRequest request, CancellationToken cancellationToken)
    {
        await transactions.AttachPayeeAsync(request.TransactionId, payee.PayeeAccountId, cancellationToken);
        var state = await transactions.GetAsync(request.TransactionId, cancellationToken)
            ?? throw new NotFoundException("Không tìm thấy giao dịch thanh toán.");
        if (state.PayeeAccountId != payee.PayeeAccountId)
        {
            throw new ConflictException("Khoản thanh toán này đã gắn với một tài khoản nhận tiền khác. Hãy tạo khoản mới.");
        }

        if (state.CheckoutPayload is { } payload
            && state.ProviderReference is { } reference
            && gateway.DescribeStoredCheckout(PaymentProviders.PayOs, payload, state.Amount) is { } stillPayable)
        {
            return new PaymentGatewayCheckoutResult(stillPayable.CheckoutUrl ?? "", reference, stillPayable)
            {
                CheckoutPayload = payload,
            };
        }

        var credentials = await payees.GetCredentialsAsync(payee.PayeeAccountId, cancellationToken)
            ?? throw new NotFoundException("Không tìm thấy tài khoản nhận tiền.");
        try
        {
            var result = await gateway.CreateCheckoutAsync(
                request with { Payee = credentials, PreviousCheckoutPayload = state.CheckoutPayload },
                cancellationToken);
            await transactions.SaveCheckoutAsync(
                request.TransactionId,
                result.ProviderReference ?? throw new InvalidOperationException("payOS returned no order code."),
                result.CheckoutPayload,
                cancellationToken);
            return result;
        }
        catch (PayeeChannelException exception)
        {
            await failures.ReportAsync(payee.PayeeAccountId, exception, request.TransactionId, CancellationToken.None);
            throw;
        }
    }

    /// <summary>
    /// The state of a payment to a payee, asked with that payee's keys. Null when the provider
    /// cannot be asked; refused keys are reported and read as "not final".
    /// </summary>
    public async Task<PaymentGatewayCallback?> QueryAsync(
        PaymentCheckoutState payment, CancellationToken cancellationToken)
    {
        if (payment.PayeeAccountId is not { } payeeAccountId || payment.ProviderReference is not { } reference)
        {
            return null;
        }

        var credentials = await payees.GetCredentialsAsync(payeeAccountId, cancellationToken);
        if (credentials is null)
        {
            return null;
        }

        try
        {
            return await gateway.QueryPaymentAsync(payment.Provider, reference, credentials, cancellationToken);
        }
        catch (PayeeChannelException exception)
        {
            await failures.ReportAsync(payeeAccountId, exception, payment.TransactionId, CancellationToken.None);
            return new PaymentGatewayCallback(reference, null, null, null, false);
        }
    }
}
