using System.Data;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using StreetBiz.Application.Common.Models;
using StreetBiz.Application.Common.Security;

namespace StreetBiz.Infrastructure.Persistence.Repositories;

/// <summary>ADM-02: suspend and reactivate Customer/Vendor accounts.</summary>
public sealed partial class PlatformAdministrationRepository
{
    private const string StorefrontOpen = "OPEN";
    private const int HistoryLimit = 20;

    public async Task<PlatformPage<PlatformAccountRow>> ListAccountsAsync(
        string? role,
        string? status,
        string? search,
        int page,
        int pageSize,
        CancellationToken cancellationToken)
    {
        var query = db.UserAccounts.AsNoTracking().Where(user =>
            user.role_code == RoleCodes.Customer || user.role_code == RoleCodes.Vendor);
        if (role is not null)
        {
            query = query.Where(user => user.role_code == role);
        }

        if (status is not null)
        {
            query = query.Where(user => user.account_status == status);
        }

        if (search is not null)
        {
            var like = $"%{EscapeLike(search)}%";
            query = query.Where(user =>
                EF.Functions.Like(user.phone_number, like, "\\")
                || (user.full_name != null && EF.Functions.Like(user.full_name, like, "\\")));
        }

        var total = await query.CountAsync(cancellationToken);
        var items = await query
            .OrderByDescending(user => user.created_at)
            .ThenByDescending(user => user.user_id)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(AccountProjection)
            .ToListAsync(cancellationToken);
        return new PlatformPage<PlatformAccountRow>(items, page, pageSize, total);
    }

    public async Task<PlatformAccountDetailRow?> GetAccountAsync(
        long userId,
        CancellationToken cancellationToken)
    {
        var account = await db.UserAccounts.AsNoTracking()
            .Where(user => user.user_id == userId
                && (user.role_code == RoleCodes.Customer || user.role_code == RoleCodes.Vendor))
            .Select(AccountProjection)
            .SingleOrDefaultAsync(cancellationToken);
        if (account is null)
        {
            return null;
        }

        var events = await db.AuditLogs.AsNoTracking()
            .Where(log => log.entity_type == AccountAuditActions.EntityType
                && log.entity_id == userId
                && (log.action == AccountAuditActions.Suspend || log.action == AccountAuditActions.Reactivate))
            .OrderByDescending(log => log.created_at)
            .ThenByDescending(log => log.audit_id)
            .Take(HistoryLimit)
            .Select(log => new { log.action, ActorName = log.actor_user.full_name, log.actor_user_id, log.details, log.created_at })
            .ToListAsync(cancellationToken);
        var history = events
            .Select(log => new PlatformAccountEventRow(
                log.action,
                log.ActorName ?? $"Quản trị viên #{log.actor_user_id}",
                ReadReason(log.details),
                log.created_at))
            .ToArray();
        return new PlatformAccountDetailRow(account, history);
    }

    public async Task<AccountStatusChangeResult> ChangeAccountStatusAsync(
        long userId,
        string targetStatus,
        string expectedStatus,
        string? reason,
        long actorUserId,
        CancellationToken cancellationToken)
    {
        var suspending = targetStatus == AccountStatuses.Suspended;
        var strategy = db.Database.CreateExecutionStrategy();
        var outcome = await strategy.ExecuteAsync(async () =>
        {
            db.ChangeTracker.Clear();
            await using var transaction = await db.Database.BeginTransactionAsync(
                IsolationLevel.Serializable, cancellationToken);
            var user = await db.UserAccounts.SingleOrDefaultAsync(
                row => row.user_id == userId, cancellationToken);
            if (user is null)
            {
                return AccountStatusChangeOutcome.NotFound;
            }

            // Officers and administrators are outside the platform administrator's remit
            // (permits, contracts and fees belong to the Ward Authority).
            if (user.role_code is not (RoleCodes.Customer or RoleCodes.Vendor))
            {
                return AccountStatusChangeOutcome.RoleNotManageable;
            }

            if (user.account_status != expectedStatus || user.account_status == targetStatus)
            {
                return AccountStatusChangeOutcome.Conflict;
            }

            var revokedSessions = 0;
            var pausedStorefronts = 0;
            user.account_status = targetStatus;
            user.updated_at = Now;
            if (suspending)
            {
                var sessions = await db.UserSessions
                    .Where(session => session.user_id == userId && session.revoked_at == null)
                    .ToListAsync(cancellationToken);
                foreach (var session in sessions)
                {
                    session.revoked_at = Now;
                }

                revokedSessions = sessions.Count;

                // Discovery already hides a suspended seller; pausing the open storefronts as well
                // means reactivating the account never silently puts anything back on sale.
                var storefronts = await db.Storefronts
                    .Where(storefront => storefront.registration.vendor.user_id == userId
                        && storefront.availability_status == StorefrontOpen)
                    .ToListAsync(cancellationToken);
                foreach (var storefront in storefronts)
                {
                    storefront.availability_status = StorefrontPaused;
                    storefront.updated_at = Now;
                }

                pausedStorefronts = storefronts.Count;
            }

            AddNotification(
                userId,
                "ACCOUNT_STATUS",
                suspending ? "Tài khoản đã bị tạm khoá" : "Tài khoản đã được mở khoá",
                suspending
                    ? $"Tài khoản của bạn bị tạm khoá. Lý do: {reason}"
                    : "Tài khoản của bạn đã được mở khoá. Điểm bán đang tạm dừng cho tới khi bạn mở lại.",
                AccountAuditActions.EntityType,
                userId);
            Audit(
                actorUserId,
                suspending ? AccountAuditActions.Suspend : AccountAuditActions.Reactivate,
                AccountAuditActions.EntityType,
                userId,
                new { reason, expectedStatus, revokedSessions, pausedStorefronts });
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return AccountStatusChangeOutcome.Updated;
        });

        var detail = outcome == AccountStatusChangeOutcome.Updated
            ? await GetAccountAsync(userId, cancellationToken)
            : null;
        return new AccountStatusChangeResult(outcome, detail);
    }

    private static readonly System.Linq.Expressions.Expression<Func<ScaffoldedModels.UserAccount, PlatformAccountRow>>
        AccountProjection = user => new PlatformAccountRow(
            user.user_id,
            user.full_name,
            user.phone_number,
            user.role_code,
            user.account_status,
            user.created_at,
            user.UserSessions.Max(session => (DateTime?)session.created_at),
            user.UserSessions.Count(session => session.revoked_at == null),
            user.Vendor == null
                ? 0
                : user.Vendor.BusinessRegistrations.Count(registration => registration.Storefront != null));

    private static string EscapeLike(string value) =>
        value.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_").Replace("[", "\\[");

    private static string? ReadReason(string? details)
    {
        if (string.IsNullOrWhiteSpace(details))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(details);
            return document.RootElement.TryGetProperty("reason", out var reason)
                && reason.ValueKind == JsonValueKind.String
                    ? reason.GetString()
                    : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
