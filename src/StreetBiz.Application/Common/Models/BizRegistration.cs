namespace StreetBiz.Application.Common.Models;

/// <summary>Application-facing projection of a business registration.</summary>
public sealed record BizRegistration(
    long RegistrationId,
    long VendorId,
    string VendorType,
    string DisplayName,
    string? DeclaredAddress,
    decimal? AddressLatitude,
    decimal? AddressLongitude,
    int WardUnitId,
    string RegistrationStatus,
    bool FastTrackFlag,
    string? ReviewDecisionReason,
    DateTime? ReviewedAt,
    DateTime CreatedAt,
    DateTime? UpdatedAt);

/// <summary>Fields supplied by a vendor to create or update a registration.</summary>
public sealed record NewBizRegistration(
    string VendorType,
    string DisplayName,
    string? DeclaredAddress,
    decimal? AddressLatitude,
    decimal? AddressLongitude,
    int WardUnitId);

/// <summary>Application-facing projection of an uploaded evidence document.</summary>
public sealed record BizRegistrationEvidence(
    long EvidenceId,
    long RegistrationId,
    string EvidenceType,
    string FileUrl,
    DateTime UploadedAt);

/// <summary>Fields supplied to attach an evidence document.</summary>
public sealed record NewRegistrationEvidence(
    string EvidenceType,
    string FileUrl,
    string? OcrExtractedData);
