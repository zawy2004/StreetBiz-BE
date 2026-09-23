using FluentValidation;
using MediatR;
using StreetBiz.Application.Common.Interfaces;
using StreetBiz.Application.Common.Models;
using StreetBiz.Application.Common.Security;

namespace StreetBiz.Application.Features.Finance.ConfirmSandboxPayment;

/// <summary>
/// Development-only stand-in for a real signed MoMo/ZaloPay callback (same role as Commerce's
/// sandbox-confirm): applies success directly to a PENDING transaction this vendor owns, so
/// FEE-01/FEE-04 can be exercised without provider credentials. Gated at the controller by
/// Development + Payments:SandboxEnabled, same as Orders.
/// </summary>
public sealed record ConfirmSandboxFinancePaymentCommand(long TransactionId)
    : IRequest<FinanceCallbackMutationResult>;

public sealed class ConfirmSandboxFinancePaymentCommandValidator
    : AbstractValidator<ConfirmSandboxFinancePaymentCommand>
{
    public ConfirmSandboxFinancePaymentCommandValidator() => RuleFor(x => x.TransactionId).GreaterThan(0);
}

public sealed class ConfirmSandboxFinancePaymentCommandHandler(
    IVendorContext vendorContext,
    IFinanceRepository finance)
    : IRequestHandler<ConfirmSandboxFinancePaymentCommand, FinanceCallbackMutationResult>
{
    public async Task<FinanceCallbackMutationResult> Handle(
        ConfirmSandboxFinancePaymentCommand request,
        CancellationToken cancellationToken)
    {
        var vendorId = await vendorContext.RequireVendorIdAsync(cancellationToken);
        return await finance.ConfirmSandboxSuccessAsync(vendorId, request.TransactionId, cancellationToken);
    }
}
