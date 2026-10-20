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
    DateTime? UpdatedAt,
    // ---- Chủ hộ kinh doanh (Mẫu số 01 Phụ lục II, Thông tư 68/2025/TT-BTC) ----
    DateOnly? OwnerDateOfBirth,
    string? OwnerGender,
    string? OwnerEthnicity,
    string? OwnerNationality,
    string? IdType,
    DateOnly? IdIssuedDate,
    string? IdIssuedPlace,
    string? PermanentAddress,
    string? ContactAddress,
    // ---- Ngành nghề, quy mô hộ kinh doanh ----
    string? BusinessLine,
    string? BusinessLineCode,
    decimal? CapitalAmount,
    int? LaborCount,
    DateOnly? PlannedStartDate,
    // ---- Cam kết ATTP / xác minh danh tính thủ công ----
    DateTime? FoodSafetyCommitmentAt,
    DateTime? IdentityVerifiedAt,
    string? IdentityVerificationNote,
    IReadOnlyList<HouseholdMemberDto> HouseholdMembers);

public sealed record HouseholdMemberDto(
    long MemberId,
    string FullName,
    DateOnly? DateOfBirth,
    string? IdNumber,
    string? RelationshipToOwner,
    decimal? CapitalContribution);

public sealed record RegistrationEvidenceDto(
    long EvidenceId,
    long RegistrationId,
    string EvidenceType,
    string FileUrl,
    DateTime UploadedAt);

public sealed record BusinessRegistrationDetailDto(
    BusinessRegistrationDto Registration,
    IReadOnlyList<RegistrationEvidenceDto> Evidence);
