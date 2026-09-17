using FluentValidation;
using MediatR;
using StreetBiz.Application.Common.Exceptions;
using StreetBiz.Application.Common.Interfaces;
using StreetBiz.Application.Common.Models;
using StreetBiz.Application.Common.Security;
using StreetBiz.Application.DTOs.AddressChanges;

namespace StreetBiz.Application.Features.AddressChanges.RequestAddressChange;

/// <summary>
/// SIDE-09/10: a fixed-storefront vendor requests to change their business address, optionally
/// releasing an adjacent slot tied to the old one and/or requesting a new one. One workflow,
/// one row -- not a direct edit of BusinessRegistrations.declared_address, which only changes
/// once WARD-17 approves.
/// </summary>
public sealed record RequestAddressChangeCommand(
    long RegistrationId,
    string NewAddress,
    decimal? NewLatitude,
    decimal? NewLongitude,
    long? ReleasedContractId,
    long? RequestedNewSlotId) : IRequest<AddressChangeRequestDto>;

public sealed class RequestAddressChangeCommandValidator : AbstractValidator<RequestAddressChangeCommand>
{
    public RequestAddressChangeCommandValidator()
    {
        RuleFor(x => x.NewAddress).NotEmpty().MaximumLength(500);
        RuleFor(x => x.NewLatitude).InclusiveBetween(-90, 90).When(x => x.NewLatitude.HasValue);
        RuleFor(x => x.NewLongitude).InclusiveBetween(-180, 180).When(x => x.NewLongitude.HasValue);
    }
}

public sealed class RequestAddressChangeCommandHandler(
    IVendorContext vendorContext,
    IAddressChangeRequestRepository addressChanges,
    IRentalContractRepository contracts,
    IGeocodingService geocoding)
    : IRequestHandler<RequestAddressChangeCommand, AddressChangeRequestDto>
{
    public async Task<AddressChangeRequestDto> Handle(RequestAddressChangeCommand request, CancellationToken cancellationToken)
    {
        var vendorId = await vendorContext.RequireVendorIdAsync(cancellationToken);
        var registration = await vendorContext.RequireOwnedRegistrationAsync(request.RegistrationId, cancellationToken);

        // Only a fixed-storefront registration has a declared address to change; an itinerant
        // vendor has none.
        if (registration.VendorType != VendorTypes.FixedStorefront)
        {
            throw new DomainRuleException(SideMessages.AddressChangeNotEligible);
        }

        if (await addressChanges.HasOpenAsync(request.RegistrationId, cancellationToken))
        {
            throw new ConflictException(SideMessages.AddressChangeAlreadyOpen);
        }

        if (request.ReleasedContractId is { } releasedContractId)
        {
            var contract = await contracts.GetByIdAsync(releasedContractId, cancellationToken)
                ?? throw new NotFoundException(SideMessages.ContractNotFound);
            if (contract.VendorId != vendorId)
            {
                throw new ForbiddenException(SideMessages.ReleasedContractNotOwned);
            }
        }

        // GPS/manual input is authoritative when given; geocoding only fills a gap, and never
        // blocks the write if it fails or the address does not resolve.
        var latitude = request.NewLatitude;
        var longitude = request.NewLongitude;
        if (latitude is null || longitude is null)
        {
            var geocoded = await geocoding.ForwardAsync(request.NewAddress, cancellationToken);
            latitude ??= geocoded?.Latitude;
            longitude ??= geocoded?.Longitude;
        }

        var data = new NewAddressChangeRequest(
            request.NewAddress, latitude, longitude, request.ReleasedContractId, request.RequestedNewSlotId);

        var addressChangeId = await addressChanges.CreateAsync(request.RegistrationId, data, cancellationToken);

        var created = await addressChanges.GetByIdAsync(addressChangeId, cancellationToken)
            ?? throw new NotFoundException(SideMessages.AddressChangeNotFound);

        return created.ToDto();
    }
}
