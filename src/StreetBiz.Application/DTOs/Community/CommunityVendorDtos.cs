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
    DateOnly? ValidUntil);

public sealed record VendorReportReceiptDto(
    long ReportId,
    string Status);
