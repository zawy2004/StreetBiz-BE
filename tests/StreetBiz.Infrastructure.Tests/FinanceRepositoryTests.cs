using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.Extensions.Options;
using StreetBiz.Application.Common.Exceptions;
using StreetBiz.Application.Common.Models;
using StreetBiz.Application.Common.Security;
using StreetBiz.Application.Features.Finance;
using StreetBiz.Infrastructure.Payments;
using StreetBiz.Infrastructure.Persistence;
using StreetBiz.Infrastructure.Persistence.Repositories;
using StreetBiz.Infrastructure.Persistence.ScaffoldedModels;

namespace StreetBiz.Infrastructure.Tests;

/// <summary>
/// Contract 1 has a superseded revision (one leftover PENDING item) and a current revision with
/// four instalments: paid, past due, due within the reminder window, and due later. Contract 2
/// has no schedule yet. "Now" is 10:00 on 26/09/2026 in Đà Nẵng.
/// </summary>
public sealed class FinanceRepositoryTests
{
    private const long Vendor = 10;
    private const long SupersededItem = 1;
    private const long PastDueItem = 3;
    private const long DueSoonItem = 4;
    private static readonly DateTimeOffset MorningOf26th = new(2026, 9, 26, 3, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Retrying_a_checkout_with_the_same_key_replays_the_original_transaction()
    {
        using var f = await Fixture.Create();
        var repository = f.Repository();

        var first = await repository.CreateFeeCheckoutAsync(Vendor, PastDueItem, "MOMO", "key-1", default);
        var retry = await repository.CreateFeeCheckoutAsync(Vendor, PastDueItem, "MOMO", "key-1", default);

        Assert.Equal(first.TransactionId, retry.TransactionId);
        Assert.Equal(1, await f.Db.PaymentTransactions.CountAsync(t => t.idempotency_key == "key-1"));
    }

    [Fact]
    public async Task Reusing_a_key_for_a_different_checkout_is_a_conflict_not_a_server_error()
    {
        using var f = await Fixture.Create();
        var repository = f.Repository();
        await repository.CreateFeeCheckoutAsync(Vendor, PastDueItem, "MOMO", "key-1", default);

        await Assert.ThrowsAsync<ConflictException>(() =>
            repository.CreateFeeCheckoutAsync(Vendor, DueSoonItem, "MOMO", "key-1", default));
        await Assert.ThrowsAsync<ConflictException>(() =>
            repository.CreateFeeCheckoutAsync(Vendor, PastDueItem, "ZALOPAY", "key-1", default));
    }

    [Fact]
    public async Task A_second_attempt_on_an_instalment_already_paid_does_not_issue_a_second_invoice()
    {
        using var f = await Fixture.Create();
        var repository = f.Repository();
        var first = await repository.CreateFeeCheckoutAsync(Vendor, PastDueItem, "MOMO", "tab-1", default);
        var second = await repository.CreateFeeCheckoutAsync(Vendor, PastDueItem, "ZALOPAY", "tab-2", default);

        // The second provider took over: the first attempt was closed when it was opened.
        Assert.Equal(PaymentStatuses.Failed, await StatusOf(f, first.TransactionId));
        await repository.ConfirmSandboxSuccessAsync(Vendor, second.TransactionId, default);
        await Assert.ThrowsAsync<DomainRuleException>(() =>
            repository.CreateFeeCheckoutAsync(Vendor, PastDueItem, "MOMO", "tab-3", default));

        var invoices = await f.Db.Invoices.AsNoTracking().Where(i => i.fee_item_id == PastDueItem).ToListAsync();
        var invoice = Assert.Single(invoices);
        // Continues from the seeded HD-2026-000010; the 2025 sequence is a different year.
        Assert.Equal("HD-2026-000011", invoice.invoice_number);
    }

    [Fact]
    public async Task Paying_the_closed_first_attempt_after_the_instalment_was_paid_is_owed_back_not_invoiced()
    {
        // MoMo's page for the first attempt was left open in another tab and paid after ZaloPay settled.
        using var f = await Fixture.Create();
        var repository = f.Repository();
        var first = await repository.CreateFeeCheckoutAsync(Vendor, PastDueItem, "MOMO", "tab-1", default);
        await repository.SetPaymentProviderReferenceAsync(first.TransactionId, "SB-T1-aaaa1111", default);
        var second = await repository.CreateFeeCheckoutAsync(Vendor, PastDueItem, "ZALOPAY", "tab-2", default);
        await repository.ConfirmSandboxSuccessAsync(Vendor, second.TransactionId, default);

        var late = await repository.ApplyPaymentCallbackAsync(new PaymentCallbackData(
            "MOMO", "SB-T1-aaaa1111", "tab-1", first.Amount, PaymentStatuses.Success, "{}", true), default);

        Assert.Equal(PaymentCallbackOutcome.Applied, late.Outcome);
        Assert.Equal(PaymentStatuses.RefundRequired, await StatusOf(f, first.TransactionId));
        Assert.Single(await f.Db.Invoices.AsNoTracking().Where(i => i.fee_item_id == PastDueItem).ToListAsync());
        var notice = await f.Db.Notifications.AsNoTracking().SingleAsync(n => n.title == "Khoản thanh toán sẽ được hoàn lại");
        Assert.Equal(Vendor, notice.user_id);
        Assert.True(await f.Db.AuditLogs.AnyAsync(log => log.action == "FINANCE_PAYMENT_REFUND_REQUIRED"));

        // A repeat of the same report changes nothing.
        var again = await repository.ApplyPaymentCallbackAsync(new PaymentCallbackData(
            "MOMO", "SB-T1-aaaa1111", "tab-1", first.Amount, PaymentStatuses.Success, "{}", true), default);
        Assert.Equal(PaymentCallbackOutcome.Duplicate, again.Outcome);
    }

    [Fact]
    public async Task A_payment_closed_as_failed_is_still_applied_if_its_page_is_paid_and_nothing_else_was()
    {
        using var f = await Fixture.Create();
        var repository = f.Repository();
        var checkout = await repository.CreateFeeCheckoutAsync(Vendor, PastDueItem, "MOMO", "key-1", default);
        await repository.SetPaymentProviderReferenceAsync(checkout.TransactionId, "SB-T1-aaaa1111", default);
        Assert.True(await repository.ClosePendingPaymentAsync(checkout.TransactionId, default));

        var result = await repository.ApplyPaymentCallbackAsync(new PaymentCallbackData(
            "MOMO", "SB-T1-aaaa1111", "key-1", checkout.Amount, PaymentStatuses.Success, "{}", true), default);

        Assert.Equal(PaymentCallbackOutcome.Applied, result.Outcome);
        Assert.Equal(PaymentStatuses.Success, await StatusOf(f, checkout.TransactionId));
        Assert.Equal(FeeItemStatuses.Paid,
            (await f.Db.FeeScheduleItems.AsNoTracking().SingleAsync(i => i.fee_item_id == PastDueItem)).item_status);
        Assert.Single(await f.Db.Invoices.AsNoTracking().Where(i => i.fee_item_id == PastDueItem).ToListAsync());
    }

    [Fact]
    public async Task An_instalment_holds_one_open_payment_and_the_same_provider_gets_it_back()
    {
        using var f = await Fixture.Create();
        var repository = f.Repository();

        var first = await repository.CreateFeeCheckoutAsync(Vendor, PastDueItem, "MOMO", "tab-1", default);
        var again = await repository.CreateFeeCheckoutAsync(Vendor, PastDueItem, "MOMO", "tab-2", default);

        Assert.Equal(first.TransactionId, again.TransactionId);
        Assert.Equal("tab-1", again.IdempotencyKey);
        Assert.Equal(1, await f.Db.PaymentTransactions.CountAsync(t => t.fee_item_id == PastDueItem));
    }

    [Fact]
    public async Task Instalments_are_paid_oldest_first()
    {
        using var f = await Fixture.Create();
        var repository = f.Repository();

        // The instalment due 20/09 is unpaid, so the one due 28/09 cannot be opened yet.
        var ex = await Assert.ThrowsAsync<DomainRuleException>(() =>
            repository.CreateFeeCheckoutAsync(Vendor, DueSoonItem, "MOMO", "skip", default));
        Assert.Contains("kỳ 2", ex.Message);
        Assert.Contains("20/09/2026", ex.Message);

        await repository.ConfirmSandboxSuccessAsync(Vendor,
            (await repository.CreateFeeCheckoutAsync(Vendor, PastDueItem, "MOMO", "first", default)).TransactionId, default);
        var next = await repository.CreateFeeCheckoutAsync(Vendor, DueSoonItem, "MOMO", "second", default);
        Assert.True(next.TransactionId > 0);
    }

    [Fact]
    public async Task Stale_payments_are_listed_and_closed_but_only_once()
    {
        using var f = await Fixture.Create();
        var repository = f.Repository();
        var stale = await repository.CreateFeeCheckoutAsync(Vendor, PastDueItem, "MOMO", "stale", default);

        Assert.Empty(await repository.ListStalePaymentsAsync(MorningOf26th.UtcDateTime.AddHours(-1), default));
        var listed = Assert.Single(await repository.ListStalePaymentsAsync(MorningOf26th.UtcDateTime.AddHours(1), default));
        Assert.Equal(stale.TransactionId, listed.TransactionId);

        Assert.True(await repository.ClosePendingPaymentAsync(stale.TransactionId, default));
        Assert.False(await repository.ClosePendingPaymentAsync(stale.TransactionId, default));
        Assert.Equal(PaymentStatuses.Failed, await StatusOf(f, stale.TransactionId));
        Assert.Empty(await repository.ListStalePaymentsAsync(MorningOf26th.UtcDateTime.AddHours(1), default));
    }

    [Fact]
    public async Task Unbilled_contracts_are_found_and_billed_contracts_are_not()
    {
        using var f = await Fixture.Create();
        var repository = f.Repository();

        // Contract 2 is approved with no schedule; contract 1 has one with instalments.
        (await f.Db.RentalApplications.SingleAsync(a => a.application_id == 2)).reviewed_by = 1;
        await f.Db.SaveChangesAsync();
        var unbilled = Assert.Single(await repository.ListUnbilledContractsAsync(new DateOnly(2026, 9, 26), default));
        Assert.Equal(2, unbilled.ContractId);

        // An empty header (what the ward's approval wrote) is just as unbilled.
        f.Db.FeeSchedules.Add(new FeeSchedule
        {
            fee_schedule_id = 9, contract_id = 2, revision = 1, total_amount = 0, generated_at = MorningOf26th.UtcDateTime,
        });
        await f.Db.SaveChangesAsync();
        Assert.Equal(2, Assert.Single(await repository.ListUnbilledContractsAsync(new DateOnly(2026, 9, 26), default)).ContractId);
        // Not once the contract has ended.
        Assert.Empty(await repository.ListUnbilledContractsAsync(new DateOnly(2027, 1, 1), default));
    }

    [Fact]
    public async Task A_contract_that_ended_early_has_its_later_instalments_closed_and_the_vendor_told_once()
    {
        using var f = await Fixture.Create();
        var repository = f.Repository();
        // Returned on 24/09: the instalment due 20/09 was owed; those due 28/09 and 28/10 never began.
        var contract = await f.Db.RentalContracts.SingleAsync(c => c.contract_id == 1);
        contract.contract_status = ContractStatuses.Cancelled;
        contract.cancelled_at = new DateTime(2026, 9, 24, 4, 0, 0, DateTimeKind.Utc);
        await f.Db.SaveChangesAsync();
        f.Db.ChangeTracker.Clear();

        Assert.Equal(2, await repository.CancelInstalmentsAfterEarlyEndAsync(default));
        Assert.Equal(0, await repository.CancelInstalmentsAfterEarlyEndAsync(default));

        var statuses = await f.Db.FeeScheduleItems.AsNoTracking().Where(i => i.fee_schedule_id == 2)
            .OrderBy(i => i.fee_item_id).Select(i => i.item_status).ToListAsync();
        Assert.Equal(
            [FeeItemStatuses.Paid, FeeItemStatuses.Pending, FeeItemStatuses.Cancelled, FeeItemStatuses.Cancelled],
            statuses);
        Assert.Equal(1, await f.Db.Notifications.CountAsync(n => n.title == "Đã huỷ các kỳ phí sau khi kết thúc hợp đồng"));

        // The sweep neither reminds about them nor marks them overdue.
        await repository.RunFeeReminderSweepAsync(new DateOnly(2026, 9, 26), default);
        Assert.False(await f.Db.Notifications.AnyAsync(n => n.title == "Sắp đến hạn đóng phí"));
    }

    [Fact]
    public async Task Sandbox_confirm_is_refused_for_a_MoMo_transaction_once_MoMo_is_configured()
    {
        using var f = await Fixture.Create();
        var checkout = await f.Repository().CreateFeeCheckoutAsync(Vendor, PastDueItem, "MOMO", "real-momo", default);
        var withMomo = f.Repository(new PaymentGatewaySettings
        {
            Momo = new PaymentProviderSettings
            {
                ApiBaseUrl = "https://test-payment.momo.vn/v2/gateway/api", PartnerCode = "MOMO",
                AccessKey = "a", SecretKey = "s", ReturnUrl = "http://localhost/r", NotifyUrl = "http://localhost/n",
            },
        });

        await Assert.ThrowsAsync<DomainRuleException>(() =>
            withMomo.ConfirmSandboxSuccessAsync(Vendor, checkout.TransactionId, default));

        Assert.Equal(FeeItemStatuses.Pending,
            (await f.Db.FeeScheduleItems.AsNoTracking().SingleAsync(i => i.fee_item_id == PastDueItem)).item_status);
    }

    [Fact]
    public async Task A_refused_checkout_is_failed_only_if_no_provider_page_was_ever_opened_for_it()
    {
        using var f = await Fixture.Create();
        var repository = f.Repository();
        var neverOpened = await repository.CreateFeeCheckoutAsync(Vendor, PastDueItem, "MOMO", "k-never", default);
        await repository.AbandonUnopenedCheckoutAsync(neverOpened.TransactionId, default);
        // The first is closed, so the instalment has no open payment and a new one can be opened.
        var openedBefore = await repository.CreateFeeCheckoutAsync(Vendor, PastDueItem, "MOMO", "k-opened", default);
        await repository.SetPaymentProviderReferenceAsync(openedBefore.TransactionId, "SB-T2-aaaa1111", default);

        await repository.AbandonUnopenedCheckoutAsync(openedBefore.TransactionId, default);

        var statuses = await f.Db.PaymentTransactions.AsNoTracking()
            .ToDictionaryAsync(t => t.transaction_id, t => t.transaction_status);
        Assert.Equal(PaymentStatuses.Failed, statuses[neverOpened.TransactionId]);
        Assert.Equal(PaymentStatuses.Pending, statuses[openedBefore.TransactionId]);
    }

    [Fact]
    public async Task Paying_through_an_earlier_attempts_MoMo_page_still_settles_the_transaction()
    {
        // A retry replaced provider_reference with a newer orderId; the vendor then paid on the
        // first attempt's page. MoMo's IPN carries the old orderId plus our key in extraData.
        using var f = await Fixture.Create();
        var repository = f.Repository();
        var checkout = await repository.CreateFeeCheckoutAsync(Vendor, PastDueItem, "MOMO", "key-1", default);
        await repository.SetPaymentProviderReferenceAsync(checkout.TransactionId, "SB-T1-newer000", default);

        var result = await repository.ApplyPaymentCallbackAsync(new PaymentCallbackData(
            "MOMO", "SB-T1-older000", "key-1", checkout.Amount, PaymentStatuses.Success, "{}", true), default);

        Assert.Equal(PaymentCallbackOutcome.Applied, result.Outcome);
        Assert.Equal(FeeItemStatuses.Paid,
            (await f.Db.FeeScheduleItems.AsNoTracking().SingleAsync(i => i.fee_item_id == PastDueItem)).item_status);
        // The receipt and any reconciliation must name the order that was actually paid.
        Assert.Equal("SB-T1-older000",
            (await f.Db.PaymentTransactions.AsNoTracking().SingleAsync(t => t.transaction_id == checkout.TransactionId)).provider_reference);
    }

    [Fact]
    public async Task A_failure_on_an_abandoned_earlier_attempt_does_not_fail_the_newer_one()
    {
        using var f = await Fixture.Create();
        var repository = f.Repository();
        var checkout = await repository.CreateFeeCheckoutAsync(Vendor, PastDueItem, "MOMO", "key-1", default);
        await repository.SetPaymentProviderReferenceAsync(checkout.TransactionId, "SB-T1-newer000", default);

        var staleFailure = await repository.ApplyPaymentCallbackAsync(new PaymentCallbackData(
            "MOMO", "SB-T1-older000", "key-1", checkout.Amount, PaymentStatuses.Failed, "{}", true), default);
        var newerPaid = await repository.ApplyPaymentCallbackAsync(new PaymentCallbackData(
            "MOMO", "SB-T1-newer000", "key-1", checkout.Amount, PaymentStatuses.Success, "{}", true), default);

        Assert.Equal(PaymentCallbackOutcome.Rejected, staleFailure.Outcome);
        Assert.Equal(PaymentCallbackOutcome.Applied, newerPaid.Outcome);
        Assert.Equal(FeeItemStatuses.Paid,
            (await f.Db.FeeScheduleItems.AsNoTracking().SingleAsync(i => i.fee_item_id == PastDueItem)).item_status);
    }

    [Fact]
    public async Task A_failure_on_the_latest_attempt_still_fails_the_transaction()
    {
        using var f = await Fixture.Create();
        var repository = f.Repository();
        var checkout = await repository.CreateFeeCheckoutAsync(Vendor, PastDueItem, "MOMO", "key-1", default);
        await repository.SetPaymentProviderReferenceAsync(checkout.TransactionId, "SB-T1-latest00", default);

        var result = await repository.ApplyPaymentCallbackAsync(new PaymentCallbackData(
            "MOMO", "SB-T1-latest00", "key-1", checkout.Amount, PaymentStatuses.Failed, "{}", true), default);

        Assert.Equal(PaymentCallbackOutcome.Applied, result.Outcome);
        Assert.Equal(PaymentStatuses.Failed,
            (await f.Db.PaymentTransactions.AsNoTracking().SingleAsync(t => t.transaction_id == checkout.TransactionId)).transaction_status);
    }

    [Fact]
    public async Task A_receipt_names_the_ward_the_payer_the_business_and_the_instalment()
    {
        using var f = await Fixture.Create();

        var receipt = await f.Repository().GetInvoiceDocumentAsync(Vendor, 2, default);

        Assert.NotNull(receipt);
        Assert.Equal("HD-2026-000009", receipt!.InvoiceNumber);
        Assert.Equal("FEE", receipt.Kind);
        Assert.Equal("One", receipt.WardName);
        Assert.Equal("User 10", receipt.PayerName);
        Assert.Equal("0900000010", receipt.PayerPhone);
        Assert.Equal("Vendor", receipt.BusinessName);
        Assert.Equal("S-01", receipt.SlotCode);
        Assert.Equal("Kỳ 1/4 · Tháng 09/2026", receipt.PeriodLabel);
        Assert.Null(await f.Repository().GetInvoiceDocumentAsync(Vendor + 1, 2, default));
    }

    [Fact]
    public async Task The_ward_receipt_list_covers_Vietnamese_days_and_only_that_ward()
    {
        using var f = await Fixture.Create();
        var reports = new WardReportRepository(f.Db);

        // All three seeded receipts were issued at 10:00 on 01/09 in Đà Nẵng (03:00 UTC).
        var september1st = await reports.ListWardInvoicesAsync(1, new DateOnly(2026, 9, 1), new DateOnly(2026, 9, 1), default);
        var later = await reports.ListWardInvoicesAsync(1, new DateOnly(2026, 9, 2), new DateOnly(2026, 9, 30), default);
        var otherWard = await reports.ListWardInvoicesAsync(2, new DateOnly(2026, 9, 1), new DateOnly(2026, 9, 1), default);

        Assert.Equal(["HD-2025-000999", "HD-2026-000009", "HD-2026-000010"], september1st.Select(row => row.InvoiceNumber));
        Assert.All(september1st, row => Assert.Equal("Phí thuê ô S-01 — Kỳ 1/4 · Tháng 09/2026", row.Description));
        Assert.Empty(later);
        Assert.Empty(otherWard);
        Assert.Equal("One", await reports.GetWardNameAsync(1, default));
    }

    [Fact]
    public async Task Invoice_numbers_follow_the_Vietnamese_calendar_year()
    {
        // 01:00 on 1 January 2027 in Đà Nẵng is still 31 December 2026 in UTC.
        using var f = await Fixture.Create(new DateTimeOffset(2026, 12, 31, 18, 0, 0, TimeSpan.Zero));
        var repository = f.Repository();
        var checkout = await repository.CreateFeeCheckoutAsync(Vendor, PastDueItem, "MOMO", "new-year", default);

        await repository.ConfirmSandboxSuccessAsync(Vendor, checkout.TransactionId, default);

        Assert.True(await f.Db.Invoices.AnyAsync(i => i.invoice_number == "HD-2027-000001"));
    }

    [Fact]
    public async Task An_instalment_on_a_superseded_schedule_can_neither_be_paid_nor_swept()
    {
        using var f = await Fixture.Create();
        var repository = f.Repository();

        Assert.Null(await repository.GetFeeItemForCheckoutAsync(Vendor, SupersededItem, default));
        await Assert.ThrowsAsync<NotFoundException>(() =>
            repository.CreateFeeCheckoutAsync(Vendor, SupersededItem, "MOMO", "old", default));

        await repository.RunFeeReminderSweepAsync(new DateOnly(2026, 9, 26), default);

        var superseded = await f.Db.FeeScheduleItems.AsNoTracking().SingleAsync(i => i.fee_item_id == SupersededItem);
        Assert.Equal(FeeItemStatuses.Pending, superseded.item_status);
        Assert.False(await f.Db.Notifications.AnyAsync(n => n.related_entity_id == SupersededItem));
    }

    [Fact]
    public async Task The_reminder_sweep_sends_each_notice_once_per_Vietnamese_day()
    {
        using var f = await Fixture.Create();
        var repository = f.Repository();
        var today = new DateOnly(2026, 9, 26);

        var first = await repository.RunFeeReminderSweepAsync(today, default);
        var again = await repository.RunFeeReminderSweepAsync(today, default);

        Assert.Equal(new FeeReminderSweepResult(1, 1), first);
        Assert.Equal(new FeeReminderSweepResult(0, 0), again);
        Assert.Equal(FeeItemStatuses.Overdue,
            (await f.Db.FeeScheduleItems.AsNoTracking().SingleAsync(i => i.fee_item_id == PastDueItem)).item_status);

        var reminder = await f.Db.Notifications.AsNoTracking().SingleAsync(n => n.related_entity_id == DueSoonItem);
        Assert.Equal("Khoản phí 2.000.000 đ của ô S-01 sẽ đến hạn trong 2 ngày (28/09/2026).", reminder.body);
    }

    [Fact]
    public async Task Payment_history_names_the_instalment_not_just_the_slot()
    {
        using var f = await Fixture.Create();
        var repository = f.Repository();
        await repository.CreateFeeCheckoutAsync(Vendor, PastDueItem, "MOMO", "key-1", default);

        var row = Assert.Single(await repository.ListPaymentTransactionsAsync(Vendor, default));

        Assert.Equal("Kỳ 2/4 · Tháng 09/2026", row.ReferenceLabel);
        Assert.Equal("S-01", row.SlotCode);
    }

    [Fact]
    public async Task A_schedule_generated_inside_the_approvers_transaction_rolls_back_with_it()
    {
        using var f = await Fixture.Create();
        var repository = f.Repository();
        FeeInstalment[] instalments = [new(1, new DateOnly(2026, 10, 1), new DateOnly(2026, 10, 1), new DateOnly(2026, 10, 30), 750_000m)];

        await using (var approval = await f.Db.Database.BeginTransactionAsync())
        {
            var schedule = await repository.ReplaceFeeScheduleAsync(2, 1, 750_000m, instalments, default);
            Assert.Single(schedule.Items);
            await approval.RollbackAsync();
        }

        Assert.False(await f.Db.FeeSchedules.AsNoTracking().AnyAsync(s => s.contract_id == 2));
    }

    [Fact]
    public async Task Cancelling_a_contract_drops_future_pending_instalments_but_never_touches_paid_ones()
    {
        using var f = await Fixture.Create();
        var contracts = new RentalContractRepository(f.Db);

        // Both fee schedules (the superseded revision 1 and the current revision 2) belong to
        // contract 1 in this fixture -- contract 2 has no schedule at all.
        await contracts.CancelAsync(contractId: 1, cancelledByUserId: 1, reason: "Vendor returned the slot", default);

        var items = await f.Db.FeeScheduleItems.AsNoTracking()
            .Where(i => i.fee_schedule.contract_id == 1).ToDictionaryAsync(i => i.fee_item_id, i => i.item_status);
        Assert.Equal(FeeItemStatuses.Paid, items[2]); // already paid: never refunded, never touched
        Assert.Equal("CANCELLED", items[PastDueItem]); // was PENDING, future instalment: dropped
        Assert.Equal("CANCELLED", items[DueSoonItem]);
        Assert.Equal("CANCELLED", items[5]);

        // The superseded revision's leftover PENDING item is excluded by the same
        // superseded_at filter every other live-debt query in this file uses -- it is not
        // live debt, so cancelling must leave it alone.
        Assert.Equal(FeeItemStatuses.Pending,
            (await f.Db.FeeScheduleItems.AsNoTracking().SingleAsync(i => i.fee_item_id == SupersededItem)).item_status);
    }
    [Fact]
    public async Task A_second_sepay_transfer_for_a_paid_instalment_is_surplus_and_the_same_transfer_again_a_duplicate()
    {
        using var f = await Fixture.Create();
        var repository = f.Repository();
        var checkout = await repository.CreateFeeCheckoutAsync(Vendor, PastDueItem, "SEPAY", "qr-1", default);
        var code = $"SBP{checkout.TransactionId}";
        await repository.SetPaymentProviderReferenceAsync(checkout.TransactionId, code, default);

        Task<FinanceCallbackMutationResult> Transfer(long sePayId) =>
            repository.ApplyPaymentCallbackAsync(new PaymentCallbackData(
                "SEPAY", code, null, checkout.Amount, PaymentStatuses.Success,
                $$"""{"id":{{sePayId}},"content":"{{code}}","transferType":"in"}""", true, sePayId.ToString()), default);

        Assert.Equal(PaymentCallbackOutcome.Applied, (await Transfer(92704)).Outcome);
        // SePay delivering the same webhook again changes nothing.
        Assert.Equal(PaymentCallbackOutcome.Duplicate, (await Transfer(92704)).Outcome);
        // The payer sending the money a second time is new money: not applied, owed back.
        var second = await Transfer(92705);

        Assert.Equal(PaymentCallbackOutcome.Surplus, second.Outcome);
        Assert.Equal(CallbackResults.Rejected, (await f.Db.PaymentCallbackEvents.AsNoTracking()
            .SingleAsync(e => e.callback_event_id == second.CallbackEventId)).processing_result);
        Assert.Equal(PaymentStatuses.Success, await StatusOf(f, checkout.TransactionId));
        Assert.Single(await f.Db.Invoices.AsNoTracking().Where(i => i.fee_item_id == PastDueItem).ToListAsync());
    }

    [Fact]
    public async Task A_repeated_momo_success_without_a_transfer_id_is_still_only_a_duplicate()
    {
        using var f = await Fixture.Create();
        var repository = f.Repository();
        var checkout = await repository.CreateFeeCheckoutAsync(Vendor, PastDueItem, "MOMO", "key-1", default);
        await repository.SetPaymentProviderReferenceAsync(checkout.TransactionId, "SB-T1-aaaa1111", default);
        var report = new PaymentCallbackData(
            "MOMO", "SB-T1-aaaa1111", "key-1", checkout.Amount, PaymentStatuses.Success, "{}", true);

        await repository.ApplyPaymentCallbackAsync(report, default);
        var again = await repository.ApplyPaymentCallbackAsync(report, default);

        Assert.Equal(PaymentCallbackOutcome.Duplicate, again.Outcome);
    }

    private static async Task<string> StatusOf(Fixture f, long transactionId) =>
        (await f.Db.PaymentTransactions.AsNoTracking().SingleAsync(t => t.transaction_id == transactionId)).transaction_status;

    private sealed class Fixture : IDisposable
    {
        private SqliteConnection Connection { get; } = new("Data Source=:memory:");
        private TimeProvider Clock { get; init; } = null!;
        public TestContext Db { get; private set; } = null!;

        public FinanceRepository Repository(PaymentGatewaySettings? paymentSettings = null) =>
            new(Db, Clock, Options.Create(paymentSettings ?? new PaymentGatewaySettings()));

        public static async Task<Fixture> Create(DateTimeOffset? now = null)
        {
            var f = new Fixture { Clock = new FixedClock(now ?? MorningOf26th) };
            await f.Connection.OpenAsync();
            f.Db = new TestContext(new DbContextOptionsBuilder<StreetBizDbContext>().UseSqlite(f.Connection).Options);
            await f.Db.Database.EnsureCreatedAsync();

            f.Db.Roles.AddRange(new Role { role_code = "WARD_AUTHORITY", role_name = "Ward" },
                new Role { role_code = "VENDOR", role_name = "Vendor" });
            f.Db.AdministrativeUnits.Add(new AdministrativeUnit { unit_id = 1, unit_type = "WARD", unit_name = "One" });
            foreach (var id in new long[] { 1, Vendor })
                f.Db.UserAccounts.Add(new UserAccount
                {
                    user_id = id,
                    phone_number = $"09000000{id:00}",
                    password_hash = "test-only",
                    full_name = $"User {id}",
                    role_code = id == 1 ? "WARD_AUTHORITY" : "VENDOR",
                    ward_unit_id = 1,
                    account_status = "ACTIVE",
                });
            f.Db.Vendors.Add(new Vendor { vendor_id = Vendor, user_id = Vendor });
            f.Db.PricingZones.Add(new PricingZone
            {
                zone_id = 1, ward_unit_id = 1, ward_unit_type = "WARD", zone_name = "Zone", price_per_day = 25000, created_by = 1,
            });
            f.Db.SidewalkSlots.AddRange(
                new SidewalkSlot { slot_id = 1, zone_id = 1, slot_code = "S-01", latitude = 16.06m, longitude = 108.21m, slot_status = "ACTIVE", source = "WARD_DEFINED" },
                new SidewalkSlot { slot_id = 2, zone_id = 1, slot_code = "S-02", latitude = 16.07m, longitude = 108.22m, slot_status = "ACTIVE", source = "WARD_DEFINED" });
            f.Db.BusinessRegistrations.Add(new BusinessRegistration
            {
                registration_id = 1, vendor_id = Vendor, ward_unit_id = 1, vendor_type = "ITINERANT", display_name = "Vendor", registration_status = "APPROVED",
            });
            foreach (var id in new long[] { 1, 2 })
            {
                f.Db.RentalApplications.Add(new RentalApplication
                {
                    application_id = id, registration_id = 1, slot_id = id, application_method = "MANUAL_SELECTED",
                    requested_term_days = 120, application_status = "APPROVED",
                });
                f.Db.RentalContracts.Add(new RentalContract
                {
                    contract_id = id, application_id = id, slot_id = id, vendor_id = Vendor,
                    start_date = new DateOnly(2026, 9, 1), end_date = new DateOnly(2026, 12, 29), contract_status = "ACTIVE",
                });
            }

            var generated = new DateTime(2026, 8, 30, 3, 0, 0, DateTimeKind.Utc);
            f.Db.FeeSchedules.AddRange(
                new FeeSchedule { fee_schedule_id = 1, contract_id = 1, revision = 1, total_amount = 1_000_000m, generated_at = generated, superseded_at = generated.AddDays(1) },
                new FeeSchedule { fee_schedule_id = 2, contract_id = 1, revision = 2, total_amount = 6_000_000m, generated_at = generated.AddDays(1) });
            f.Db.FeeScheduleItems.AddRange(
                Item(SupersededItem, 1, new DateOnly(2026, 9, 15), 1_000_000m, FeeItemStatuses.Pending),
                Item(2, 2, new DateOnly(2026, 9, 1), 1_000_000m, FeeItemStatuses.Paid),
                Item(PastDueItem, 2, new DateOnly(2026, 9, 20), 1_000_000m, FeeItemStatuses.Pending),
                Item(DueSoonItem, 2, new DateOnly(2026, 9, 28), 2_000_000m, FeeItemStatuses.Pending),
                Item(5, 2, new DateOnly(2026, 10, 28), 2_000_000m, FeeItemStatuses.Pending));
            f.Db.Invoices.AddRange(
                Invoice(1, "HD-2025-000999"),
                Invoice(2, "HD-2026-000009"),
                Invoice(3, "HD-2026-000010"));
            await f.Db.SaveChangesAsync();
            f.Db.ChangeTracker.Clear();
            return f;
        }

        private static FeeScheduleItem Item(long id, long scheduleId, DateOnly due, decimal amount, string status) => new()
        {
            fee_item_id = id, fee_schedule_id = scheduleId, due_date = due, amount = amount, item_status = status,
            paid_at = status == FeeItemStatuses.Paid ? due.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc) : null,
        };

        private static Invoice Invoice(long id, string number) => new()
        {
            invoice_id = id, invoice_number = number, fee_item_id = 2, vendor_id = Vendor, amount = 1_000_000m,
            issued_at = new DateTime(2026, 9, 1, 3, 0, 0, DateTimeKind.Utc),
        };

        public void Dispose()
        {
            Db.Dispose();
            Connection.Dispose();
        }
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class TestContext(DbContextOptions<StreetBizDbContext> options) : StreetBizDbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);
            foreach (var entity in modelBuilder.Model.GetEntityTypes())
                foreach (var property in entity.GetProperties())
                {
                    if (property.GetComputedColumnSql() is not null)
                    {
                        property.SetComputedColumnSql(null);
                        property.ValueGenerated = ValueGenerated.Never;
                    }
                    property.SetDefaultValueSql(null);
                }
        }
    }
}
