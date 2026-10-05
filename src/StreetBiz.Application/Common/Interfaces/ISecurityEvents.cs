namespace StreetBiz.Application.Common.Interfaces;

/// <summary>One entry in an account's security history (sign-ins, lockouts, password changes, revoked sessions).</summary>
public sealed record SecurityEvent(long Id, string Action, string? Details, DateTime CreatedAt);

/// <summary>Action codes written by <see cref="ISecurityEvents"/>.</summary>
public static class SecurityActions
{
    public const string LoginSuccess = "LOGIN_SUCCESS";
    public const string LoginFailed = "LOGIN_FAILED";
    public const string AccountLocked = "ACCOUNT_LOCKED";
    public const string PasswordChanged = "PASSWORD_CHANGED";
    public const string PasswordReset = "PASSWORD_RESET";
    public const string SessionRevoked = "SESSION_REVOKED";
    public const string OtherSessionsRevoked = "OTHER_SESSIONS_REVOKED";

    public static readonly string[] All =
    [
        LoginSuccess, LoginFailed, AccountLocked, PasswordChanged, PasswordReset, SessionRevoked, OtherSessionsRevoked,
    ];
}

/// <summary>
/// Audit trail for authentication events (BR-46) and, for the events a person should hear about,
/// an in-app notification. Kept apart from the handlers so each one stays a few lines.
/// </summary>
public interface ISecurityEvents
{
    /// <param name="notification">When set, also tells the user (title, body) in their notification list.</param>
    Task RecordAsync(
        long userId,
        string action,
        string? details,
        (string Title, string Body)? notification,
        CancellationToken cancellationToken);

    /// <summary>Newest first; <paramref name="beforeId"/> pages backwards.</summary>
    Task<IReadOnlyList<SecurityEvent>> ListAsync(
        long userId, int take, long? beforeId, CancellationToken cancellationToken);
}
