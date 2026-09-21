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
    DateTime? UpdatedAt,
    // ---- Chủ hộ kinh doanh (Mẫu số 01 Phụ lục II, Thông tư 68/2025/TT-BTC) ----
    // Defaulted to null so pre-existing call sites (older tests, callers built before this
    // feature) keep compiling; SubmitRegistrationCommandValidator is what actually requires
    // these on a real submission.
    DateOnly? OwnerDateOfBirth = null,
    string? OwnerGender = null,
    string? OwnerEthnicity = null,
    string? OwnerNationality = null,
    string? IdType = null,
    DateOnly? IdIssuedDate = null,
    string? IdIssuedPlace = null,
    string? PermanentAddress = null,
    string? ContactAddress = null,
    // ---- Ngành nghề, quy mô hộ kinh doanh ----
    string? BusinessLine = null,
    string? BusinessLineCode = null,
    decimal? CapitalAmount = null,
    int? LaborCount = null,
    DateOnly? PlannedStartDate = null,
    // ---- Cam kết ATTP / xác minh danh tính thủ công ----
    DateTime? FoodSafetyCommitmentAt = null,
    long? IdentityVerifiedBy = null,
    DateTime? IdentityVerifiedAt = null,
    string? IdentityVerificationNote = null,
    IReadOnlyList<BizHouseholdMember>? HouseholdMembers = null)
{
    /// <summary>Never null even when the caller omitted it — always enumerate through this.</summary>
    public IReadOnlyList<BizHouseholdMember> HouseholdMembersOrEmpty => HouseholdMembers ?? [];

    /// <summary>
    /// BR-41 KYC gate: an officer must have manually confirmed the vendor's identity against
    /// their physical/chip CCCD before an enrollment may be APPROVEd. AI-OCR alone
    /// (id_number/AiComplianceService) only reads and self-compares an uploaded photo -- it
    /// never queries the Bo Cong an/CSDL quoc gia ve dan cu -- so it cannot satisfy this gate.
    /// </summary>
    public bool IdentityVerified => IdentityVerifiedAt.HasValue;
}

/// <summary>A household member co-registering the hộ kinh doanh (Mẫu số 01's "Thành viên hộ gia đình cùng góp vốn").</summary>
public sealed record BizHouseholdMember(
    long MemberId,
    string FullName,
    DateOnly? DateOfBirth,
    string? IdNumber,
    string? RelationshipToOwner,
    decimal? CapitalContribution);

/// <summary>Fields supplied for a household member when creating/updating a registration.</summary>
public sealed record NewHouseholdMember(
    string FullName,
    DateOnly? DateOfBirth,
    string? IdNumber,
    string? RelationshipToOwner,
    decimal? CapitalContribution);

/// <summary>Fields supplied by a vendor to create or update a registration.</summary>
public sealed record NewBizRegistration(
    string VendorType,
    string DisplayName,
    string? DeclaredAddress,
    decimal? AddressLatitude,
    decimal? AddressLongitude,
    int WardUnitId,
    // ---- Chủ hộ kinh doanh ----
    DateOnly? OwnerDateOfBirth = null,
    string? OwnerGender = null,
    string? OwnerEthnicity = null,
    string? OwnerNationality = null,
    string? IdType = null,
    DateOnly? IdIssuedDate = null,
    string? IdIssuedPlace = null,
    string? PermanentAddress = null,
    string? ContactAddress = null,
    // ---- Ngành nghề, quy mô hộ kinh doanh ----
    string? BusinessLine = null,
    string? BusinessLineCode = null,
    decimal? CapitalAmount = null,
    int? LaborCount = null,
    DateOnly? PlannedStartDate = null,
    // ---- Cam kết an toàn thực phẩm: true chỉ khi vendor vừa tích chọn ở bước này;
    // không bao giờ dùng để xoá một cam kết đã ghi nhận trước đó (giống BiometricConsent). ----
    bool FoodSafetyCommitment = false,
    IReadOnlyList<NewHouseholdMember>? HouseholdMembers = null)
{
    public IReadOnlyList<NewHouseholdMember> HouseholdMembersOrEmpty => HouseholdMembers ?? [];
}

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
