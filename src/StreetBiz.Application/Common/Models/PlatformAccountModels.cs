namespace StreetBiz.Application.Common.Models;

/// <summary>ADM-02: one Customer or Vendor account as the platform administrator sees it.</summary>
public sealed record PlatformAccountRow(
    long UserId,
    string? FullName,
    string PhoneNumber,
    string RoleCode,
    string AccountStatus,
    DateTime CreatedAt,
    DateTime? LastLoginAt,
    int ActiveSessions,
    int StorefrontCount);

/// <summary>One suspend/reactivate decision, read back from the audit trail.</summary>
public sealed record PlatformAccountEventRow(
    string Action,
    string ActorName,
    string? Reason,
    DateTime CreatedAt);

public sealed record PlatformAccountDetailRow(
    PlatformAccountRow Account,
    IReadOnlyList<PlatformAccountEventRow> History);

public enum AccountStatusChangeOutcome
{
    Updated,
    NotFound,
    Conflict,
    RoleNotManageable,
}

public sealed record AccountStatusChangeResult(
    AccountStatusChangeOutcome Outcome,
    PlatformAccountDetailRow? Account);
