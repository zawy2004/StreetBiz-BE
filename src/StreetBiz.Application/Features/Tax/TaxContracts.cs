using StreetBiz.Application.Common.Models;

namespace StreetBiz.Application.Features.Tax;

#region Policy

public sealed record TaxIndustryRateDto(string Code, string Name, decimal VatRate, decimal PitRate, string? Note);

/// <summary>
/// The parameters in force for a tax year. <c>Inherited</c> is true when nobody has published rules
/// for the year yet and the latest earlier ones are shown instead.
/// </summary>
public sealed record TaxPolicyDto(
    int PolicyId,
    int TaxYear,
    int VersionNo,
    bool Inherited,
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
    IReadOnlyList<TaxIndustryRateDto> Rates)
{
    public static TaxPolicyDto From(TaxPolicyRow row, int requestedYear) => new(
        row.PolicyId, row.TaxYear, row.VersionNo, row.TaxYear != requestedYear, row.LegalBasis,
        row.RevenueThreshold, row.EInvoiceThreshold, row.Group2Ceiling, row.Group3Ceiling,
        row.PitDeductsThreshold, row.PitProfitRateG2, row.PitProfitRateG3, row.PitProfitRateG4,
        row.AnnualNoticeMonth, row.AnnualNoticeDay, row.WarnRatio, row.Note, row.CreatedAt,
        [.. row.Rates.Select(rate => new TaxIndustryRateDto(rate.Code, rate.Name, rate.VatRate, rate.PitRate, rate.Note))]);
}

public sealed record CreateTaxPolicyRequest(
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
    IReadOnlyList<TaxIndustryRateDto> Rates);

#endregion

#region Household overview and revenue book

public sealed record TaxProfileDto(
    bool Exists,
    string? TaxCode,
    string IndustryCode,
    string IndustryName,
    bool EInvoiceRegistered,
    bool ShareRevenueWithWard,
    DateTime? ConsentUpdatedAt);

public sealed record UpdateTaxProfileRequest(
    string? TaxCode,
    string IndustryCode,
    bool EInvoiceRegistered,
    bool ShareRevenueWithWard);

public sealed record RevenueTotalsDto(decimal Online, decimal Recorded, decimal Total);

public sealed record TaxMonthRevenueDto(int Month, decimal Online, decimal Recorded, decimal Total);

public sealed record TaxProjectionDto(
    bool Enough,
    int DaysOfData,
    decimal DailyRate,
    decimal ProjectedRevenue,
    string ProjectedGroup,
    string ProjectedLevel,
    bool WillCross,
    DateOnly? CrossingDate);

public sealed record TaxObligationDto(string Code, string Title, string Detail, string Kind, bool? Done);

public sealed record TaxDeadlineDto(string PeriodType, int PeriodNo, string Label, DateOnly DueDate, int DaysLeft, string Status);

/// <summary>
/// Where a household stands for a tax year: revenue to date by source, the revenue group and how
/// close the threshold is, the forecast, what it must do, and its next deadline.
/// </summary>
public sealed record TaxOverviewDto(
    int TaxYear,
    DateOnly Today,
    bool YearEnded,
    TaxPolicyDto Policy,
    RevenueTotalsDto Revenue,
    string Group,
    string GroupDescription,
    string Level,
    decimal ThresholdRatio,
    decimal RemainingToThreshold,
    TaxProjectionDto Projection,
    IReadOnlyList<TaxObligationDto> Obligations,
    IReadOnlyList<TaxObligationDto> ObligationsOnceOver,
    TaxDeadlineDto? NextDeadline,
    TaxProfileDto Profile,
    IReadOnlyList<TaxMonthRevenueDto> Months,
    string Disclaimer);

public sealed record RevenueEntryDto(
    long EntryId,
    DateOnly Date,
    decimal Amount,
    string Channel,
    string? Note,
    DateTime CreatedAt,
    bool Voided,
    string? VoidReason)
{
    public static RevenueEntryDto From(RevenueEntryRow row) => new(
        row.EntryId, row.Date, row.Amount, row.Channel, row.Note, row.CreatedAt, row.VoidedAt is not null, row.VoidReason);
}

public sealed record RevenueDayDto(
    DateOnly Date,
    decimal Online,
    int OnlineOrders,
    decimal Recorded,
    decimal Total,
    IReadOnlyList<RevenueEntryDto> Entries);

public sealed record RevenueLedgerDto(
    int Year,
    int Month,
    RevenueTotalsDto Totals,
    IReadOnlyList<RevenueDayDto> Days);

public sealed record AddRevenueEntryRequest(DateOnly Date, decimal Amount, string Channel, string? Note);

public sealed record VoidRevenueEntryRequest(string? Reason);

#endregion

#region Estimate

