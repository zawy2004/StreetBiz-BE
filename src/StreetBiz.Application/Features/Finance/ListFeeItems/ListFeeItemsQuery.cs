using FluentValidation;
using MediatR;
using StreetBiz.Application.Common.Interfaces;
using StreetBiz.Application.Common.Security;
using StreetBiz.Application.DTOs.Finance;

namespace StreetBiz.Application.Features.Finance.ListFeeItems;

/// <summary>FinanceHome's "Phí thuê ô" tab, optionally filtered by item_status.</summary>
public sealed record ListFeeItemsQuery(string? Status) : IRequest<IReadOnlyList<FeeItemDto>>;

public sealed class ListFeeItemsQueryValidator : AbstractValidator<ListFeeItemsQuery>
{
    public ListFeeItemsQueryValidator() =>
        RuleFor(x => x.Status)
            .Must(status => FeeItemStatuses.IsValid(status!.Trim().ToUpperInvariant()))
            .When(x => !string.IsNullOrWhiteSpace(x.Status))
            .WithMessage("Status must be PENDING, OVERDUE or PAID.");
}

public sealed class ListFeeItemsQueryHandler(IVendorContext vendorContext, IFinanceRepository finance)
    : IRequestHandler<ListFeeItemsQuery, IReadOnlyList<FeeItemDto>>
{
    public async Task<IReadOnlyList<FeeItemDto>> Handle(ListFeeItemsQuery request, CancellationToken cancellationToken)
    {
        var vendorId = await vendorContext.RequireVendorIdAsync(cancellationToken);
        var status = string.IsNullOrWhiteSpace(request.Status) ? null : request.Status.Trim().ToUpperInvariant();
        var items = await finance.ListFeeItemsAsync(vendorId, status, cancellationToken);
        return items.Select(item => item.ToDto()).ToList();
    }
}
