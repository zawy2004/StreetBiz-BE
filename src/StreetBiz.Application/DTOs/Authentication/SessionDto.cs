namespace StreetBiz.Application.DTOs.Authentication;

public sealed record SessionDto(
    long SessionId,
    string? DeviceInfo,
    string? IpAddress,
    DateTime CreatedAt,
    DateTime? LastActiveAt,
    DateTime ExpiresAt,
    bool IsCurrent);
