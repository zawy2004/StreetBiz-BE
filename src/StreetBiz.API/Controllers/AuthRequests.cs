namespace StreetBiz.API.Controllers;

public sealed record SendOtpRequest(string PhoneNumber, string Purpose);
public sealed record RegisterRequest(
    string PhoneNumber, string Password, string? FullName, string RoleCode, int? WardUnitId, string Otp);
public sealed record LoginRequest(string PhoneNumber, string Password);
public sealed record RefreshRequest(string RefreshToken);
public sealed record ChangePasswordRequest(string CurrentPassword, string NewPassword);
public sealed record ForgotPasswordRequest(string PhoneNumber);
public sealed record ResetPasswordRequest(string PhoneNumber, string Otp, string NewPassword);
