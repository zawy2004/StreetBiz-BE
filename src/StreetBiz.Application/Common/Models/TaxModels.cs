namespace StreetBiz.Application.Common.Models;

// Read/write models of the household tax module (db/StreetBiz_SQL_Server.sql, section 12).
// Plain records: the repository fills them, the pure rules in Features/Tax/TaxRules.cs read them.

public sealed record TaxIndustryRateRow(string Code, string Name, decimal VatRate, decimal PitRate, string? Note);

/// <summary>One published version of a tax year's parameters, with its rates by industry.</summary>
public sealed record TaxPolicyRow(
    int PolicyId,
    int TaxYear,
    int VersionNo,
    string LegalBasis,
    decimal RevenueThreshold,
    decimal EInvoiceThreshold,
    decimal Group2Ceiling,
    decimal Group3Ceiling,
    bool PitDeductsThreshold,
    decimal PitProfitRateG2,
    decimal PitProfitRateG3,
    decimal PitProfitRateG4,
    int AnnualNoticeMonth,
    int AnnualNoticeDay,
    decimal WarnRatio,
    string? Note,
    DateTime CreatedAt,
    IReadOnlyList<TaxIndustryRateRow> Rates);

/// <summary>What a platform administrator publishes; the repository numbers the version.</summary>
public sealed record NewTaxPolicy(
    int TaxYear,
    string LegalBasis,
    decimal RevenueThreshold,
    decimal EInvoiceThreshold,
    decimal Group2Ceiling,
    decimal Group3Ceiling,
    bool PitDeductsThreshold,
    decimal PitProfitRateG2,
    decimal PitProfitRateG3,
    decimal PitProfitRateG4,
    int AnnualNoticeMonth,
    int AnnualNoticeDay,
    decimal WarnRatio,
    string? Note,
    IReadOnlyList<TaxIndustryRateRow> Rates);

public sealed record VendorTaxProfileRow(
    long VendorId,
    string? TaxCode,
    string IndustryCode,
    bool EInvoiceRegistered,
    bool ShareRevenueWithWard,
    DateTime? ConsentUpdatedAt);

/// <summary>
/// One Vietnamese calendar day of a household's sales: <c>Online</c> from completed StreetBiz orders
/// (net of refunds), <c>Recorded</c> from the entries the household typed in.
/// </summary>
public sealed record RevenueDayRow(DateOnly Date, decimal Online, int OnlineOrders, decimal Recorded)
{
    public decimal Total => Online + Recorded;
}

public sealed record RevenueEntryRow(
    long EntryId,
    DateOnly Date,
    decimal Amount,
    string Channel,
    string? Note,
    DateTime CreatedAt,
    DateTime? VoidedAt,
    string? VoidReason);

public sealed record TaxDeclarationRow(
    long DeclarationId,
    int TaxYear,
    string PeriodType,
    int PeriodNo,
    decimal RevenueDeclared,
    decimal RevenueSystem,
    DateOnly FiledOn,
    string? AuthorityReference,
    string? ReceiptUrl,
    string? Note,
    DateTime CreatedAt,
    DateTime? UpdatedAt);

public sealed record SaveTaxDeclaration(
    int TaxYear,
    string PeriodType,
    int PeriodNo,
    decimal RevenueDeclared,
    decimal RevenueSystem,
    DateOnly FiledOn,
    string? AuthorityReference,
    string? ReceiptUrl,
    string? Note);

/// <summary>A household's revenue for one tax year, split by source, with the day it first sold.</summary>
public sealed record YearRevenueRow(long VendorId, decimal Online, decimal Recorded, DateOnly? FirstDate)
{
    public decimal Total => Online + Recorded;
}

/// <summary>
/// The facts about one household of a ward that its status and tax standing are derived from.
/// Never carries revenue: that is read separately and only shown with the household's consent.
/// </summary>
public sealed record WardHouseholdFacts(
    long VendorId,
    long OwnerUserId,
    string DisplayName,
    string OwnerName,
    string? Phone,
    string? Address,
    string? BusinessLine,
    string VendorType,
    DateTime ApprovedAt,
    int TotalContracts,
    int ActiveContracts,
    int SuspendedContracts,
    IReadOnlyList<string> SlotCodes,
    string? PermitStatus,
    DateTime? LastInspectionAt,
    DateTime? LastOrderAt,
    string? TaxCode,
    bool EInvoiceRegistered,
    bool ShareRevenueWithWard);

public sealed record TaxReferralRow(
    long ReferralId,
    int WardId,
    long? VendorId,
    string? VendorName,
    string ReasonCode,
    string SourceType,
    long SourceId,
    string Summary,
    DateTime DetectedAt,
    DateTime CreatedAt,
    string Status,
    long? HandledBy,
    string? HandledByName,
    DateTime? HandledAt,
    string? AuthorityReference,
    string? HandlingNote);

public sealed record TaxReferralRefreshResult(int Created, int AutoClosed);

public sealed record TaxAnnouncementRow(
    long AnnouncementId,
    int WardId,
    string Segment,
    int TaxYear,
    string Title,
    string Body,
    int RecipientCount,
    string? CreatedByName,
    DateTime CreatedAt);
