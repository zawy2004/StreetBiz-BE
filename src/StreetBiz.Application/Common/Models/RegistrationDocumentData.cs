namespace StreetBiz.Application.Common.Models;

/// <summary>
/// Everything Mẫu số 01 (Phụ lục II, Thông tư 68/2025/TT-BTC — "GIẤY ĐỀ NGHỊ ĐĂNG KÝ HỘ KINH
/// DOANH") needs, already resolved by the caller (vendor-side or ward-side query handler) so
/// <see cref="IRegistrationDocumentGenerator"/> stays a pure formatter with no DB access of its
/// own -- the same content built from the same data regardless of which role downloads it.
/// </summary>
public sealed record RegistrationDocumentData(
    long RegistrationId,
    string WardName,
    string OwnerFullName,
    string DisplayName,
    string VendorType,
    string? DeclaredAddress,
    string RegistrationStatus,
    DateOnly? OwnerDateOfBirth,
    string? OwnerGender,
    string? OwnerEthnicity,
    string? OwnerNationality,
    string? IdNumber,
    string? IdType,
    DateOnly? IdIssuedDate,
    string? IdIssuedPlace,
    string? PermanentAddress,
    string? ContactAddress,
    string? BusinessLine,
    string? BusinessLineCode,
    decimal? CapitalAmount,
    int? LaborCount,
    DateOnly? PlannedStartDate,
    DateTime? FoodSafetyCommitmentAt,
    IReadOnlyList<RegistrationDocumentHouseholdMember> HouseholdMembers,
    /// <summary>Null unless RegistrationStatus is APPROVED -- printed only once a decision exists.</summary>
    DateTime? ReviewedAt,
    /// <summary>May be null even when approved (e.g. printed from the vendor's own copy, which
    /// does not need the reviewing officer's name -- that accountability already lives in
    /// AuditLogs per BR-46). When known (ward-side download), it is shown.</summary>
    string? ReviewedByName);

public sealed record RegistrationDocumentHouseholdMember(
    string FullName,
    DateOnly? DateOfBirth,
    string? IdNumber,
    string? RelationshipToOwner,
    decimal? CapitalContribution);

public interface IRegistrationDocumentGenerator
{
    /// <summary>`format` is "docx" or "pdf" (validated by the caller's request DTO/validator).</summary>
    Task<(byte[] Content, string FileName)> GenerateAsync(RegistrationDocumentData data, string format, CancellationToken ct);
}
