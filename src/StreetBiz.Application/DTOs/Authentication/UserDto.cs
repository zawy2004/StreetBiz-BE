namespace StreetBiz.Application.DTOs.Authentication;

public sealed record UserDto(
    long UserId,
    string PhoneNumber,
    string? FullName,
    string RoleCode,
    int? WardUnitId,
    string AccountStatus);
