using MediatR;
using StreetBiz.Application.Common.Interfaces;
using StreetBiz.Application.Common.Security;
using StreetBiz.Application.DTOs.Finance;

namespace StreetBiz.Application.Features.Finance.ListPayments;

/// <summary>FEE-05.</summary>
public sealed record ListPaymentTransactionsQuery : IRequest<IReadOnlyList<PaymentTransactionDto>>;

public sealed class ListPaymentTransactionsQueryHandler(IVendorContext vendorContext, IFinanceRepository finance)
    : IRequestHandler<ListPaymentTransactionsQuery, IReadOnlyList<PaymentTransactionDto>>
{
    public async Task<IReadOnlyList<PaymentTransactionDto>> Handle(
        ListPaymentTransactionsQuery request, CancellationToken cancellationToken)
    {
        var vendorId = await vendorContext.RequireVendorIdAsync(cancellationToken);
        var transactions = await finance.ListPaymentTransactionsAsync(vendorId, cancellationToken);
        return transactions.Select(transaction => transaction.ToDto()).ToList();
    }
}
