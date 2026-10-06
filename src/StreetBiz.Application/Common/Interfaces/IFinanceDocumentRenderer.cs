using StreetBiz.Application.Common.Models;

namespace StreetBiz.Application.Common.Interfaces;

/// <summary>Turns finance read models into downloadable documents. Pure: no data access.</summary>
public interface IFinanceDocumentRenderer
{
    /// <summary>FEE-03: the payment receipt as a PDF.</summary>
    byte[] RenderInvoicePdf(InvoiceDocumentRow invoice, DateTime printedAtUtc);

    /// <summary>WARD-14: the collection report as an .xlsx workbook.</summary>
    byte[] RenderCollectionReportXlsx(CollectionReportExport report);

    /// <summary>FEE-03/FEE-05: a vendor's receipts and instalments for one year, as an .xlsx workbook.</summary>
    byte[] RenderVendorStatementXlsx(VendorStatementExport statement);
}
