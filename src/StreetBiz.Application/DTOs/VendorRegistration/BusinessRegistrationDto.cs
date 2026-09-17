namespace StreetBiz.Application.DTOs.VendorRegistration;

public sealed record BusinessRegistrationDto(
    long RegistrationId,
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

public sealed record RegistrationEvidenceDto(
    long EvidenceId,
    long RegistrationId,
    string EvidenceType,
    string FileUrl,
    DateTime UploadedAt);

public sealed record BusinessRegistrationDetailDto(
    BusinessRegistrationDto Registration,
    IReadOnlyList<RegistrationEvidenceDto> Evidence);
