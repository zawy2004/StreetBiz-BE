using FluentValidation;
using MediatR;
using StreetBiz.Application.Common.Exceptions;
using StreetBiz.Application.Common.Interfaces;
using StreetBiz.Application.Common.Security;
using StreetBiz.Application.DTOs.Sidewalk;

namespace StreetBiz.Application.Features.SlotHolds.CreateSlotHold;

/// <summary>
/// Reserves an AVAILABLE slot for a registration for a short while so the vendor can finish the
/// application without someone else submitting first. Holding a slot you already hold renews it.
/// </summary>
public sealed record CreateSlotHoldCommand(long RegistrationId, long SlotId) : IRequest<SlotHoldDto>;

public sealed class CreateSlotHoldCommandValidator : AbstractValidator<CreateSlotHoldCommand>
{
    public CreateSlotHoldCommandValidator()
    {
        RuleFor(x => x.RegistrationId).GreaterThan(0);
        RuleFor(x => x.SlotId).GreaterThan(0);
    }
}

public sealed class CreateSlotHoldCommandHandler(
    IVendorContext vendorContext,
    ISidewalkSlotRepository slots,
    IRentalApplicationRepository applications,
    ISlotHoldRepository holds,
    ISidewalkPolicy sidewalkPolicy,
    IDateTimeProvider clock)
    : IRequestHandler<CreateSlotHoldCommand, SlotHoldDto>
{
    public async Task<SlotHoldDto> Handle(CreateSlotHoldCommand request, CancellationToken cancellationToken)
    {
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

        var now = clock.UtcNow;

        var existing = await SlotHoldRules.RequireNotHeldByAnotherAsync(
            holds, now, request.SlotId, request.RegistrationId, cancellationToken);

        // Renewing a hold does not take an extra place, so the limit only applies to a new one.
        if (existing is null)
        {
            var mine = await holds.ListActiveByRegistrationAsync(request.RegistrationId, now, cancellationToken);
            if (mine.Count >= sidewalkPolicy.MaxSlotHoldsPerRegistration)
            {
                throw new ConflictException(SideMessages.SlotHoldLimitReached);
            }
        }

        var expiresAt = now.AddMinutes(sidewalkPolicy.SlotHoldTtlMinutes);
        await holds.UpsertAsync(request.SlotId, request.RegistrationId, now, expiresAt, cancellationToken);

        return new SlotHoldDto(request.SlotId, request.RegistrationId, now, expiresAt);
    }
}
