using Microsoft.EntityFrameworkCore;
using StreetBiz.Application.Common.Interfaces;
using static StreetBiz.Infrastructure.Tests.PayeeTestFixture;

namespace StreetBiz.Infrastructure.Tests;

/// <summary>Receiving accounts on SQLite: one per owner, keys encrypted, disconnect keeps them, errors tell the owner.</summary>
public sealed class PayeeAccountRepositoryTests
{
    [Fact]
    public async Task A_connected_account_keeps_its_keys_encrypted_and_gives_them_back_for_a_request()
    {
        using var f = await Create();

        var account = await f.ConnectVendorAsync();

        var stored = await f.Db.PayeeAccounts.AsNoTracking().SingleAsync();
        Assert.DoesNotContain("api-7", stored.api_key_protected);
        Assert.DoesNotContain("vendor-checksum", stored.checksum_key_protected);
        var credentials = await f.Payees.GetCredentialsAsync(account.PayeeAccountId, default);
        Assert.Equal(new PayeeCredentials(account.PayeeAccountId, "client-7", "api-7", "vendor-checksum"), credentials);
        Assert.Equal(PayeeAccountStatuses.Active, account.Status);
        Assert.Equal(Now.UtcDateTime, account.WebhookConfirmedAt);
        Assert.Equal("MB Bank", account.BankName);
        // The audit trail names the channel without its keys.
        var audit = await f.Db.AuditLogs.AsNoTracking().SingleAsync();
        Assert.Equal("PAYEE_ACCOUNT_CONNECTED", audit.action);
        Assert.DoesNotContain("api-7", audit.details);
        Assert.DoesNotContain("checksum", audit.details);
    }

    [Fact]
    public async Task Credentials_never_show_their_keys_when_printed()
    {
        var text = new PayeeCredentials(4, "client-123456", "secret-api", "secret-checksum").ToString();

        Assert.DoesNotContain("secret-api", text);
        Assert.DoesNotContain("secret-checksum", text);
        Assert.DoesNotContain("client-123456", text);
    }

    [Fact]
    public async Task Reconnecting_replaces_the_keys_in_place_and_reactivates_the_account()
    {
        using var f = await Create();
        var first = await f.ConnectWardAsync();
        await f.Payees.DisableAsync(first.PayeeAccountId, Officer, Now.UtcDateTime, default);

        var second = await f.ConnectWardAsync(checksum: "new-checksum", token: "token-ward-2");

        Assert.Equal(first.PayeeAccountId, second.PayeeAccountId);
        Assert.Equal(PayeeAccountStatuses.Active, second.Status);
        Assert.Equal("token-ward-2", second.WebhookToken);
        Assert.Equal("new-checksum", (await f.Payees.GetCredentialsAsync(second.PayeeAccountId, default))!.ChecksumKey);
        Assert.Equal(1, await f.Db.PayeeAccounts.CountAsync());
        Assert.Equal(
            ["PAYEE_ACCOUNT_CONNECTED", "PAYEE_ACCOUNT_DISCONNECTED", "PAYEE_ACCOUNT_RECONNECTED"],
            await f.Db.AuditLogs.AsNoTracking().OrderBy(a => a.audit_id).Select(a => a.action).ToListAsync());
    }

    [Fact]
    public async Task A_disconnected_account_still_verifies_late_webhooks_by_its_token()
    {
        using var f = await Create();
        var account = await f.ConnectVendorAsync(token: "late-token");
        Assert.True(await f.Payees.DisableAsync(account.PayeeAccountId, VendorUser, Now.UtcDateTime, default));
        Assert.False(await f.Payees.DisableAsync(account.PayeeAccountId, VendorUser, Now.UtcDateTime, default));

        var found = await f.Payees.FindByWebhookTokenAsync("late-token", default);

        Assert.NotNull(found);
        Assert.Equal(PayeeAccountStatuses.Disabled, found!.Value.Account.Status);
        Assert.Equal("vendor-checksum", found.Value.Credentials.ChecksumKey);
        Assert.Null(await f.Payees.FindByWebhookTokenAsync("someone-else", default));
        Assert.Null(await f.Payees.FindByWebhookTokenAsync(new string('x', 65), default));
    }

