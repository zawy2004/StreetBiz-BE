namespace StreetBiz.Application.Common.Models;

public sealed record AccessTokenResult(string Token, DateTime ExpiresAtUtc);

public sealed record RefreshTokenResult(string RawToken, byte[] Hash, DateTime ExpiresAtUtc);
