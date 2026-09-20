using FluentValidation;
using MediatR;
using StreetBiz.Application.Common.Exceptions;
using StreetBiz.Application.Common.Interfaces;
using StreetBiz.Application.Common.Security;
using StreetBiz.Application.DTOs.SlotTransfers;

namespace StreetBiz.Application.Features.SlotTransfers.RequestTransfer;

/// <summary>
/// SIDE-12: the current holder of an active contract requests to transfer it to another vendor,
/// identified by phone number since vendors don't know each other's vendor_id. Does not change
/// RentalContracts.vendor_id -- only WARD-18's approval does that.
/// </summary>
public sealed record RequestTransferCommand(long ContractId, string ToVendorPhone) : IRequest<SlotTransferRequestDto>;

public sealed class RequestTransferCommandValidator : AbstractValidator<RequestTransferCommand>
{
    public RequestTransferCommandValidator()
    {
        RuleFor(x => x.ContractId).GreaterThan(0);
        RuleFor(x => x.ToVendorPhone).NotEmpty().MaximumLength(20);
    }
}

public sealed class RequestTransferCommandHandler(
    IVendorContext vendorContext,
    IVendorRepository vendors,
    IBusinessRegistrationRepository registrations,
    IRentalContractRepository contracts,
    ISlotTransferRequestRepository transfers)
    : IRequestHandler<RequestTransferCommand, SlotTransferRequestDto>
{
    public async Task<SlotTransferRequestDto> Handle(RequestTransferCommand request, CancellationToken cancellationToken)
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

        var toVendorId = await vendors.GetVendorIdByPhoneAsync(request.ToVendorPhone, cancellationToken)
            ?? throw new NotFoundException(SideMessages.ReceiverNotFound);

        // Don't rely on CK_SlotTransferRequests_DifferentVendors -- surface this as a clean 400.
        if (toVendorId == vendorId)
        {
            throw new ValidationAppException(new Dictionary<string, string[]>
            {
                ["ToVendorPhone"] = [SideMessages.TransferSameVendor],
            });
        }

        // BR-26: the receiver must hold an approved business registration.
        if (!await registrations.HasApprovedRegistrationAsync(toVendorId, cancellationToken))
        {
            throw new DomainRuleException(SideMessages.ReceiverNotApproved);
        }

        // BR-27: the sender can't hand off a slot while owing fees or penalties.
        if (await contracts.HasOutstandingDebtAsync(request.ContractId, cancellationToken))
        {
            throw new DomainRuleException(SideMessages.TransferBlockedByDebt);
        }

        if (await transfers.HasOpenForContractAsync(request.ContractId, cancellationToken))
        {
            throw new ConflictException(SideMessages.TransferAlreadyOpen);
        }

        var transferId = await transfers.CreateAsync(request.ContractId, vendorId, toVendorId, cancellationToken);

        var created = await transfers.GetByIdAsync(transferId, cancellationToken)
            ?? throw new NotFoundException(SideMessages.TransferNotFound);

        return created.ToDto();
    }
}
