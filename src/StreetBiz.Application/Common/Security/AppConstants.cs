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
}

/// <summary>Application messages mirrored from SRS Report 3 (MSGxx).</summary>
public static class AppMessages
{
    public const string OtpSent = "A verification code has been sent to your phone number.";
    public const string OtpIncorrect = "The verification code is incorrect. Please try again.";
    public const string OtpExpired = "The verification code has expired. Please request a new one.";
    public const string SignInSuccess = "You have signed in successfully.";
    public const string SessionExpired = "Your session has expired. Please sign in again.";
    public const string Forbidden = "You do not have permission to access this resource.";
    public const string InvalidCredentials = "Invalid phone number or password.";
    public const string PhoneAlreadyRegistered = "This phone number is already registered. Please sign in instead.";
    public const string SignOutSuccess = "You have signed out successfully.";
    public const string SessionRevoked = "The selected session has been signed out.";
    public const string CannotRevokeCurrent = "You cannot revoke the session you are currently using. Use Sign Out instead.";
    public const string PasswordResetSent = "A password reset code has been sent to your phone number.";
    public const string PasswordResetSuccess = "Your password has been reset successfully. Please sign in.";
    public const string CurrentPasswordIncorrect = "The current password you entered is incorrect.";
    public const string OtpCooldown = "Please wait before requesting another verification code.";
    public const string OtpLocked = "Too many failed verification attempts. Please try again later.";
    public const string AccountSuspended = "This account is suspended. Please contact support.";
    public const string InvalidWard = "Please select a valid ward.";
}

/// <summary>AdministrativeUnits.unit_type values. `ward_unit_type` is a computed 'WARD' column.</summary>
public static class AdministrativeUnitTypes
{
    public const string Ward = "WARD";
}
