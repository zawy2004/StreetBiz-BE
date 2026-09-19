using FluentValidation;
using MediatR;
using StreetBiz.Application.Common.Exceptions;
using StreetBiz.Application.Common.Interfaces;
using StreetBiz.Application.Common.Models;
using StreetBiz.Application.Common.Security;
using StreetBiz.Application.DTOs.Sidewalk;

namespace StreetBiz.Application.Features.SlotProposals.ProposeSlot;

/// <summary>SIDE-11: propose a new sidewalk slot at an address not on the ward's grid.</summary>
public sealed record ProposeSlotCommand(
    long RegistrationId,
    int ZoneId,
    decimal Latitude,
    decimal Longitude,
    decimal? WidthMeters,
    decimal? LengthMeters,
    string ProposalPhotoUrl) : IRequest<SlotProposalDto>;

public sealed class ProposeSlotCommandValidator : AbstractValidator<ProposeSlotCommand>
{
    public ProposeSlotCommandValidator()
    {
        RuleFor(x => x.ZoneId).GreaterThan(0);
        RuleFor(x => x.Latitude).InclusiveBetween(-90, 90);
        RuleFor(x => x.Longitude).InclusiveBetween(-180, 180);
        // WARD-16 asks an officer to judge a location off the ward's own grid; without a photo
        // there is nothing to judge, and CK_SidewalkSlots_ProposalCoherent would reject the
        // insert anyway -- catching it here turns a possible 500 into a clean 400.
        RuleFor(x => x.ProposalPhotoUrl).NotEmpty().WithMessage(SideMessages.ProposalPhotoRequired);
    }
}

public sealed class ProposeSlotCommandHandler(
    IVendorContext vendorContext,
    ISidewalkSlotRepository slots)
    : IRequestHandler<ProposeSlotCommand, SlotProposalDto>
{
    private const int MaxSlotCodeAttempts = 5;

    public async Task<SlotProposalDto> Handle(ProposeSlotCommand request, CancellationToken cancellationToken)
    {
        await vendorContext.RequireOwnedRegistrationAsync(request.RegistrationId, cancellationToken);

        if (!await slots.ZoneExistsAsync(request.ZoneId, cancellationToken))
        {
            throw new ValidationAppException(new Dictionary<string, string[]>
            {
                ["ZoneId"] = [SideMessages.ZoneNotFound],
            });
        }

        var proposal = new NewSlotProposal(
            request.ZoneId, request.Latitude, request.Longitude,
            request.WidthMeters, request.LengthMeters, request.ProposalPhotoUrl);

        for (var attempt = 1; attempt <= MaxSlotCodeAttempts; attempt++)
        {
            var slotCode = GenerateSlotCode(request.ZoneId);

            try
            {
                var slotId = await slots.ProposeAsync(request.RegistrationId, proposal, slotCode, cancellationToken);
                var created = await slots.GetProposalByIdAsync(slotId, cancellationToken)
                    ?? throw new NotFoundException(SideMessages.SlotNotFound);
                return created.ToDto();
            }
            catch (ConflictException ex) when (attempt < MaxSlotCodeAttempts && ex.Message == SideMessages.SlotCodeGenerationFailed)
            {
                // slot_code collision on the millisecond-precision timestamp — retry with a fresh one.
            }
        }

        throw new ConflictException(SideMessages.SlotCodeGenerationFailed);
    }

    private static string GenerateSlotCode(int zoneId) =>
        $"VP-{zoneId}-{DateTime.UtcNow:yyyyMMddHHmmssfff}";
}
