using FluentValidation;
using MediatR;
using StreetBiz.Application.Common.Exceptions;
using StreetBiz.Application.Common.Interfaces;
using StreetBiz.Application.Common.Models;
using StreetBiz.Application.Common.Security;

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
    IPaymentGateway paymentGateway)
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

        var state = await paymentGateway.QueryPaymentAsync(
            payment.Provider, payment.ProviderReference, cancellationToken);
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
                state.SignatureValid),
            cancellationToken);
        var after = await finance.GetVendorPaymentAsync(vendorId, request.TransactionId, cancellationToken);
        return new FinancePaymentSyncDto(payment.TransactionId, after?.Status ?? payment.Status);
    }
}
