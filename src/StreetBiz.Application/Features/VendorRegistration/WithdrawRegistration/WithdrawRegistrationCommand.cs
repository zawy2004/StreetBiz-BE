using MediatR;
using StreetBiz.Application.Common.Exceptions;
using StreetBiz.Application.Common.Interfaces;
using StreetBiz.Application.Common.Security;

namespace StreetBiz.Application.Features.VendorRegistration.WithdrawRegistration;

/// <summary>REG-05: withdraw a pending or approved registration.</summary>
public sealed record WithdrawRegistrationCommand(long RegistrationId) : IRequest<Unit>;

public sealed class WithdrawRegistrationCommandHandler(
    IVendorContext vendorContext,
    IBusinessRegistrationRepository repository) : IRequestHandler<WithdrawRegistrationCommand, Unit>
{
    public async Task<Unit> Handle(WithdrawRegistrationCommand request, CancellationToken cancellationToken)
    {
        var registration = await vendorContext.RequireOwnedRegistrationAsync(request.RegistrationId, cancellationToken);

        // Already terminal — nothing to withdraw.
        if (registration.RegistrationStatus is RegistrationStatuses.Withdrawn
            or RegistrationStatuses.Rejected)
        {
            return Unit.Value;
        }

        // BR-16: an APPROVED registration backing an ACTIVE contract cannot be withdrawn.
        if (registration.RegistrationStatus == RegistrationStatuses.Approved
            && await repository.HasActiveContractAsync(request.RegistrationId, cancellationToken))
        {
            throw new DomainRuleException(RegMessages.WithdrawBlockedActiveContract);
        }

        await repository.SetStatusAsync(request.RegistrationId, RegistrationStatuses.Withdrawn, cancellationToken);
        return Unit.Value;
    }
}
