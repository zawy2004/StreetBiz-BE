using FluentValidation;
using MediatR;
using StreetBiz.Application.Common.Exceptions;
using StreetBiz.Application.Common.Interfaces;
using StreetBiz.Application.Common.Models;
using StreetBiz.Application.Common.Security;
using StreetBiz.Application.Features.PayoutAccounts;

namespace StreetBiz.Application.Features.Finance.SyncPayment;

/// <summary>
/// FEE-01/FEE-04: the vendor is back from MoMo. Ask MoMo itself for the transaction state
/// and apply it through the same path as a verified callback (the return URL's query string
/// is never trusted). Settles fee/penalty payments when MoMo's IPN cannot reach the server.
/// </summary>
public sealed record SyncFinancePaymentCommand(long TransactionId) : IRequest<FinancePaymentSyncDto>;

/// <summary>Status is PENDING, SUCCESS or FAILED after the sync.</summary>
public sealed record FinancePaymentSyncDto(long TransactionId, string Status);

public sealed class SyncFinancePaymentCommandValidator : AbstractValidator<SyncFinancePaymentCommand>
{
    public SyncFinancePaymentCommandValidator() => RuleFor(x => x.TransactionId).GreaterThan(0);
}

public sealed class SyncFinancePaymentCommandHandler(
    IVendorContext vendorContext,
    IFinanceRepository finance,
    IPaymentGateway paymentGateway,
    IPaymentTransactionRepository? transactions = null,
    PayeeCheckouts? payeeCheckouts = null)
    : IRequestHandler<SyncFinancePaymentCommand, FinancePaymentSyncDto>
{
    public async Task<FinancePaymentSyncDto> Handle(
        SyncFinancePaymentCommand request,
        CancellationToken cancellationToken)
    {
        var vendorId = await vendorContext.RequireVendorIdAsync(cancellationToken);
        var payment = await finance.GetVendorPaymentAsync(vendorId, request.TransactionId, cancellationToken)
            ?? throw new NotFoundException(FinanceMessages.TransactionNotFound);
        if (payment.Status != PaymentStatuses.Pending || string.IsNullOrWhiteSpace(payment.ProviderReference))
        {
            return new FinancePaymentSyncDto(payment.TransactionId, payment.Status);
        }

        // PAYOS: asked on the ward's own channel, with its own keys.
        var stored = payment.Provider == PaymentProviders.PayOs && transactions is not null
            ? await transactions.GetAsync(payment.TransactionId, cancellationToken)
            : null;
        var state = stored?.PayeeAccountId is not null && payeeCheckouts is not null
            ? await payeeCheckouts.QueryAsync(stored, cancellationToken)
            : await paymentGateway.QueryPaymentAsync(payment.Provider, payment.ProviderReference, cancellationToken);
        if (state?.Status is null)
        {
            return new FinancePaymentSyncDto(payment.TransactionId, payment.Status);
        }

        await finance.ApplyPaymentCallbackAsync(
            new PaymentCallbackData(
                payment.Provider,
                state.ProviderReference ?? payment.ProviderReference,
                payment.IdempotencyKey,
                state.Amount,
                state.Status,
                state.RawPayload ?? "{}",
                state.SignatureValid,
                state.ProviderEventId)
            {
                PayeeAccountId = stored?.PayeeAccountId,
            },
            cancellationToken);
        var after = await finance.GetVendorPaymentAsync(vendorId, request.TransactionId, cancellationToken);
        return new FinancePaymentSyncDto(payment.TransactionId, after?.Status ?? payment.Status);
    }
}
