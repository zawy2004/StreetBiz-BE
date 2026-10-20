using FluentValidation;
using MediatR;
using StreetBiz.Application.Common.Exceptions;
using StreetBiz.Application.Common.Interfaces;
using StreetBiz.Application.Common.Security;

namespace StreetBiz.Application.Features.RentalContracts.CancelContract;

/// <summary>SIDE-07: the vendor voluntarily returns their slot. No ward approval is involved.</summary>
public sealed record CancelContractCommand(long ContractId, string? Reason) : IRequest<Unit>;

public sealed class CancelContractCommandValidator : AbstractValidator<CancelContractCommand>
{
    public CancelContractCommandValidator()
    {
        RuleFor(x => x.Reason).MaximumLength(500);
    }
}

public sealed class CancelContractCommandHandler(
    IVendorContext vendorContext,
    ICurrentUser currentUser,
    IRentalContractRepository contracts,
    IRenewalRequestRepository renewals)
    : IRequestHandler<CancelContractCommand, Unit>
{
    public async Task<Unit> Handle(CancelContractCommand request, CancellationToken cancellationToken)
    {
        var vendorId = await vendorContext.RequireVendorIdAsync(cancellationToken);

        var contract = await contracts.GetByIdAsync(request.ContractId, cancellationToken)
            ?? throw new NotFoundException(SideMessages.ContractNotFound);

        if (contract.VendorId != vendorId)
        {
            throw new ForbiddenException(AppMessages.Forbidden);
        }

        if (contract.ContractStatus != ContractStatuses.Active)
        {
            throw new DomainRuleException(SideMessages.ContractNotActive);
        }

        // BR: returning a slot must not be an exit from money already owed (mirrors
        // TR_RentalContracts_NoCancelWithDebt, which is the safety net for a concurrent charge).
        if (await contracts.HasOutstandingDebtAsync(request.ContractId, cancellationToken))
        {
            throw new DomainRuleException(SideMessages.CannotReturnWithDebt);
        }

        // RequireVendorIdAsync above already required an authenticated session, so UserId is set.
        var userId = currentUser.UserId!.Value;

        await contracts.CancelAsync(request.ContractId, userId, request.Reason, cancellationToken);

        // A renewal request only makes sense against a contract that still exists to extend --
        // withdraw any of the vendor's own still-open renewal requests so it doesn't sit stuck
        // in the ward's queue (it would be safely blocked at decision time anyway, since
        // DecideRenewalAsync requires the contract to still be ACTIVE, but nobody would ever
        // clean it up otherwise).
        await renewals.CloseOpenForContractAsync(request.ContractId, cancellationToken);

        return Unit.Value;
    }
}
