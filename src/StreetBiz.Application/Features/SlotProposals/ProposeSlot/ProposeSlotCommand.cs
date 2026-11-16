using FluentValidation;
using MediatR;
using StreetBiz.Application.Common.Exceptions;
using StreetBiz.Application.Common.Geo;
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
        // DECIMAL(5,2) columns; a negative or zero size describes no place on the pavement.
        RuleFor(x => x.WidthMeters).Must(BeARealSize).WithMessage(SideMessages.ProposalDimensionInvalid);
        RuleFor(x => x.LengthMeters).Must(BeARealSize).WithMessage(SideMessages.ProposalDimensionInvalid);
        // WARD-16 asks an officer to judge a location off the ward's own grid; without a photo
        // there is nothing to judge, and CK_SidewalkSlots_ProposalCoherent would reject the
        // insert anyway -- catching it here turns a possible 500 into a clean 400.
        RuleFor(x => x.ProposalPhotoUrl).NotEmpty().WithMessage(SideMessages.ProposalPhotoRequired);
    }

    /// <summary>A size is optional; when given it must fit DECIMAL(5,2) and be more than nothing.</summary>
    private static bool BeARealSize(decimal? meters) => meters is null or (> 0m and <= 999.99m);
}

public sealed class ProposeSlotCommandHandler(
    IVendorContext vendorContext,
    ISidewalkSlotRepository slots,
    ICurrentUser currentUser)
    : IRequestHandler<ProposeSlotCommand, SlotProposalDto>
{
    private const int MaxSlotCodeAttempts = 5;

    public async Task<SlotProposalDto> Handle(ProposeSlotCommand request, CancellationToken cancellationToken)
    {
        await vendorContext.RequireOwnedRegistrationAsync(request.RegistrationId, cancellationToken);

        // The officer reviewing the proposal opens this photo: only a file the vendor uploaded is
        // accepted, never a planted link (javascript:, another host, someone else's file).
        if (currentUser.UserId is not { } userId || !EvidenceFiles.IsOwnUpload(request.ProposalPhotoUrl, userId))
        {
            throw new ValidationAppException(new Dictionary<string, string[]>
            {
                ["ProposalPhotoUrl"] = [SideMessages.ProposalPhotoInvalid],
            });
        }

        if (!await slots.ZoneExistsAsync(request.ZoneId, cancellationToken))
        {
            throw new ValidationAppException(new Dictionary<string, string[]>
            {
                ["ZoneId"] = [SideMessages.ZoneNotFound],
            });
        }

        await RequireFreeSpotAsync(request, cancellationToken);

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

    /// <summary>
    /// Two slots closer than one metre are the same spot; with sizes, their footprints (circles of
    /// half the longer side, as the ward's own placement check draws them) must not overlap.
    /// </summary>
    public const double SameSpotMeters = 1.0;

    /// <summary>Largest footprint radius looked for around the point (a 999.99 m slot is not real).</summary>
    private const double SearchRadiusMeters = 100;

    /// <summary>
    /// SIDE-11 is for an address that is not on the ward's grid. A proposal on top of an existing
    /// slot (or of another vendor's pending proposal) is a duplicate: the vendor should apply for
    /// that slot instead, and the officer would only reject it.
    /// </summary>
    private async Task RequireFreeSpotAsync(ProposeSlotCommand request, CancellationToken cancellationToken)
    {
        var lat = (double)request.Latitude;
        var lng = (double)request.Longitude;
        var radius = Radius(request.WidthMeters, request.LengthMeters);
        var box = GeoMath.BoundingBox(lat, lng, SearchRadiusMeters + radius);
        var nearby = await slots.ListFootprintsInAsync(
            new SlotSearchArea((decimal)box.MinLat, (decimal)box.MaxLat, (decimal)box.MinLon, (decimal)box.MaxLon,
                null, null, IncludeUnavailable: true),
            cancellationToken);

        var clash = nearby
            .Select(slot => (slot.SlotCode, Distance: GeoMath.DistanceMeters(lat, lng, (double)slot.Latitude, (double)slot.Longitude),
                Reach: Math.Max(SameSpotMeters, radius + Radius(slot.WidthMeters, slot.LengthMeters))))
            .Where(x => x.Distance < x.Reach)
            .OrderBy(x => x.Distance)
            .FirstOrDefault();

        if (clash.SlotCode is not null)
        {
            throw new ConflictException(
                $"Vị trí này trùng hoặc chồng lấn ô {clash.SlotCode} đã có (cách {clash.Distance:0.0} m). " +
                "Hãy đăng ký ô đó hoặc chọn vị trí khác.");
        }
    }

    private static double Radius(decimal? width, decimal? length) =>
        (double)Math.Max(width ?? 0m, length ?? 0m) / 2;

    private static string GenerateSlotCode(int zoneId) =>
        $"VP-{zoneId}-{DateTime.UtcNow:yyyyMMddHHmmssfff}";
}
