using MediatR;
using StreetBiz.Application.Common.Exceptions;
using StreetBiz.Application.Common.Interfaces;
using StreetBiz.Application.Common.Security;

namespace StreetBiz.Application.Features.VendorRegistration.RemoveEvidence;

/// <summary>REG-02: remove an attached document so a wrong or blurry upload can be replaced.</summary>
public sealed record RemoveEvidenceCommand(long RegistrationId, long EvidenceId) : IRequest<Unit>;

public sealed class RemoveEvidenceCommandHandler(
    IVendorContext vendorContext,
    IBusinessRegistrationRepository repository) : IRequestHandler<RemoveEvidenceCommand, Unit>
{
    public async Task<Unit> Handle(RemoveEvidenceCommand request, CancellationToken cancellationToken)
    {
        var registration = await vendorContext.RequireOwnedRegistrationAsync(request.RegistrationId, cancellationToken);

        // Same BR-62 window as adding: documents cannot change under an officer who is reviewing them.
        if (!RegistrationStatuses.Editable.Contains(registration.RegistrationStatus))
        {
            throw new DomainRuleException(
                string.Format(RegMessages.NotEditable, RegMessages.StatusWord(registration.RegistrationStatus)));
        }

        if (!await repository.RemoveEvidenceAsync(request.RegistrationId, request.EvidenceId, cancellationToken))
        {
            throw new NotFoundException(RegMessages.EvidenceNotFound);
        }

        return Unit.Value;
    }
}
