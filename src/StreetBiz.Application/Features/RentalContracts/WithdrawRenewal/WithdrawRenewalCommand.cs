using MediatR;
using StreetBiz.Application.Common.Exceptions;
using StreetBiz.Application.Common.Interfaces;
using StreetBiz.Application.Common.Security;

namespace StreetBiz.Application.Features.RentalContracts.WithdrawRenewal;

/// <summary>SIDE-06: vendor withdraws their own open renewal request, e.g. after submitting the
/// wrong term. Mirrors WithdrawApplicationCommand (SIDE-04) for RentalApplications -- renewals
/// had the same gap: no way back out short of asking the ward to reject it.</summary>
public sealed record WithdrawRenewalCommand(long RenewalId) : IRequest<Unit>;

public sealed class WithdrawRenewalCommandHandler(
    IVendorContext vendorContext,
    IRentalContractRepository contracts,
    IRenewalRequestRepository renewals)
    : IRequestHandler<WithdrawRenewalCommand, Unit>
{
    public async Task<Unit> Handle(WithdrawRenewalCommand request, CancellationToken cancellationToken)
    {
        var vendorId = await vendorContext.RequireVendorIdAsync(cancellationToken);

        var renewal = await renewals.GetByIdAsync(request.RenewalId, cancellationToken)
            ?? throw new NotFoundException(SideMessages.RenewalNotFound);

        var contract = await contracts.GetByIdAsync(renewal.ContractId, cancellationToken)
            ?? throw new NotFoundException(SideMessages.ContractNotFound);

        if (contract.VendorId != vendorId)
        {
            throw new ForbiddenException(AppMessages.Forbidden);
        }

        var withdrawn = await renewals.WithdrawAsync(request.RenewalId, cancellationToken);
        if (!withdrawn)
        {
            throw new ConflictException(SideMessages.RenewalNotWithdrawable);
        }

        return Unit.Value;
    }
}
