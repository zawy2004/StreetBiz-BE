using FluentValidation;
using MediatR;
using StreetBiz.Application.Common.Exceptions;
using StreetBiz.Application.Common.Interfaces;
using StreetBiz.Application.Common.Security;

namespace StreetBiz.Application.Features.PayoutAccounts;

/// <summary>
/// Who a rental fee would be paid to, for the payment screen: the ward of the instalment's slot and
/// its receiving account (masked, no keys). Null when the ward has not connected one, so the screen
/// offers only the other methods. The instalment must be the caller's own.
/// </summary>
public sealed record GetFeePayeeQuery(long FeeItemId) : IRequest<PayeeSummary?>;

public sealed class GetFeePayeeQueryValidator : AbstractValidator<GetFeePayeeQuery>
{
    public GetFeePayeeQueryValidator() => RuleFor(x => x.FeeItemId).GreaterThan(0);
}

public sealed class GetFeePayeeQueryHandler(
    IVendorContext vendorContext,
    IFinanceRepository finance,
    IPayeeAccountRepository payees) : IRequestHandler<GetFeePayeeQuery, PayeeSummary?>
{
    public async Task<PayeeSummary?> Handle(GetFeePayeeQuery request, CancellationToken cancellationToken)
    {
        var vendorId = await vendorContext.RequireVendorIdAsync(cancellationToken);
        _ = await finance.GetFeeItemForCheckoutAsync(vendorId, request.FeeItemId, cancellationToken)
            ?? throw new NotFoundException(FinanceMessages.FeeItemNotFound);
        return await payees.GetForFeeItemAsync(request.FeeItemId, cancellationToken);
    }
}

/// <summary>
/// Who an order from this storefront would be paid to: the household's receiving account (masked,
/// no keys), or null when it has not connected one and takes no online orders.
/// </summary>
public sealed record GetStorefrontPayeeQuery(long StorefrontId) : IRequest<PayeeSummary?>;

public sealed class GetStorefrontPayeeQueryValidator : AbstractValidator<GetStorefrontPayeeQuery>
{
    public GetStorefrontPayeeQueryValidator() => RuleFor(x => x.StorefrontId).GreaterThan(0);
}

public sealed class GetStorefrontPayeeQueryHandler(
    ICustomerContext customerContext,
    IPayeeAccountRepository payees) : IRequestHandler<GetStorefrontPayeeQuery, PayeeSummary?>
{
    public async Task<PayeeSummary?> Handle(GetStorefrontPayeeQuery request, CancellationToken cancellationToken)
    {
        _ = await customerContext.RequireCustomerUserIdAsync(cancellationToken);
        return await payees.GetForStorefrontAsync(request.StorefrontId, cancellationToken);
    }
}
