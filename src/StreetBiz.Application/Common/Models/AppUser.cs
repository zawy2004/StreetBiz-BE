namespace StreetBiz.Application.Common.Models;

/// <summary>Application-facing projection of a user account (keeps handlers free of EF types).</summary>
public sealed record AppUser(
    long Id,
    string PhoneNumber,
    string PasswordHash,
    string? FullName,
    string RoleCode,
    int? WardUnitId,
    string AccountStatus,
    DateTime? PhoneVerifiedAt);

/// <summary>Data required to create a new user account.</summary>
public sealed record NewUser(
    string PhoneNumber,
    string PasswordHash,
    string? FullName,
    string RoleCode,
    int? WardUnitId,
    string AccountStatus,
    DateTime PhoneVerifiedAtUtc);

/// <summary>Application-facing projection of an authentication session.</summary>
public sealed record AppSession(
    long Id,
    long UserId,
    string? DeviceInfo,
    string? IpAddress,
    DateTime CreatedAt,
    DateTime? LastActiveAt,
    DateTime ExpiresAt,
    DateTime? RevokedAt);
