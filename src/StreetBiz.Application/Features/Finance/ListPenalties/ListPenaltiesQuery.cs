using FluentValidation;
using MediatR;
using StreetBiz.Application.Common.Interfaces;
using StreetBiz.Application.Common.Security;
using StreetBiz.Application.DTOs.Finance;

namespace StreetBiz.Application.Features.Finance.ListPenalties;

/// <summary>FinanceHome's "Biên bản phạt" tab, optionally filtered by penalty_status.</summary>
public sealed record ListPenaltiesQuery(string? Status) : IRequest<IReadOnlyList<PenaltyListDto>>;

public sealed class ListPenaltiesQueryValidator : AbstractValidator<ListPenaltiesQuery>
{
    public ListPenaltiesQueryValidator() =>
        RuleFor(x => x.Status)
            .Must(status => PenaltyStatuses.IsValid(status!.Trim().ToUpperInvariant()))
            .When(x => !string.IsNullOrWhiteSpace(x.Status))
            .WithMessage("Status must be UNPAID, PAID, WAIVED or CANCELLED.");
}

public sealed class ListPenaltiesQueryHandler(IVendorContext vendorContext, IFinanceRepository finance)
    : IRequestHandler<ListPenaltiesQuery, IReadOnlyList<PenaltyListDto>>
{
    public async Task<IReadOnlyList<PenaltyListDto>> Handle(ListPenaltiesQuery request, CancellationToken cancellationToken)
    {
        var vendorId = await vendorContext.RequireVendorIdAsync(cancellationToken);
        var status = string.IsNullOrWhiteSpace(request.Status) ? null : request.Status.Trim().ToUpperInvariant();
        var penalties = await finance.ListPenaltiesAsync(vendorId, status, cancellationToken);
        return penalties.Select(penalty => penalty.ToDto()).ToList();
    }
}
