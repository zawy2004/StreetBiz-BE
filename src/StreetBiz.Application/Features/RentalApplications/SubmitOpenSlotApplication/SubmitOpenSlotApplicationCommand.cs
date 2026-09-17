using FluentValidation;
using MediatR;
using StreetBiz.Application.Common.Exceptions;
using StreetBiz.Application.Common.Interfaces;
using StreetBiz.Application.Common.Security;
using StreetBiz.Application.DTOs.RentalApplications;

namespace StreetBiz.Application.Features.RentalApplications.SubmitOpenSlotApplication;

/// <summary>SIDE-03B: submit a rental application for an open slot chosen from the map.</summary>
public sealed record SubmitOpenSlotApplicationCommand(
    long RegistrationId,
    long SlotId,
    int RequestedTermDays) : IRequest<RentalApplicationDto>;

public sealed class SubmitOpenSlotApplicationCommandValidator : AbstractValidator<SubmitOpenSlotApplicationCommand>
{
    public SubmitOpenSlotApplicationCommandValidator()
    {
        RuleFor(x => x.SlotId).GreaterThan(0);
        RuleFor(x => x.RequestedTermDays).GreaterThan(0);
    }
}

public sealed class SubmitOpenSlotApplicationCommandHandler(
    IVendorContext vendorContext,
    ISidewalkSlotRepository slots,
    IRentalApplicationRepository applications)
    : IRequestHandler<SubmitOpenSlotApplicationCommand, RentalApplicationDto>
{
    public async Task<RentalApplicationDto> Handle(SubmitOpenSlotApplicationCommand request, CancellationToken cancellationToken)
    {
        // Ownership only. BR-16 (registration must be APPROVED) is checked at WARD-08
        // approval time, not at submission — a vendor may apply while still under review.
        await vendorContext.RequireOwnedRegistrationAsync(request.RegistrationId, cancellationToken);

        var slot = await slots.GetByIdAsync(request.SlotId, cancellationToken)
            ?? throw new NotFoundException(SideMessages.SlotNotFound);

        if (slot.SlotStatus != SlotStatuses.Available)
        {
            throw new ConflictException(SideMessages.SlotNotAvailable);
        }

        if (await applications.HasOpenApplicationForSlotAsync(request.SlotId, cancellationToken))
        {
            throw new ConflictException(SideMessages.ApplicationAlreadyOpenForSlot);
        }

        var applicationId = await applications.CreateAsync(
            request.RegistrationId, request.SlotId, ApplicationMethods.ManualSelected,
            request.RequestedTermDays, cancellationToken);

        var created = await applications.GetByIdAsync(applicationId, cancellationToken)
            ?? throw new NotFoundException(SideMessages.ApplicationNotFound);

        return created.ToDto();
    }
}
