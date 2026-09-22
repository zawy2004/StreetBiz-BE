using FluentValidation;
using MediatR;
using StreetBiz.Application.Common.Exceptions;
using StreetBiz.Application.Common.Interfaces;
using StreetBiz.Application.Common.Security;
using StreetBiz.Application.DTOs.RentalContracts;

namespace StreetBiz.Application.Features.RentalContracts.RequestRenewal;

/// <summary>SIDE-06: request a renewal of an active rental contract.</summary>
public sealed record RequestRenewalCommand(long ContractId, int RequestedTermDays) : IRequest<RenewalRequestDto>;

public sealed class RequestRenewalCommandValidator : AbstractValidator<RequestRenewalCommand>
{
    public RequestRenewalCommandValidator()
    {
        // Same bound as the original application's term (GetSlotQuoteQuery: 1-365 days).
        RuleFor(x => x.RequestedTermDays).InclusiveBetween(1, RenewalStatuses.MaxRequestedTermDays);
    }
}

public sealed class RequestRenewalCommandHandler(
    IVendorContext vendorContext,
    IRentalContractRepository contracts,
    IRenewalRequestRepository renewals)
    : IRequestHandler<RequestRenewalCommand, RenewalRequestDto>
{
    public async Task<RenewalRequestDto> Handle(RequestRenewalCommand request, CancellationToken cancellationToken)
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

        // Pre-check; UQ_RenewalRequests_OpenPerContract is the race-condition safety net.
        if (await renewals.HasOpenAsync(request.ContractId, cancellationToken))
        {
            throw new ConflictException(SideMessages.RenewalAlreadyOpen);
        }

        var renewalId = await renewals.CreateAsync(request.ContractId, request.RequestedTermDays, cancellationToken);

        var created = await renewals.GetByIdAsync(renewalId, cancellationToken)
            ?? throw new NotFoundException(SideMessages.ContractNotFound);

        return created.ToDto();
    }
}
