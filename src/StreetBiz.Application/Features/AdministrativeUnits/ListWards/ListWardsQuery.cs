using MediatR;
using StreetBiz.Application.Common.Interfaces;
using StreetBiz.Application.DTOs.AdministrativeUnits;

namespace StreetBiz.Application.Features.AdministrativeUnits.ListWards;

/// <summary>Reference data for the ward pickers in sign-up and vendor registration.</summary>
public sealed record ListWardsQuery : IRequest<IReadOnlyList<WardDto>>;

public sealed class ListWardsQueryHandler(IAdministrativeUnitRepository repository)
    : IRequestHandler<ListWardsQuery, IReadOnlyList<WardDto>>
{
    public Task<IReadOnlyList<WardDto>> Handle(ListWardsQuery request, CancellationToken cancellationToken)
        => repository.ListWardsAsync(cancellationToken);
}
