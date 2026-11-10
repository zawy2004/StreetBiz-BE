namespace StreetBiz.Application.DTOs.Community;

public sealed record ActiveVendorLocationDto(
    long VendorId,
    string DisplayName,
    string VendorType,
    string? Address,
    long PermitId,
    DateOnly PermitEndDate,
    long SlotId,
    string SlotCode,
    string ZoneName,
    decimal Latitude,
    decimal Longitude,
    double? DistanceMeters,
    decimal? CommunityRating,
    int CommunityCount,
    decimal? VerifiedRating,
    int VerifiedCount);

public sealed record VendorCommentDto(
    long CommentId,
    string AuthorName,
    short? Rating,
    string? CommentText,
    DateTime CreatedAt);

public sealed record PublicVendorProfileDto(
    long VendorId,
    string DisplayName,
    string VendorType,
    string? Address,
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
    int VerifiedCount,
    IReadOnlyList<VendorCommentDto> Comments);

/// <param name="PriceBoard">The stall's price board, read live (BR-39). Only when the permit is VALID,
/// and only when the stall keeps one on StreetBiz; null otherwise.</param>
public sealed record PermitVerificationDto(
    bool IsValid,
    string Status,
    long? PermitId,
    long? VendorId,
    string? DisplayName,
    long? SlotId,
    string? SlotCode,
    decimal? Latitude,
    decimal? Longitude,
    DateOnly? ValidFrom,
    DateOnly? ValidUntil,
    PublicPriceBoardDto? PriceBoard = null);

/// <param name="Version">"Bản số N": grows by one with every change to the board.</param>
/// <param name="PrintedVersionIsCurrent">Whether the paper board scanned (its <c>?v=</c>) is this
/// version; null when the scan carried no version.</param>
public sealed record PublicPriceBoardDto(
    int Version,
    DateTime? UpdatedAt,
    bool? PrintedVersionIsCurrent,
    IReadOnlyList<PriceBoardItemDto> Items);

public sealed record PriceBoardItemDto(
    long MenuItemId,
    string Name,
    string? NameEn,
    string PriceUnit,
    decimal UnitPrice);

/// <param name="PostedAmount">For an overcharge: the posted price at purchase x quantity, computed
/// by the server.</param>
public sealed record VendorReportReceiptDto(
    long ReportId,
    string Status,
    string ReportType = "GENERAL",
    DateTime? DueAt = null,
    decimal? PostedAmount = null,
    decimal? ChargedAmount = null);

/// <summary>The price evidence of an overcharge report (BUY-07), as the ward and the reporter see it.</summary>
/// <param name="PriceVersion">The board version ("bản số") the posted price comes from.</param>
public sealed record VendorReportPriceEvidenceDto(
    long MenuItemId,
    string ItemName,
    string PriceUnit,
    decimal PostedUnitPrice,
    int PriceVersion,
    DateTime PriceVersionAt,
    int Quantity,
    decimal PostedAmount,
    decimal ChargedAmount,
    decimal Difference,
    DateTime PurchasedAt);

/// <summary>"Phản ánh của tôi": the caller's own report and how the ward closed it.</summary>
public sealed record MyVendorReportDto(
    long ReportId,
    long? VendorId,
    string? VendorName,
    string? SlotCode,
    string ReportType,
    string Reason,
    string Status,
    DateTime CreatedAt,
    DateTime? DueAt,
    bool IsOverdue,
    string? Resolution,
    string? ResolutionNote,
    decimal? RefundAmount,
    DateTime? ReviewedAt,
    bool HasPhoto,
    VendorReportPriceEvidenceDto? PriceEvidence);
