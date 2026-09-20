using FluentValidation;
using MediatR;
using StreetBiz.Application.Common.Exceptions;
using StreetBiz.Application.Common.Interfaces;
using StreetBiz.Application.Common.Security;
using StreetBiz.Application.DTOs.Sidewalk;

namespace StreetBiz.Application.Features.SidewalkSlots.GetSlotQuote;

/// <summary>An informational price estimate for renting a slot for a number of days.</summary>
public sealed record GetSlotQuoteQuery(long SlotId, int TermDays) : IRequest<FeeQuoteDto>;

public sealed class GetSlotQuoteQueryValidator : AbstractValidator<GetSlotQuoteQuery>
{
    public GetSlotQuoteQueryValidator()
    {
        RuleFor(x => x.TermDays).InclusiveBetween(1, 365);
    }
}

public sealed class GetSlotQuoteQueryHandler(ISidewalkSlotRepository slots, ISidewalkZoneRepository zones)
    : IRequestHandler<GetSlotQuoteQuery, FeeQuoteDto>
{
    public async Task<FeeQuoteDto> Handle(GetSlotQuoteQuery request, CancellationToken cancellationToken)
    {
        var slot = await slots.GetByIdAsync(request.SlotId, cancellationToken)
            ?? throw new NotFoundException(SideMessages.SlotNotFound);

        var components = await zones.ListFeeComponentsAsync(slot.ZoneId, cancellationToken);

        return FeeQuoteCalculator.Calculate(slot.SlotId, slot.PricePerDay, request.TermDays, components);
    }
}
