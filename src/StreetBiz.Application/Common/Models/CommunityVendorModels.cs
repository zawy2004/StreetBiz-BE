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

public sealed record CustomerVendorReport(
    string Reason,
    string? EvidenceUrl,
    long? SlotId,
    long? ScannedPermitId);
