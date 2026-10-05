using MediatR;
using StreetBiz.Application.Common.Exceptions;
using StreetBiz.Application.Common.Interfaces;
using StreetBiz.Application.Common.Security;
using StreetBiz.Application.DTOs.VendorRegistration;

namespace StreetBiz.Application.Features.VendorRegistration.FileRegistration;

/// <summary>REG-01: send a completed draft to the ward. Only now does the officer see it.</summary>
public sealed record FileRegistrationCommand(long RegistrationId) : IRequest<BusinessRegistrationDto>;

public sealed class FileRegistrationCommandHandler(
    IVendorContext vendorContext,
    IBusinessRegistrationRepository repository) : IRequestHandler<FileRegistrationCommand, BusinessRegistrationDto>
{
    public async Task<BusinessRegistrationDto> Handle(FileRegistrationCommand request, CancellationToken cancellationToken)
    {
        var registration = await vendorContext.RequireOwnedRegistrationAsync(request.RegistrationId, cancellationToken);

        if (registration.RegistrationStatus != RegistrationStatuses.Draft)
        {
            throw new DomainRuleException(string.Format(
                RegMessages.IllegalTransition, registration.RegistrationStatus, RegistrationStatuses.Submitted));
        }

        // BR-09: one filed application at a time.
        if (await repository.HasActivePendingAsync(
                registration.VendorId, cancellationToken, excludeRegistrationId: registration.RegistrationId))
        {
            throw new ConflictException(RegMessages.DuplicatePending);
        }

        // BR-07: the documents the vendor type requires must be on file before it can be filed.
        var attached = (await repository.ListEvidenceAsync(request.RegistrationId, cancellationToken))
            .Select(e => e.EvidenceType)
            .ToHashSet();
        var missing = EvidenceTypes.RequiredFor(registration.VendorType)
            .Where(type => !attached.Contains(type))
            .Select(EvidenceTypes.Label)
            .ToList();
        if (missing.Count > 0)
        {
            throw new DomainRuleException(
                string.Format(RegMessages.MissingRequiredEvidence, string.Join(", ", missing)));
        }

        if (!await repository.TryTransitionAsync(request.RegistrationId, RegistrationStatuses.Submitted, cancellationToken))
        {
            throw new ConflictException(RegMessages.ConcurrentChange);
        }

        await repository.RecordSubmittedAsync(request.RegistrationId, cancellationToken);

        var filed = await repository.GetByIdAsync(request.RegistrationId, cancellationToken)
            ?? throw new NotFoundException(RegMessages.NotFound);
        return filed.ToDto();
    }
}
