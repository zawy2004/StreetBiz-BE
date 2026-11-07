using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using StreetBiz.Application.Common.Interfaces;
using StreetBiz.Infrastructure.Persistence.ScaffoldedModels;

namespace StreetBiz.Infrastructure.Persistence.Repositories;

/// <summary>
/// Receiving accounts (PayeeAccounts). The channel's API key and checksum key go through
/// <see cref="ICredentialProtector"/> on the way in and out; the database never sees them in clear.
/// </summary>
public sealed class PayeeAccountRepository(StreetBizDbContext db, ICredentialProtector protector) : IPayeeAccountRepository
{
    /// <summary>Notifications.notification_type for anything about a receiving account.</summary>
    public const string NotificationType = "PAYMENT_ACCOUNT";

    private const string WardAuthority = "WARD_AUTHORITY";

    public async Task<PayeeAccountRow?> GetForVendorAsync(long vendorId, CancellationToken cancellationToken) =>
        ToRow(await db.PayeeAccounts.AsNoTracking().SingleOrDefaultAsync(a => a.vendor_id == vendorId, cancellationToken));

    public async Task<PayeeAccountRow?> GetForWardAsync(int wardId, CancellationToken cancellationToken) =>
        ToRow(await db.PayeeAccounts.AsNoTracking().SingleOrDefaultAsync(a => a.ward_unit_id == wardId, cancellationToken));

    public async Task<PayeeAccountRow> SaveAsync(
        SavePayeeAccount account, long userId, DateTime now, CancellationToken cancellationToken)
    {
        var existing = account.OwnerType == PayeeOwnerTypes.Vendor
            ? await db.PayeeAccounts.SingleOrDefaultAsync(a => a.vendor_id == account.VendorId, cancellationToken)
            : await db.PayeeAccounts.SingleOrDefaultAsync(a => a.ward_unit_id == account.WardUnitId, cancellationToken);
        var row = existing ?? new PayeeAccount
        {
            owner_type = account.OwnerType,
            vendor_id = account.OwnerType == PayeeOwnerTypes.Vendor ? account.VendorId : null,
            ward_unit_id = account.OwnerType == PayeeOwnerTypes.Ward ? account.WardUnitId : null,
            provider = "PAYOS",
            created_by = userId,
            created_at = now,
        };

        row.client_id = account.ClientId.Trim();
        row.api_key_protected = protector.Protect(account.ApiKey.Trim());
        row.checksum_key_protected = protector.Protect(account.ChecksumKey.Trim());
        row.webhook_token = account.WebhookToken;
        row.bank_name = Truncate(account.Channel.BankName, 100);
        row.account_number = Truncate(account.Channel.AccountNumber, 50);
        row.account_name = Truncate(account.Channel.AccountName, 150);
        row.status = PayeeAccountStatuses.Active;
        row.last_error = null;
        row.webhook_confirmed_at = account.Channel.WebhookConfirmed ? now : null;
        row.verified_at = now;
        row.last_checked_at = now;
        if (existing is null)
        {
            db.PayeeAccounts.Add(row);
        }
        else
        {
            row.updated_at = now;
        }

        await db.SaveChangesAsync(cancellationToken);
        Audit(userId, existing is null ? "PAYEE_ACCOUNT_CONNECTED" : "PAYEE_ACCOUNT_RECONNECTED", row, now);
        await db.SaveChangesAsync(cancellationToken);
        return ToRow(row)!;
    }

    public async Task<bool> DisableAsync(long payeeAccountId, long userId, DateTime now, CancellationToken cancellationToken)
    {
        var row = await db.PayeeAccounts.SingleOrDefaultAsync(a => a.payee_account_id == payeeAccountId, cancellationToken);
        if (row is null || row.status == PayeeAccountStatuses.Disabled)
        {
            return false;
        }

        row.status = PayeeAccountStatuses.Disabled;
        row.updated_at = now;
        Audit(userId, "PAYEE_ACCOUNT_DISCONNECTED", row, now);
        await db.SaveChangesAsync(cancellationToken);
        return true;
    }

