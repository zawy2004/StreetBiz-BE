namespace StreetBiz.API.Controllers;

public sealed record SubmitRegistrationRequest(
    string VendorType,
    string DisplayName,
    string? DeclaredAddress,
    decimal? AddressLatitude,
    decimal? AddressLongitude,
    int WardUnitId);

public sealed record UpdateRegistrationRequest(
    string VendorType,
    string DisplayName,
    string? DeclaredAddress,
    decimal? AddressLatitude,
    decimal? AddressLongitude,
    int WardUnitId);

public sealed record SubmitEvidenceRequest(
    string EvidenceType,
    string FileUrl,
    string? OcrExtractedData);
