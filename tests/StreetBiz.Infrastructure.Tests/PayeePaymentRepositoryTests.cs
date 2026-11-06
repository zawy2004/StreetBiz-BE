using Microsoft.EntityFrameworkCore;
using StreetBiz.Application.Common.Models;
using StreetBiz.Application.Common.Security;
using StreetBiz.Infrastructure.Persistence.Repositories;
using StreetBiz.Infrastructure.Persistence.ScaffoldedModels;
using static StreetBiz.Infrastructure.Tests.PayeeTestFixture;

namespace StreetBiz.Infrastructure.Tests;

/// <summary>
/// A payment to a payee is settled only by that payee's own payOS channel: a webhook or status
/// answer from any other channel is refused, whatever it reports. Fees and orders alike.
/// </summary>
public sealed class PayeePaymentRepositoryTests
{
    private const string Raw = """{"code":"00","data":{"orderCode":42001,"reference":"FT-1"}}""";

    private static PaymentCallbackData Report(long? payeeAccountId, string reference = "PAYOS-42001", string eventId = "FT-1", decimal amount = 1_040_000m, string raw = Raw) =>
        new("PAYOS", reference, null, amount, "SUCCESS", raw, true, eventId) { PayeeAccountId = payeeAccountId };

    private static async Task<long> OpenFeePaymentAsync(PayeeTestFixture f, long? payeeAccountId, string status = "PENDING")
    {
        var payment = new PaymentTransaction
        {
            idempotency_key = $"key-{Guid.NewGuid():N}", payment_purpose = "RENTAL_FEE", fee_item_id = FeeItem, provider = "PAYOS",
            provider_reference = "PAYOS-42001", amount = 1_040_000m, transaction_status = status, created_at = Now.UtcDateTime,
            payee_account_id = payeeAccountId,
        };
        f.Db.PaymentTransactions.Add(payment);
        await f.Db.SaveChangesAsync();
        f.Db.ChangeTracker.Clear();
        return payment.transaction_id;
    }

    // ----------------------------------------------------------------------------------- fees

    [Fact]
    public async Task A_fee_is_settled_by_the_report_of_the_ward_it_pays()
    {
        using var f = await Create();
        var ward = await f.ConnectWardAsync();
        var transactionId = await OpenFeePaymentAsync(f, ward.PayeeAccountId);

        var result = await f.Finance().ApplyPaymentCallbackAsync(Report(ward.PayeeAccountId), default);

        Assert.Equal(PaymentCallbackOutcome.Applied, result.Outcome);
        var payment = await f.Db.PaymentTransactions.AsNoTracking().SingleAsync(p => p.transaction_id == transactionId);
        Assert.Equal("SUCCESS", payment.transaction_status);
        Assert.Equal("PAID", (await f.Db.FeeScheduleItems.AsNoTracking().SingleAsync(i => i.fee_item_id == FeeItem)).item_status);
        Assert.Equal(1, await f.Db.Invoices.CountAsync(i => i.fee_item_id == FeeItem));
    }

    [Fact]
    public async Task Another_channels_report_never_settles_the_fee_even_with_the_right_reference_and_amount()
    {
        using var f = await Create();
        var ward = await f.ConnectWardAsync();
        var vendor = await f.ConnectVendorAsync();
        var transactionId = await OpenFeePaymentAsync(f, ward.PayeeAccountId);

        var fromHousehold = await f.Finance().ApplyPaymentCallbackAsync(Report(vendor.PayeeAccountId), default);
        var fromNobody = await f.Finance().ApplyPaymentCallbackAsync(Report(null), default);

        Assert.Equal(PaymentCallbackOutcome.Rejected, fromHousehold.Outcome);
        Assert.Equal(PaymentCallbackOutcome.Rejected, fromNobody.Outcome);
        Assert.Equal("PENDING", (await f.Db.PaymentTransactions.AsNoTracking().SingleAsync(p => p.transaction_id == transactionId)).transaction_status);
        Assert.Equal("PENDING", (await f.Db.FeeScheduleItems.AsNoTracking().SingleAsync(i => i.fee_item_id == FeeItem)).item_status);
        Assert.Empty(f.Db.Invoices);
        // Both reports are kept as evidence.
        Assert.Equal(2, await f.Db.PaymentCallbackEvents.CountAsync(e => e.processing_result == "REJECTED"));
    }

    [Fact]
    public async Task A_report_naming_a_payee_cannot_settle_a_payment_that_has_none()
    {
        using var f = await Create();
        var ward = await f.ConnectWardAsync();
        await OpenFeePaymentAsync(f, null);

        var result = await f.Finance().ApplyPaymentCallbackAsync(Report(ward.PayeeAccountId), default);

        Assert.Equal(PaymentCallbackOutcome.Rejected, result.Outcome);
    }

