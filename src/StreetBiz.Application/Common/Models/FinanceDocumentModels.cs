using StreetBiz.Application.DTOs.Finance;

namespace StreetBiz.Application.Common.Models;

/// <summary>A file the API hands back for download (receipt PDF, report workbook).</summary>
public sealed record FinanceFile(string FileName, string ContentType, byte[] Content);

/// <summary>
/// Everything printed on a payment receipt (FEE-03 "tải hoá đơn"): who collected, who paid,
/// for what, how and when. Kind is "FEE" or "PENALTY", as on <see cref="InvoiceRow"/>.
/// </summary>
public sealed record InvoiceDocumentRow(
    string InvoiceNumber,
    string Kind,
    decimal Amount,
    DateTime IssuedAt,
    string? WardName,
    string? PayerName,
    string? PayerPhone,
    string? BusinessName,
    string? SlotCode,
    string? PeriodLabel,
    string? ViolationLabel,
    string? DecisionNumber,
    string? PaymentProvider,
    string? ProviderReference,
    DateTime? PaidAt);

/// <summary>One receipt issued inside a ward during a report period (the "bảng kê thu").</summary>
public sealed record WardInvoiceRow(
    string InvoiceNumber,
    DateTime IssuedAt,
    string Kind,
    string? PayerName,
    string? SlotCode,
    string Description,
    decimal Amount,
    string? PaymentProvider);

/// <summary>WARD-14 as a workbook: the on-screen totals plus the receipts behind them.</summary>
public sealed record CollectionReportExport(
    string WardName,
    DateOnly From,
    DateOnly To,
    CollectionReportRow Report,
    IReadOnlyList<WardInvoiceRow> Invoices,
    DateTime GeneratedAt)
{
    /// <summary>Households with overdue fees at the time of export (the "Nợ phí" sheet).</summary>
    public IReadOnlyList<WardDebtorDto> Debtors { get; init; } = [];

    /// <summary>On-time rate and per-zone figures for the same period, when computed.</summary>
    public CollectionPerformanceDto? Performance { get; init; }
}

/// <summary>One instalment on a vendor's yearly statement.</summary>
public sealed record StatementInstalmentRow(
    string SlotCode,
    string PeriodLabel,
    DateOnly DueDate,
    decimal Amount,
    string Status,
    DateTime? PaidAt,
    string? InvoiceNumber);

/// <summary>
/// FEE-03/FEE-05 for a whole year: every receipt issued to the vendor and every instalment that
/// fell due, e.g. for a household business's own bookkeeping.
/// </summary>
public sealed record VendorStatementExport(
    string VendorName,
    string? BusinessName,
    int Year,
    IReadOnlyList<InvoiceRow> Invoices,
    IReadOnlyList<StatementInstalmentRow> Instalments,
    DateTime GeneratedAt);
