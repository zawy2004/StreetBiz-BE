namespace StreetBiz.Application.Common.Models;

public sealed record CommunityVendorSearchArea(
    decimal? MinLatitude,
    decimal? MaxLatitude,
    decimal? MinLongitude,
    decimal? MaxLongitude);

public sealed record ActiveVendorLocationRow(
    long VendorId,
    long RegistrationId,
    string DisplayName,
    string VendorType,
    string? DeclaredAddress,
    long PermitId,
    DateOnly PermitEndDate,
    long SlotId,
    string SlotCode,
    string ZoneName,
    decimal Latitude,
    decimal Longitude,
    decimal? CommunityRating,
    int CommunityCount,
    decimal? VerifiedRating,
    int VerifiedCount);

public sealed record PublicVendorProfileRow(
    long VendorId,
    long RegistrationId,
    string DisplayName,
    string VendorType,
    string? DeclaredAddress,
    int WardId,
    string? WardName,
    long PermitId,
    string PermitStatus,
    DateOnly PermitEndDate,
    long SlotId,
    string SlotCode,
    string ZoneName,
    decimal Latitude,
    decimal Longitude,
    decimal? CommunityRating,
    int CommunityCount,
    decimal? VerifiedRating,
    int VerifiedCount);

public sealed record PublicVendorCommentRow(
    long CommentId,
    long CustomerUserId,
    string AuthorName,
    short? Rating,
    string? CommentText,
    DateTime CreatedAt);

public sealed record PublicPermitVerificationRow(
    long PermitId,
    long ContractId,
    long VendorId,
    string DisplayName,
    long SlotId,
    string SlotCode,
    decimal Latitude,
    decimal Longitude,
    DateOnly StartDate,
    DateOnly EndDate,
    string EffectiveStatus);

public sealed record PublicPermitScan(
    long? PermitId,
    string QrPayload,
    long? ScannedBy,
    string Result,
    decimal? Latitude,
    decimal? Longitude,
    string? PhotoUrl);

public sealed record CustomerVendorComment(
    short Rating,
    string CommentText);

/// <param name="PostedAmount">Computed by the server from MenuItemPriceHistory at PurchasedAt, never
/// taken from the request (BUY-07).</param>
public sealed record CustomerVendorReport(
    string Reason,
    string? EvidenceUrl,
    long? SlotId,
    long? ScannedPermitId,
    string ReportType = "GENERAL",
    long? MenuItemId = null,
    long? PriceHistoryId = null,
    int? Quantity = null,
    decimal? PostedAmount = null,
    decimal? ChargedAmount = null,
    DateTime? PurchasedAt = null,
    DateTime? DueAt = null);

/// <summary>Who a report is about, enough to word the notices and route it to a ward.</summary>
/// <param name="DisplayName">The scanned permit's registration name, else the vendor's latest
/// approved (else latest) registration name.</param>
public sealed record ReportTargetRow(
    long VendorId,
    string DisplayName,
    string? SlotCode);

/// <summary>
/// One dish of a vendor and the MenuItemPriceHistory row in force at a given instant.
/// PriceHistoryId is null when the dish had no history row yet at that instant.
/// </summary>
public sealed record PostedPriceRow(
    long MenuItemId,
    long StorefrontId,
    string StorefrontName,
    string? SlotCode,
    long? PriceHistoryId,
    string ItemName,
    string? PriceUnit,
    decimal? UnitPrice,
    bool IsListed,
    DateTime? PriceChangedAt);

public sealed record NotificationText(string Title, string Body);

/// <summary>The notices a new report sends: to the ward's officers and to the vendor.</summary>
public sealed record NewVendorReportNotices(NotificationText ForWard, NotificationText ForVendor);

/// <summary>A storefront's price board as it stands: the dishes on it and its version.</summary>
/// <param name="Version">"Bản số N": the storefront's count of price-history rows.</param>
public sealed record PriceBoardRow(
    long StorefrontId,
    string StorefrontName,
    string? SlotCode,
    long ContractId,
    int Version,
    DateTime? UpdatedAt,
    IReadOnlyList<PriceBoardItemRow> Items);

public sealed record PriceBoardItemRow(
    long MenuItemId,
    string Name,
    string? NameEn,
    string PriceUnit,
    decimal UnitPrice);

/// <summary>The price evidence frozen on an overcharge report, with the board version it came from.</summary>
public sealed record VendorReportPriceEvidenceRow(
    long MenuItemId,
    string ItemName,
    string PriceUnit,
    decimal PostedUnitPrice,
    int PriceVersion,
    DateTime PriceVersionAt,
    int Quantity,
    decimal PostedAmount,
    decimal ChargedAmount,
    DateTime PurchasedAt);

/// <summary>One of the caller's own reports ("Phản ánh của tôi").</summary>
public sealed record MyVendorReportRow(
    long ReportId,
    long? VendorId,
    string? VendorName,
    string? SlotCode,
    string ReportType,
    string Reason,
    string Status,
    DateTime CreatedAt,
    DateTime? DueAt,
    string? Resolution,
    string? ResolutionNote,
    decimal? RefundAmount,
    DateTime? ReviewedAt,
    bool HasPhoto,
    VendorReportPriceEvidenceRow? PriceEvidence);