    [Fact]
    public async Task Refused_keys_put_the_account_in_error_once_and_tell_every_officer_of_the_ward()
    {
        using var f = await Create();
        var account = await f.ConnectWardAsync();
        f.Db.UserAccounts.Add(new StreetBiz.Infrastructure.Persistence.ScaffoldedModels.UserAccount
        {
            user_id = 30, phone_number = "0900000030", password_hash = "x", full_name = "Officer 30",
            role_code = "WARD_AUTHORITY", ward_unit_id = Ward, account_status = "ACTIVE",
        });
        await f.Db.SaveChangesAsync();

        Assert.True(await f.Payees.MarkErrorAsync(account.PayeeAccountId, "401", Now.UtcDateTime, default));
        Assert.False(await f.Payees.MarkErrorAsync(account.PayeeAccountId, "401", Now.UtcDateTime, default));

        var row = await f.Payees.GetForWardAsync(Ward, default);
        Assert.Equal(PayeeAccountStatuses.Error, row!.Status);
        Assert.Equal("401", row.LastError);
        var told = await f.Db.Notifications.AsNoTracking().Select(n => n.user_id).OrderBy(id => id).ToListAsync();
        Assert.Equal([Officer, 30L], told); // not the officer of the other ward

        await f.Payees.MarkCheckedAsync(account.PayeeAccountId, new PayeeChannelInfo(null, null, null, true), Now.UtcDateTime, default);
        Assert.Equal(PayeeAccountStatuses.Active, (await f.Payees.GetForWardAsync(Ward, default))!.Status);
    }

    [Fact]
    public async Task A_household_in_error_is_told_once_through_its_own_account()
    {
        using var f = await Create();
        var account = await f.ConnectVendorAsync();

        await f.Payees.MarkErrorAsync(account.PayeeAccountId, "401", Now.UtcDateTime, default);

        var notice = await f.Db.Notifications.AsNoTracking().SingleAsync();
        Assert.Equal(VendorUser, notice.user_id);
        Assert.Equal("PAYMENT_ACCOUNT", notice.notification_type);
    }

    [Fact]
    public async Task A_fee_is_paid_to_the_ward_of_its_slot_and_an_order_to_the_household_of_its_storefront()
    {
        using var f = await Create();
        Assert.Null(await f.Payees.GetForFeeItemAsync(FeeItem, default));
        var ward = await f.ConnectWardAsync();
        await f.ConnectWardAsync(OtherWard, "token-other-ward");
        var vendor = await f.ConnectVendorAsync();

        var feePayee = await f.Payees.GetForFeeItemAsync(FeeItem, default);
        var storePayee = await f.Payees.GetForStorefrontAsync(Storefront, default);
        var cartPayee = await f.Payees.GetForCartAsync(Cart, Customer, default);

        Assert.Equal(ward.PayeeAccountId, feePayee!.PayeeAccountId);
        Assert.Equal("Phường Hải Châu", feePayee.DisplayName);
        Assert.Equal("••••••6655", feePayee.AccountNumberMasked);
        Assert.True(feePayee.Active);
        Assert.Equal(vendor.PayeeAccountId, storePayee!.PayeeAccountId);
        Assert.Equal("Bánh mì Cô Lan", storePayee.DisplayName);
        Assert.Equal(vendor.PayeeAccountId, cartPayee!.PayeeAccountId);
        Assert.Null(await f.Payees.GetForStorefrontAsync(OtherStorefront, default)); // household 8 has not connected
        Assert.Null(await f.Payees.GetForCartAsync(Cart, VendorUser, default)); // not this user's cart
        Assert.Null(await f.Payees.GetForFeeItemAsync(999, default));
    }

    [Fact]
    public async Task The_payee_and_the_link_are_recorded_on_an_open_transaction_only()
    {
        using var f = await Create();
        var vendor = await f.ConnectVendorAsync();
        f.Db.PaymentTransactions.AddRange(
            new StreetBiz.Infrastructure.Persistence.ScaffoldedModels.PaymentTransaction
            {
                transaction_id = 50, idempotency_key = "k-50", payment_purpose = "RENTAL_FEE", fee_item_id = FeeItem,
                provider = "PAYOS", amount = 1_040_000m, transaction_status = "PENDING", created_at = Now.UtcDateTime,
            },
            new StreetBiz.Infrastructure.Persistence.ScaffoldedModels.PaymentTransaction
            {
                transaction_id = 51, idempotency_key = "k-51", payment_purpose = "RENTAL_FEE", fee_item_id = FeeItem,
                provider = "PAYOS", amount = 1_040_000m, transaction_status = "FAILED", created_at = Now.UtcDateTime,
            });
        await f.Db.SaveChangesAsync();

        await f.Transactions.AttachPayeeAsync(50, vendor.PayeeAccountId, default);
        await f.Transactions.AttachPayeeAsync(51, vendor.PayeeAccountId, default);
        await f.Transactions.SaveCheckoutAsync(50, "50001", "{\"attempt\":1}", default);
        await f.Transactions.SaveCheckoutAsync(50, "50002", "{\"attempt\":2}", default);

        var open = await f.Transactions.GetAsync(50, default);
        var closed = await f.Transactions.GetAsync(51, default);
        Assert.Equal(vendor.PayeeAccountId, open!.PayeeAccountId);
        Assert.Equal("50002", open.ProviderReference);
        Assert.Equal("{\"attempt\":2}", open.CheckoutPayload);
        Assert.Null(closed!.PayeeAccountId);
    }
}
