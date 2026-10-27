using StreetBiz.Application.Common.Models;

namespace StreetBiz.Application.Common.Interfaces;

/// <summary>Turns household tax read models into downloadable documents. Pure: no data access.</summary>
public interface ITaxDocumentRenderer
{
    /// <summary>The household's revenue book for a tax year as an .xlsx workbook.</summary>
    byte[] RenderRevenueBookXlsx(TaxBookExport book);

    /// <summary>The household's yearly revenue summary as a PDF (a worksheet for filing, not a tax form).</summary>
    byte[] RenderRevenueSummaryPdf(TaxBookExport book, DateTime printedAtUtc);

    /// <summary>The ward's household list and referrals for the tax office, as an .xlsx workbook.</summary>
    byte[] RenderWardTaxXlsx(WardTaxExport export);
}
