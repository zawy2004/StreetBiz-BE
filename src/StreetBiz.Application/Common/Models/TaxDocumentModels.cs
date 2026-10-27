namespace StreetBiz.Application.Common.Models;

/// <summary>One month of a household's revenue book.</summary>
public sealed record TaxBookMonth(int Month, decimal Online, decimal Recorded)
{
    public decimal Total => Online + Recorded;
}

/// <summary>One day of the revenue book, with the entries recorded that day.</summary>
public sealed record TaxBookDay(
    DateOnly Date,
    decimal Online,
    int OnlineOrders,
    decimal Recorded,
    string Remarks);

public sealed record TaxBookEntry(
    DateOnly Date,
    decimal Amount,
    string Channel,
    string? Note,
    DateTime CreatedAt,
    bool Voided,
    string? VoidReason);

/// <summary>
/// Everything printed in a household's revenue book for one tax year (the .xlsx) and in its
/// yearly summary (the PDF): who, how much by month and source, where it stands against the
/// threshold, and the filings it has confirmed.
/// </summary>
public sealed record TaxBookExport(
    int TaxYear,
    string VendorName,
    string? BusinessName,
    string? TaxCode,
    string IndustryName,
    DateTime GeneratedAt,
    decimal Threshold,
    string Group,
    string GroupDescription,
    string Level,
    decimal Online,
    decimal Recorded,
    IReadOnlyList<TaxBookMonth> Months,
    IReadOnlyList<TaxBookDay> Days,
    IReadOnlyList<TaxBookEntry> Entries,
    IReadOnlyList<TaxBookFiling> Filings)
{
    public decimal Total => Online + Recorded;
}

public sealed record TaxBookFiling(
    string Label,
    DateOnly DueDate,
    string Status,
    decimal? RevenueDeclared,
    DateOnly? FiledOn,
    string? AuthorityReference);

/// <summary>One household row of the ward's tax workbook; revenue is null unless it was shared.</summary>
public sealed record WardTaxExportHousehold(
    string DisplayName,
    string OwnerName,
    string? Phone,
    string? Address,
    string? BusinessLine,
    string Status,
    string SlotCodes,
    DateTime ApprovedAt,
    DateTime? LastInspectionAt,
    string TaxCodeMasked,
    bool NoticeFiled,
    decimal? Revenue,
    string? Group);

public sealed record WardTaxExportReferral(
    string ReasonLabel,
    string Summary,
    string? VendorName,
    DateTime DetectedAt,
    string Status,
    DateTime? HandledAt,
    string? AuthorityReference,
    string? HandlingNote);

/// <summary>The ward's tax workbook: summary figures, the household list and the referral list.</summary>
public sealed record WardTaxExport(
    int TaxYear,
    string WardName,
    DateTime GeneratedAt,
    IReadOnlyList<(string Label, string Value)> Summary,
    IReadOnlyList<WardTaxExportHousehold> Households,
    IReadOnlyList<WardTaxExportReferral> Referrals);
