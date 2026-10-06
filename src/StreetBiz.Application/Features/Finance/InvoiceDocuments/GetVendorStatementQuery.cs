using FluentValidation;
using MediatR;
using StreetBiz.Application.Common.Interfaces;
using StreetBiz.Application.Common.Models;
using StreetBiz.Application.Common.Security;
using StreetBiz.Application.Features.Finance.VendorContracts;
using StreetBiz.Application.Features.Finance.WardReports;

namespace StreetBiz.Application.Features.Finance.InvoiceDocuments;

/// <summary>
/// FEE-03/FEE-05: one year of a vendor's receipts and instalments as a workbook, defaulting to the
/// current year (Vietnam time). Receipts are filed by the day they were issued, instalments by
/// their due date, both on the Vietnamese calendar.
/// </summary>
public sealed record GetVendorStatementQuery(int? Year) : IRequest<FinanceFile>;

public sealed class GetVendorStatementQueryValidator : AbstractValidator<GetVendorStatementQuery>
{
    public GetVendorStatementQueryValidator() =>
        RuleFor(x => x.Year).InclusiveBetween(2020, 2100).When(x => x.Year is not null);
}

public sealed class GetVendorStatementQueryHandler(
    IVendorContext vendorContext,
    IFinanceRepository finance,
    IVendorFinanceRepository vendorFinance,
    IFinanceDocumentRenderer renderer,
    TimeProvider clock) : IRequestHandler<GetVendorStatementQuery, FinanceFile>
{
    public async Task<FinanceFile> Handle(GetVendorStatementQuery request, CancellationToken cancellationToken)
    {
        var vendorId = await vendorContext.RequireVendorIdAsync(cancellationToken);
        var today = BusinessCalendar.Today(clock);
        var year = request.Year ?? today.Year;

        var invoices = (await finance.ListInvoicesAsync(vendorId, cancellationToken))
            .Where(invoice => BusinessCalendar.DateOf(invoice.IssuedAt).Year == year)
            .ToList();
        var slotByContract = (await vendorFinance.ListContractsAsync(vendorId, cancellationToken))
            .ToDictionary(contract => contract.ContractId, contract => contract.SlotCode);
        var instalments = (await vendorFinance.ListScheduleItemsAsync(vendorId, null, cancellationToken))
            .Where(item => item.DueDate.Year == year)
            .Select(item =>
            {
                var dto = FeeScheduleProgress.ToDto(item, today);
                return new StatementInstalmentRow(
                    slotByContract.GetValueOrDefault(item.ContractId) ?? "",
                    dto.PeriodLabel,
                    dto.DueDate,
                    dto.Amount,
                    dto.ItemStatus,
                    dto.PaidAt,
                    dto.InvoiceNumber);
            })
            .ToList();
        var (vendorName, businessName) = await vendorFinance.GetVendorNamesAsync(vendorId, cancellationToken);

        var workbook = renderer.RenderVendorStatementXlsx(new VendorStatementExport(
            vendorName, businessName, year, invoices, instalments, clock.GetUtcNow().UtcDateTime));
        return new FinanceFile(
            $"sao-ke-phi-{year}.xlsx", ExportCollectionReportQueryHandler.XlsxContentType, workbook);
    }
}
