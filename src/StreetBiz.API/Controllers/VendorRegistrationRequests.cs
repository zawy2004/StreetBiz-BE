using StreetBiz.Application.Common.Models;

namespace StreetBiz.API.Controllers;

public sealed record HouseholdMemberRequest(
    string? FullName,
    DateOnly? DateOfBirth,
    string? IdNumber,
    string? RelationshipToOwner,
    decimal? CapitalContribution)
{
    public NewHouseholdMember ToModel() => new(
        FullName ?? string.Empty, DateOfBirth, IdNumber, RelationshipToOwner, CapitalContribution);
}

public sealed record SubmitRegistrationRequest(
    string? VendorType,
    string? DisplayName,
    string? DeclaredAddress,
    decimal? AddressLatitude,
    decimal? AddressLongitude,
    int? WardUnitId,
    DateOnly? OwnerDateOfBirth = null,
    string? OwnerGender = null,
    string? OwnerEthnicity = null,
    string? OwnerNationality = null,
    string? IdType = null,
    DateOnly? IdIssuedDate = null,
    string? IdIssuedPlace = null,
    string? PermanentAddress = null,
    string? ContactAddress = null,
    string? BusinessLine = null,
    string? BusinessLineCode = null,
    decimal? CapitalAmount = null,
    int? LaborCount = null,
    DateOnly? PlannedStartDate = null,
    bool FoodSafetyCommitment = false,
    IReadOnlyList<HouseholdMemberRequest>? HouseholdMembers = null);

public sealed record UpdateRegistrationRequest(
    string? VendorType,
    string? DisplayName,
    string? DeclaredAddress,
    decimal? AddressLatitude,
    decimal? AddressLongitude,
    int? WardUnitId,
    DateOnly? OwnerDateOfBirth = null,
    string? OwnerGender = null,
    string? OwnerEthnicity = null,
    string? OwnerNationality = null,
    string? IdType = null,
    DateOnly? IdIssuedDate = null,
    string? IdIssuedPlace = null,
    string? PermanentAddress = null,
    string? ContactAddress = null,
    string? BusinessLine = null,
    string? BusinessLineCode = null,
    decimal? CapitalAmount = null,
    int? LaborCount = null,
    DateOnly? PlannedStartDate = null,
    bool FoodSafetyCommitment = false,
    IReadOnlyList<HouseholdMemberRequest>? HouseholdMembers = null);

public sealed record SubmitEvidenceRequest(
    string? EvidenceType,
    string? FileUrl,
    string? OcrExtractedData,
    bool BiometricConsent = false);