public sealed record TaxEstimateDto(
    int TaxYear,
    string Basis,
    decimal Revenue,
    string IndustryCode,
    string IndustryName,
    string Group,
    string GroupDescription,
    bool Taxable,
    decimal VatRate,
    decimal PitRate,
    decimal Vat,
    decimal PitBase,
    decimal PitOnRevenue,
    decimal TotalOnRevenue,
    decimal EffectiveRate,
    decimal? CostRatio,
    decimal? Profit,
    decimal? PitProfitRate,
    decimal? PitOnProfit,
    decimal? TotalOnProfit,
    string? Cheaper,
    bool PitDeductsThreshold,
    decimal RevenueThreshold,
    string LegalBasis,
    string Disclaimer);

#endregion

#region Declarations (filings the household confirms)

public sealed record TaxFilingDto(
    decimal RevenueDeclared,
    decimal RevenueSystem,
    DateOnly FiledOn,
    string? AuthorityReference,
    string? ReceiptUrl,
    string? Note,
    DateTime CreatedAt,
    DateTime? UpdatedAt);

public sealed record TaxPeriodDto(
    string PeriodType,
    int PeriodNo,
    string Label,
    DateOnly Start,
    DateOnly End,
    DateOnly DueDate,
    string Status,
    bool Required,
    bool CanFile,
    decimal SystemRevenue,
    TaxFilingDto? Filing);

public sealed record TaxDeclarationsDto(
    int TaxYear,
    DateOnly Today,
    string Group,
    IReadOnlyList<TaxPeriodDto> Periods,
    string Disclaimer);

public sealed record ConfirmTaxFilingRequest(
    decimal RevenueDeclared,
    DateOnly FiledOn,
    string? AuthorityReference,
    string? ReceiptUrl,
    string? Note);

#endregion

#region Ward

public sealed record TaxGroupCountsDto(int G1, int G2, int G3, int G4, int NoRevenue);

public sealed record WardTaxDashboardDto(
    int TaxYear,
    DateOnly Today,
    TaxPolicyDto Policy,
    int HouseholdCount,
    int ActiveCount,
    int SuspendedCount,
    int EndedCount,
    int NotStartedCount,
    int WithTaxCode,
    int WithoutTaxCode,
    int SharingRevenue,
    TaxGroupCountsDto Groups,
    int NearThresholdCount,
    int OverThresholdCount,
    int NoticeFiled,
    int NoticeNotFiled,
    DateOnly NoticeDueDate,
    int PendingReferrals,
    int ReportedReferrals,
    string Disclaimer);

/// <summary>
/// A household as the ward sees it. <c>Revenue</c> and <c>Group</c> are null unless the household
/// agreed to share them; the tax code is masked.
/// </summary>
public sealed record WardTaxHouseholdDto(
    long VendorId,
    string DisplayName,
    string OwnerName,
    string? Phone,
    string? Address,
    string? BusinessLine,
    string VendorType,
    string Status,
    IReadOnlyList<string> SlotCodes,
    string? PermitStatus,
    DateTime ApprovedAt,
    DateTime? LastInspectionAt,
    DateTime? LastOrderAt,
    bool HasTaxCode,
    string TaxCodeMasked,
    bool EInvoiceRegistered,
    bool NoticeFiled,
    bool SharesRevenue,
    decimal? Revenue,
    string? Group);

public sealed record WardTaxHouseholdsDto(
    int TaxYear,
    IReadOnlyList<WardTaxHouseholdDto> Households);

public sealed record TaxReferralDto(
    long ReferralId,
    string ReasonCode,
    string ReasonLabel,
    string Summary,
    long? VendorId,
    string? VendorName,
    DateTime DetectedAt,
    string Status,
    DateTime? HandledAt,
    string? HandledByName,
    string? AuthorityReference,
    string? HandlingNote)
{
    public static TaxReferralDto From(TaxReferralRow row) => new(
        row.ReferralId, row.ReasonCode, TaxReferralReasons.Label(row.ReasonCode), row.Summary, row.VendorId,
        row.VendorName, row.DetectedAt, row.Status, row.HandledAt, row.HandledByName, row.AuthorityReference,
        row.HandlingNote);
}

public sealed record TaxReferralRefreshDto(int Created, int AutoClosed, int Pending);

public sealed record ReportTaxReferralRequest(string? Reference, string? Note);

public sealed record DismissTaxReferralRequest(string Reason);

public sealed record TaxAnnouncementDto(
    long AnnouncementId,
    string Segment,
    int TaxYear,
    string Title,
    string Body,
    int RecipientCount,
    string? CreatedByName,
    DateTime CreatedAt)
{
    public static TaxAnnouncementDto From(TaxAnnouncementRow row) => new(
        row.AnnouncementId, row.Segment, row.TaxYear, row.Title, row.Body, row.RecipientCount, row.CreatedByName,
        row.CreatedAt);
}

public sealed record SendTaxAnnouncementRequest(string Segment, int? TaxYear, string Title, string Body);

#endregion
