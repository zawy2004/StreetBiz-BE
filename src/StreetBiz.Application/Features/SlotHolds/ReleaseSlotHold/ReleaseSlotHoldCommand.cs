using MediatR;
using StreetBiz.Application.Common.Exceptions;
using StreetBiz.Application.Common.Interfaces;
using StreetBiz.Application.Common.Security;

namespace StreetBiz.Application.Features.SlotHolds.ReleaseSlotHold;

/// <summary>Gives up a hold the registration owns before it lapses on its own.</summary>
public sealed record ReleaseSlotHoldCommand(long RegistrationId, long SlotId) : IRequest;

public sealed class ReleaseSlotHoldCommandHandler(IVendorContext vendorContext, ISlotHoldRepository holds)
    : IRequestHandler<ReleaseSlotHoldCommand>
{
    public async Task Handle(ReleaseSlotHoldCommand request, CancellationToken cancellationToken)
    {
        await vendorContext.RequireOwnedRegistrationAsync(request.RegistrationId, cancellationToken);

        if (!await holds.ReleaseAsync(request.SlotId, request.RegistrationId, cancellationToken))
        {
            throw new NotFoundException(SideMessages.SlotHoldNotFound);
        }
    }
}
