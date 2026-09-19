namespace StreetBiz.Application.Common.Security;

/// <summary>Role codes seeded in the Roles table. Adjust if the DB seed differs.</summary>
public static class RoleCodes
{
    public const string Customer = "CUSTOMER";
    public const string Vendor = "VENDOR";
    public const string WardAuthority = "WARD_AUTHORITY";
    public const string PlatformAdmin = "PLATFORM_ADMIN";

    /// <summary>Roles a Guest may self-register as (BR-02).</summary>
    public static readonly string[] SelfRegisterable = [Customer, Vendor];
}

/// <summary>UserAccounts.account_status values (max 20 chars).</summary>
public static class AccountStatuses
{
    public const string Active = "ACTIVE";
    public const string Suspended = "SUSPENDED";
    public const string Deactivated = "DEACTIVATED";
}

/// <summary>OtpChallenges.purpose values (max 20 chars).</summary>
public static class OtpPurposes
{
    public const string Signup = "REGISTRATION";
    public const string PasswordReset = "PASSWORD_RESET";

    /// <summary>AUTH-03 passwordless sign-in (FE-01, BR-03: "phone/OTP").</summary>
    public const string Login = "LOGIN";
}

/// <summary>
/// User-facing messages, mirrored from the SRS Report 3 message table (MSGxx) but
/// written in Vietnamese: every reader of this API is a Vietnamese vendor, customer
/// or ward officer, and the SPA shows `detail` verbatim. Machine-readable error codes
/// (AppException.ErrorCode: validation_error, conflict, …) stay English.
/// </summary>
public static class AppMessages
{
    public const string OtpSent = "Mã xác thực đã được gửi tới số điện thoại của bạn.";                    // MSG01
    public const string OtpIncorrect = "Mã xác thực không đúng. Vui lòng thử lại.";                        // MSG02
    public const string OtpExpired = "Mã xác thực đã hết hạn. Vui lòng yêu cầu mã mới.";                   // MSG03
    public const string SignInSuccess = "Đăng nhập thành công.";                                           // MSG04
    public const string SessionExpired = "Phiên đăng nhập đã hết hạn. Vui lòng đăng nhập lại.";            // MSG05
    public const string Forbidden = "Bạn không có quyền thực hiện thao tác này.";                          // MSG06
    public const string InvalidPhone = "Số điện thoại phải gồm 10 số, bắt đầu bằng 0.";                    // MSG08
    public const string InvalidCredentials = "Số điện thoại hoặc mật khẩu không đúng.";
    public const string PhoneAlreadyRegistered = "Số điện thoại này đã được đăng ký. Vui lòng đăng nhập.";
    public const string SignOutSuccess = "Đã đăng xuất.";
    public const string SessionRevoked = "Đã đăng xuất thiết bị đã chọn.";
    public const string CannotRevokeCurrent = "Không thể đăng xuất phiên đang dùng. Hãy dùng chức năng Đăng xuất.";
    public const string PasswordResetSent = "Mã đặt lại mật khẩu đã được gửi tới số điện thoại của bạn.";
    public const string PasswordResetSuccess = "Đặt lại mật khẩu thành công. Vui lòng đăng nhập.";
    public const string PasswordChanged = "Đổi mật khẩu thành công. Các thiết bị khác đã được đăng xuất.";
    public const string CurrentPasswordIncorrect = "Mật khẩu hiện tại không đúng.";
    public const string OtpCooldown = "Vui lòng chờ trước khi yêu cầu mã xác thực mới.";
    public const string OtpLocked = "Nhập sai mã quá nhiều lần. Vui lòng yêu cầu mã mới và thử lại sau.";
    public const string AccountSuspended = "Tài khoản đã bị tạm khoá. Vui lòng liên hệ hỗ trợ.";
    public const string InvalidWard = "Vui lòng chọn phường/xã hợp lệ.";

    // Field-level validation messages (MSG07 "{field} is required" family).
    public const string PasswordRequired = "Vui lòng nhập mật khẩu.";
    public const string PasswordPolicy = "Mật khẩu cần tối thiểu 8 ký tự, gồm chữ hoa, chữ thường, chữ số và ký tự đặc biệt.";
    public const string NewPasswordPolicy = "Mật khẩu mới chưa đạt yêu cầu bảo mật.";
    public const string NewPasswordMustDiffer = "Mật khẩu mới phải khác mật khẩu hiện tại.";
    public const string CurrentPasswordRequired = "Vui lòng nhập mật khẩu hiện tại.";
    public const string OtpFormat = "Mã xác thực gồm 6 chữ số.";
    public const string RoleNotSelfRegisterable = "Chỉ có thể đăng ký vai trò Người mua hoặc Hộ kinh doanh.";
    public const string FullNameTooLong = "Họ tên tối đa 150 ký tự.";
    public const string RefreshTokenRequired = "Thiếu refresh token.";
    public const string UnsupportedOtpPurpose = "Mục đích mã xác thực không hợp lệ.";
    public const string SessionNotFound = "Không tìm thấy phiên đăng nhập.";
    public const string TooManyAttempts = "Bạn đã thử quá nhiều lần. Vui lòng chờ ít phút rồi thử lại.";
}

/// <summary>AdministrativeUnits.unit_type values. `ward_unit_type` is a computed 'WARD' column.</summary>
public static class AdministrativeUnitTypes
{
    public const string Ward = "WARD";
}