    [Fact]
    public async Task The_same_bank_transfer_reported_twice_is_one_payment_and_a_second_transfer_is_surplus()
    {
        using var f = await Create();
        var ward = await f.ConnectWardAsync();
        await OpenFeePaymentAsync(f, ward.PayeeAccountId);
        var finance = f.Finance();
        await finance.ApplyPaymentCallbackAsync(Report(ward.PayeeAccountId), default);

        var sameTransfer = await finance.ApplyPaymentCallbackAsync(Report(ward.PayeeAccountId), default);
        // A status-API answer for the same transfer carries its reference in transactions[], not data.
        var sameFromStatusApi = await finance.ApplyPaymentCallbackAsync(
            Report(ward.PayeeAccountId, raw: """{"status":"PAID","transactions":[{"reference":"FT-1","amount":1040000}]}"""), default);
        var secondTransfer = await finance.ApplyPaymentCallbackAsync(
            Report(ward.PayeeAccountId, eventId: "FT-2", raw: """{"code":"00","data":{"orderCode":42001,"reference":"FT-2"}}"""), default);

        Assert.Equal(PaymentCallbackOutcome.Duplicate, sameTransfer.Outcome);
        Assert.Equal(PaymentCallbackOutcome.Duplicate, sameFromStatusApi.Outcome);
        Assert.Equal(PaymentCallbackOutcome.Surplus, secondTransfer.Outcome);
        Assert.Equal(1, await f.Db.Invoices.CountAsync());
    }

    [Fact]
    public async Task A_wrong_amount_is_refused_for_a_payee_too()
    {
        using var f = await Create();
        var ward = await f.ConnectWardAsync();
        await OpenFeePaymentAsync(f, ward.PayeeAccountId);

        var result = await f.Finance().ApplyPaymentCallbackAsync(Report(ward.PayeeAccountId, amount: 1_000_000m), default);

        Assert.Equal(PaymentCallbackOutcome.Rejected, result.Outcome);
    }

    [Fact]
    public async Task The_sweep_asks_about_a_stale_payment_with_its_payee_attached()
    {
        using var f = await Create();
        var ward = await f.ConnectWardAsync();
        var transactionId = await OpenFeePaymentAsync(f, ward.PayeeAccountId);

        var stale = await f.Finance().ListStalePaymentsAsync(Now.UtcDateTime.AddMinutes(5), default);

        var row = Assert.Single(stale);
        Assert.Equal(transactionId, row.TransactionId);
        Assert.Equal(ward.PayeeAccountId, row.PayeeAccountId);
    }

    // --------------------------------------------------------------------------------- orders

    private static async Task<long> OpenOrderPaymentAsync(PayeeTestFixture f, long? payeeAccountId)
    {
        var payment = new PaymentTransaction
        {
            idempotency_key = $"key-{Guid.NewGuid():N}", payment_purpose = "ORDER", order_id = OrderId, provider = "PAYOS",
            provider_reference = "PAYOS-77001", amount = 65_000m, transaction_status = "PENDING", created_at = Now.UtcDateTime,
            payee_account_id = payeeAccountId,
        };
        f.Db.PaymentTransactions.Add(payment);
        await f.Db.SaveChangesAsync();
        f.Db.ChangeTracker.Clear();
        return payment.transaction_id;
    }

    private static PaymentCallbackData OrderReport(long? payeeAccountId) =>
        new("PAYOS", "PAYOS-77001", null, 65_000m, "SUCCESS", """{"code":"00","data":{"orderCode":77001,"reference":"FT-9"}}""", true, "FT-9")
        {
            PayeeAccountId = payeeAccountId,
        };

    [Fact]
    public async Task An_order_is_placed_by_the_report_of_the_household_it_pays_and_by_no_other()
    {
        using var f = await Create();
        var vendor = await f.ConnectVendorAsync();
        var otherVendor = await f.ConnectVendorAsync(OtherVendor, "token-other");
        await OpenOrderPaymentAsync(f, vendor.PayeeAccountId);
        var commerce = new CommerceRepository(f.Db, f.Clock);

        var wrongChannel = await commerce.ApplyPaymentCallbackAsync(OrderReport(otherVendor.PayeeAccountId), default);
        Assert.Equal(PaymentCallbackOutcome.Rejected, wrongChannel.Outcome);
        Assert.Equal("PENDING_PAYMENT", (await f.Db.Orders.AsNoTracking().SingleAsync(o => o.order_id == OrderId)).order_status);

        var rightChannel = await commerce.ApplyPaymentCallbackAsync(OrderReport(vendor.PayeeAccountId), default);
        Assert.Equal(PaymentCallbackOutcome.Applied, rightChannel.Outcome);
        Assert.Equal("PLACED", (await f.Db.Orders.AsNoTracking().SingleAsync(o => o.order_id == OrderId)).order_status);
    }

    [Fact]
    public async Task The_order_sweep_lists_only_payee_payments_still_waiting()
    {
        using var f = await Create();
        var vendor = await f.ConnectVendorAsync();
        var waiting = await OpenOrderPaymentAsync(f, vendor.PayeeAccountId);
        f.Db.PaymentTransactions.Add(new PaymentTransaction
        {
            idempotency_key = "key-old-momo", payment_purpose = "ORDER", order_id = OrderId, provider = "MOMO",
            provider_reference = "SB-T1-aaaa", amount = 65_000m, transaction_status = "PENDING", created_at = Now.UtcDateTime,
        });
        await f.Db.SaveChangesAsync();

        var stale = await f.Transactions.ListStalePayeeOrderPaymentsAsync(Now.UtcDateTime.AddMinutes(5), default);
        var tooYoung = await f.Transactions.ListStalePayeeOrderPaymentsAsync(Now.UtcDateTime.AddMinutes(-5), default);

        Assert.Equal([waiting], stale.Select(s => s.TransactionId));
        Assert.Empty(tooYoung);
    }
}