    public async Task<PayeeCredentials?> GetCredentialsAsync(long payeeAccountId, CancellationToken cancellationToken)
    {
        var row = await db.PayeeAccounts.AsNoTracking()
            .SingleOrDefaultAsync(a => a.payee_account_id == payeeAccountId, cancellationToken);
        return row is null ? null : Credentials(row);
    }

    public async Task<(PayeeAccountRow Account, PayeeCredentials Credentials)?> FindByWebhookTokenAsync(
        string webhookToken, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(webhookToken) || webhookToken.Length > 64)
        {
            return null;
        }

        var row = await db.PayeeAccounts.AsNoTracking()
            .SingleOrDefaultAsync(a => a.webhook_token == webhookToken, cancellationToken);
        return row is null ? null : (ToRow(row)!, Credentials(row));
    }

    public async Task<bool> MarkErrorAsync(long payeeAccountId, string error, DateTime now, CancellationToken cancellationToken)
    {
        var row = await db.PayeeAccounts.SingleOrDefaultAsync(a => a.payee_account_id == payeeAccountId, cancellationToken);
        if (row is null || row.status != PayeeAccountStatuses.Active)
        {
            return false;
        }

        row.status = PayeeAccountStatuses.Error;
        row.last_error = Truncate(error, 500);
        row.last_checked_at = now;
        row.updated_at = now;

        var (title, body) = row.owner_type == PayeeOwnerTypes.Ward
            ? ("Tài khoản nhận phí thuê ô cần kết nối lại",
                "payOS từ chối khoá của tài khoản nhận tiền phí thuê ô của phường. Hộ kinh doanh tạm thời không trả phí " +
                "online được cho tới khi phường kết nối lại trong mục Thu phí → Tài khoản nhận tiền.")
            : ("Tài khoản nhận tiền đơn hàng cần kết nối lại",
                "payOS từ chối khoá của tài khoản nhận tiền của quầy. Khách tạm thời không đặt và trả online được " +
                "cho tới khi bạn kết nối lại trong mục Tài chính → Tài khoản nhận tiền.");
        foreach (var userId in await OwnerUserIdsAsync(row, cancellationToken))
        {
            db.Notifications.Add(new Notification
            {
                user_id = userId,
                notification_type = NotificationType,
                title = title,
                body = body,
                related_entity_type = "PayeeAccount",
                related_entity_id = row.payee_account_id,
                is_read = false,
                sent_at = now,
            });
        }

        await db.SaveChangesAsync(cancellationToken);
        return true;
    }

    public async Task MarkCheckedAsync(
        long payeeAccountId, PayeeChannelInfo channel, DateTime now, CancellationToken cancellationToken)
    {
        var row = await db.PayeeAccounts.SingleOrDefaultAsync(a => a.payee_account_id == payeeAccountId, cancellationToken);
        if (row is null || row.status == PayeeAccountStatuses.Disabled)
        {
            return;
        }

        row.status = PayeeAccountStatuses.Active;
        row.last_error = null;
        row.last_checked_at = now;
        row.verified_at = now;
        row.bank_name = Truncate(channel.BankName, 100) ?? row.bank_name;
        row.account_number = Truncate(channel.AccountNumber, 50) ?? row.account_number;
        row.account_name = Truncate(channel.AccountName, 150) ?? row.account_name;
        if (channel.WebhookConfirmed)
        {
            row.webhook_confirmed_at = now;
        }

        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<PayeeSummary?> GetForFeeItemAsync(long feeItemId, CancellationToken cancellationToken)
    {
        var ward = await db.FeeScheduleItems.AsNoTracking()
            .Where(i => i.fee_item_id == feeItemId)
            .Select(i => new
            {
                WardId = i.fee_schedule.contract.slot.zone.ward_unit_id,
            })
            .SingleOrDefaultAsync(cancellationToken);
        return ward is null ? null : await WardSummaryAsync(ward.WardId, cancellationToken);
    }

    public async Task<PayeeSummary?> GetForStorefrontAsync(long storefrontId, CancellationToken cancellationToken)
    {
        var store = await db.Storefronts.AsNoTracking()
            .Where(s => s.storefront_id == storefrontId)
            .Select(s => new { s.storefront_name, VendorId = s.registration.vendor_id })
            .SingleOrDefaultAsync(cancellationToken);
        return store is null ? null : await VendorSummaryAsync(store.VendorId, store.storefront_name, cancellationToken);
    }

    public async Task<PayeeSummary?> GetForCartAsync(long cartId, long customerUserId, CancellationToken cancellationToken)
    {
        var storefrontId = await db.ShoppingCarts.AsNoTracking()
            .Where(c => c.cart_id == cartId && c.customer_user_id == customerUserId)
            .Select(c => (long?)c.storefront_id)
            .SingleOrDefaultAsync(cancellationToken);
        return storefrontId is { } id ? await GetForStorefrontAsync(id, cancellationToken) : null;
    }

    // ------------------------------------------------------------------------------- helpers

    private async Task<PayeeSummary?> WardSummaryAsync(int wardId, CancellationToken cancellationToken)
    {
        var account = await db.PayeeAccounts.AsNoTracking().SingleOrDefaultAsync(a => a.ward_unit_id == wardId, cancellationToken);
        if (account is null)
        {
            return null;
        }

        var wardName = await db.AdministrativeUnits.AsNoTracking()
            .Where(u => u.unit_id == wardId)
            .Select(u => u.unit_name)
            .SingleOrDefaultAsync(cancellationToken);
        return Summary(account, wardName ?? "Phường");
    }

    private async Task<PayeeSummary?> VendorSummaryAsync(long vendorId, string storeName, CancellationToken cancellationToken)
    {
        var account = await db.PayeeAccounts.AsNoTracking().SingleOrDefaultAsync(a => a.vendor_id == vendorId, cancellationToken);
        return account is null ? null : Summary(account, storeName);
    }

    private static PayeeSummary Summary(PayeeAccount account, string displayName) => new(
        account.payee_account_id,
        account.owner_type,
        displayName,
        account.bank_name,
        account.account_number is null ? null : PayeeMasks.Tail(account.account_number),
        account.account_name,
        account.status == PayeeAccountStatuses.Active);

    /// <summary>The household's own account, or every active officer of the ward.</summary>
    private async Task<IReadOnlyList<long>> OwnerUserIdsAsync(PayeeAccount account, CancellationToken cancellationToken)
    {
        if (account.vendor_id is { } vendorId)
        {
            return await db.Vendors.AsNoTracking()
                .Where(v => v.vendor_id == vendorId)
                .Select(v => v.user_id)
                .ToListAsync(cancellationToken);
        }

        return await db.UserAccounts.AsNoTracking()
            .Where(u => u.ward_unit_id == account.ward_unit_id && u.role_code == WardAuthority && u.account_status == "ACTIVE")
            .Select(u => u.user_id)
            .ToListAsync(cancellationToken);
    }

    private PayeeCredentials Credentials(PayeeAccount row) => new(
        row.payee_account_id,
        row.client_id,
        protector.Unprotect(row.api_key_protected),
        protector.Unprotect(row.checksum_key_protected));

    private void Audit(long userId, string action, PayeeAccount row, DateTime now) =>
        db.AuditLogs.Add(new AuditLog
        {
            actor_user_id = userId,
            action = action,
            entity_type = "PayeeAccount",
            entity_id = row.payee_account_id,
            // Never the keys: only what identifies the channel.
            details = JsonSerializer.Serialize(new
            {
                ownerType = row.owner_type,
                vendorId = row.vendor_id,
                wardUnitId = row.ward_unit_id,
                clientId = PayeeMasks.Tail(row.client_id),
                accountNumber = PayeeMasks.Tail(row.account_number),
            }),
            created_at = now,
        });

    private static PayeeAccountRow? ToRow(PayeeAccount? row) => row is null ? null : new PayeeAccountRow(
        row.payee_account_id,
        row.owner_type,
        row.vendor_id,
        row.ward_unit_id,
        row.client_id,
        row.bank_name,
        row.account_number,
        row.account_name,
        row.webhook_token,
        row.status,
        row.last_error,
        row.webhook_confirmed_at,
        row.verified_at,
        row.last_checked_at,
        row.created_at,
        row.updated_at);

    private static string? Truncate(string? value, int length) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Length <= length ? value.Trim() : value.Trim()[..length];
}
