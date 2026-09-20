using FluentValidation;
using MediatR;
using StreetBiz.Application.Common.Exceptions;
using StreetBiz.Application.Common.Geo;
using StreetBiz.Application.Common.Interfaces;
using StreetBiz.Application.Common.Security;
using StreetBiz.Application.DTOs.RentalApplications;
using StreetBiz.Application.Features.SlotHolds;

namespace StreetBiz.Application.Features.RentalApplications.SubmitAdjacentApplication;

/// <summary>SIDE-03A: submit a storefront-adjacent rental application.</summary>
public sealed record SubmitAdjacentApplicationCommand(
    long RegistrationId,
    long SlotId,
    int RequestedTermDays) : IRequest<RentalApplicationDto>;

public sealed class SubmitAdjacentApplicationCommandValidator : AbstractValidator<SubmitAdjacentApplicationCommand>
{
    public SubmitAdjacentApplicationCommandValidator()
    {
        RuleFor(x => x.SlotId).GreaterThan(0);
        RuleFor(x => x.RequestedTermDays).GreaterThan(0);
    }
}

public sealed class SubmitAdjacentApplicationCommandHandler(
    IVendorContext vendorContext,
    ISidewalkSlotRepository slots,
    IRentalApplicationRepository applications,
    IRentalContractRepository contracts,
    ISidewalkPolicy sidewalkPolicy,
    ISlotHoldRepository holds,
    IDateTimeProvider clock)
    : IRequestHandler<SubmitAdjacentApplicationCommand, RentalApplicationDto>
{
    public async Task<RentalApplicationDto> Handle(SubmitAdjacentApplicationCommand request, CancellationToken cancellationToken)
    {
        var registration = await vendorContext.RequireOwnedRegistrationAsync(request.RegistrationId, cancellationToken);

        // Only a fixed-storefront registration has a declared address to be adjacent to.
        if (registration.VendorType != VendorTypes.FixedStorefront)
        {
            throw new DomainRuleException(SideMessages.NotEligibleForAdjacent);
        }

        if (registration.AddressLatitude is null || registration.AddressLongitude is null)
        {
            throw new DomainRuleException(SideMessages.RegistrationMissingAddress);
        }

        var slot = await slots.GetByIdAsync(request.SlotId, cancellationToken)
            ?? throw new NotFoundException(SideMessages.SlotNotFound);

        if (slot.SlotStatus != SlotStatuses.Available)
        {
            throw new ConflictException(SideMessages.SlotNotAvailable);
        }

        // BR-11: the slot must be within the configured radius of the registered address.
        var distanceMeters = GeoMath.DistanceMeters(
            (double)registration.AddressLatitude.Value, (double)registration.AddressLongitude.Value,
            (double)slot.Latitude, (double)slot.Longitude);
        if (distanceMeters > sidewalkPolicy.AdjacentRadiusMeters)
        {
            throw new DomainRuleException(SideMessages.OutsideAdjacentRadius);
        }

        // BR-12: at most one active adjacent contract per registration.
        if (await contracts.HasActiveAdjacentContractAsync(request.RegistrationId, cancellationToken))
        {
            throw new ConflictException(SideMessages.AlreadyHasActiveAdjacentContract);
        }

        if (await applications.HasOpenApplicationForSlotAsync(request.SlotId, cancellationToken))
        {
            throw new ConflictException(SideMessages.ApplicationAlreadyOpenForSlot);
        }

        // A vendor holding the slot has reserved it -- an adjacent application must not jump the hold.
        await SlotHoldRules.RequireNotHeldByAnotherAsync(
            holds, clock.UtcNow, request.SlotId, request.RegistrationId, cancellationToken);

        var applicationId = await applications.CreateAsync(
            request.RegistrationId, request.SlotId, ApplicationMethods.AutoAdjacent,
            request.RequestedTermDays, null, cancellationToken);

        await holds.ReleaseAsync(request.SlotId, request.RegistrationId, cancellationToken);

        var created = await applications.GetByIdAsync(applicationId, cancellationToken)
            ?? throw new NotFoundException(SideMessages.ApplicationNotFound);

        return created.ToDto();
    }
}
