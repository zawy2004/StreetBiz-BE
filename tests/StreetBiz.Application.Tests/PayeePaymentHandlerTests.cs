using FluentAssertions;
using Moq;
using StreetBiz.Application.Common.Exceptions;
using StreetBiz.Application.Common.Interfaces;
using StreetBiz.Application.Common.Models;
using StreetBiz.Application.Common.Security;
using StreetBiz.Application.Features.Commerce;
using StreetBiz.Application.Features.Finance.BankTransfer;
using StreetBiz.Application.Features.Finance.FeeReminders;
using StreetBiz.Application.Features.Finance.PayFee;
using StreetBiz.Application.Features.Finance.SyncPayment;
using StreetBiz.Application.Features.PayoutAccounts;

namespace StreetBiz.Application.Tests;

/// <summary>
/// payOS paid straight to the payee's own channel: which payee a fee or an order goes to, how the
/// link is opened and shown again, who may settle it, and what happens when the channel breaks.
/// </summary>
public sealed class PayeePaymentHandlerTests
{
    private static readonly DateTime Now = new(2026, 11, 2, 3, 0, 0, DateTimeKind.Utc);
    private static readonly PayeeSummary Ward = new(11, "WARD", "Phường Hải Châu", "MB Bank", "••••••6789", "UBND PHUONG", true);
    private static readonly PayeeSummary Store = new(21, "VENDOR", "Bánh mì Cô Lan", "MB Bank", "••••••3456", "NGUYEN THI LAN", true);
    private static readonly PayeeCredentials WardKeys = new(11, "ward-client", "ward-api", "ward-checksum");

    private static readonly BankTransferInstructions Qr = new("MB Bank", "V3CAS01", "UBND PHUONG", 1_040_000m, "CSXXSB101")
    {
        QrContent = "00020101QR",
        CheckoutUrl = "https://pay.payos.vn/web/x",
    };

    // ---------------------------------------------------------------------------------- fees

    private static (Mock<IFinanceRepository> Finance, Mock<IPaymentGateway> Gateway) FeeSetup()
    {
        var finance = new Mock<IFinanceRepository>();
        finance.Setup(x => x.GetFeeItemForCheckoutAsync(4, 2, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new FeeItemCheckoutRow(2, 4, 5, "NVL-01", 2, 3, new DateOnly(2026, 11, 10), 1_040_000m, FeeItemStatuses.Pending));
        finance.Setup(x => x.CreateFeeCheckoutAsync(4, 2, "PAYOS", "idem-1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new FinanceCheckoutTransactionRow(101, "idem-1", "PAYOS", 1_040_000m));
        var gateway = new Mock<IPaymentGateway>();
        gateway.Setup(x => x.IsProviderAvailable("PAYOS")).Returns(true);
        return (finance, gateway);
    }

    [Fact]
    public async Task A_fee_cannot_go_to_payos_until_its_ward_has_connected_an_account()
    {
        var (finance, gateway) = FeeSetup();
        var payees = new Mock<IPayeeAccountRepository>();
        payees.Setup(x => x.GetForFeeItemAsync(2, It.IsAny<CancellationToken>())).ReturnsAsync((PayeeSummary?)null);

        var act = () => new PayFeeCommandHandler(VendorContext(), finance.Object, gateway.Object, payees.Object, Checkouts(payees).Object)
            .Handle(new PayFeeCommand(2, "payos", "idem-1"), CancellationToken.None);

        (await act.Should().ThrowAsync<DomainRuleException>()).WithMessage(FinanceMessages.WardNotAcceptingTransfers);
        // Nothing was written: no PENDING attempt is left behind.
        finance.Verify(x => x.CreateFeeCheckoutAsync(It.IsAny<long>(), It.IsAny<long>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task A_ward_account_in_error_is_not_paid_into()
    {
        var (finance, gateway) = FeeSetup();
        var payees = new Mock<IPayeeAccountRepository>();
        payees.Setup(x => x.GetForFeeItemAsync(2, It.IsAny<CancellationToken>())).ReturnsAsync(Ward with { Active = false });

        var act = () => new PayFeeCommandHandler(VendorContext(), finance.Object, gateway.Object, payees.Object, Checkouts(payees).Object)
            .Handle(new PayFeeCommand(2, "payos", "idem-1"), CancellationToken.None);

        await act.Should().ThrowAsync<DomainRuleException>();
    }

    [Fact]
    public async Task A_fee_to_payos_opens_the_wards_link_and_returns_its_qr_without_touching_the_reference_itself()
    {
        var (finance, gateway) = FeeSetup();
        var payees = new Mock<IPayeeAccountRepository>();
        payees.Setup(x => x.GetForFeeItemAsync(2, It.IsAny<CancellationToken>())).ReturnsAsync(Ward);
        payees.Setup(x => x.GetCredentialsAsync(11, It.IsAny<CancellationToken>())).ReturnsAsync(WardKeys);
        var transactions = Transactions(101, payeeAccountId: 11, checkoutPayload: null, providerReference: null);
        PaymentGatewayCheckoutRequest? sent = null;
        gateway.Setup(x => x.CreateCheckoutAsync(It.IsAny<PaymentGatewayCheckoutRequest>(), It.IsAny<CancellationToken>()))
            .Callback<PaymentGatewayCheckoutRequest, CancellationToken>((r, _) => sent = r)
            .ReturnsAsync(new PaymentGatewayCheckoutResult("https://pay.payos.vn/web/x", "PAYOS-101001", Qr) { CheckoutPayload = "{\"attempt\":1}" });

        var result = await new PayFeeCommandHandler(
                VendorContext(), finance.Object, gateway.Object, payees.Object, Checkouts(payees, transactions, gateway).Object)
            .Handle(new PayFeeCommand(2, "payos", "idem-1"), CancellationToken.None);

        result.Provider.Should().Be("PAYOS");
        result.BankTransfer!.QrContent.Should().Be("00020101QR");
        result.BankTransfer.CheckoutUrl.Should().Be("https://pay.payos.vn/web/x");
        sent!.Payee.Should().Be(WardKeys);
        sent.Purpose.Should().Be(PaymentPurposes.RentalFee);
        transactions.Verify(x => x.AttachPayeeAsync(101, 11, It.IsAny<CancellationToken>()), Times.Once);
        transactions.Verify(x => x.SaveCheckoutAsync(101, "PAYOS-101001", "{\"attempt\":1}", It.IsAny<CancellationToken>()), Times.Once);
        finance.Verify(x => x.SetPaymentProviderReferenceAsync(It.IsAny<long>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task A_link_that_is_still_payable_is_shown_again_instead_of_opening_a_second_one()
    {
        var payees = new Mock<IPayeeAccountRepository>();
        var transactions = Transactions(101, 11, "{\"attempt\":1}", "PAYOS-101001");
        var gateway = new Mock<IPaymentGateway>();
        gateway.Setup(x => x.DescribeStoredCheckout("PAYOS", "{\"attempt\":1}", 1_040_000m)).Returns(Qr);

        var result = await Checkouts(payees, transactions, gateway).Object.OpenAsync(Ward, Request(), default);

        result.ProviderReference.Should().Be("PAYOS-101001");
        result.BankTransfer.Should().Be(Qr);
        gateway.Verify(x => x.CreateCheckoutAsync(It.IsAny<PaymentGatewayCheckoutRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task An_expired_link_gets_a_new_code_built_on_the_previous_attempt()
    {
        var payees = new Mock<IPayeeAccountRepository>();
        payees.Setup(x => x.GetCredentialsAsync(11, It.IsAny<CancellationToken>())).ReturnsAsync(WardKeys);
        var transactions = Transactions(101, 11, "{\"attempt\":1}", "PAYOS-101001");
        var gateway = new Mock<IPaymentGateway>();
        gateway.Setup(x => x.DescribeStoredCheckout(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<decimal>())).Returns((BankTransferInstructions?)null);
        PaymentGatewayCheckoutRequest? sent = null;
        gateway.Setup(x => x.CreateCheckoutAsync(It.IsAny<PaymentGatewayCheckoutRequest>(), It.IsAny<CancellationToken>()))
            .Callback<PaymentGatewayCheckoutRequest, CancellationToken>((r, _) => sent = r)
            .ReturnsAsync(new PaymentGatewayCheckoutResult("", "PAYOS-101002", Qr) { CheckoutPayload = "{\"attempt\":2}" });

        await Checkouts(payees, transactions, gateway).Object.OpenAsync(Ward, Request(), default);

        sent!.PreviousCheckoutPayload.Should().Be("{\"attempt\":1}");
        transactions.Verify(x => x.SaveCheckoutAsync(101, "PAYOS-101002", "{\"attempt\":2}", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task A_transaction_never_changes_the_payee_it_pays()
    {
        var payees = new Mock<IPayeeAccountRepository>();
        var transactions = Transactions(101, payeeAccountId: 99, checkoutPayload: null, providerReference: null);

        var act = () => Checkouts(payees, transactions, new Mock<IPaymentGateway>()).Object.OpenAsync(Ward, Request(), default);

        await act.Should().ThrowAsync<ConflictException>();
    }

    [Fact]
    public async Task Refused_keys_put_the_account_in_error_raise_one_alert_and_still_fail_the_checkout()
    {
        var payees = new Mock<IPayeeAccountRepository>();
        payees.Setup(x => x.GetCredentialsAsync(11, It.IsAny<CancellationToken>())).ReturnsAsync(WardKeys);
        payees.Setup(x => x.MarkErrorAsync(11, It.IsAny<string>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>())).ReturnsAsync(true);
        var transactions = Transactions(101, 11, null, null);
        var gateway = new Mock<IPaymentGateway>();
        gateway.Setup(x => x.CreateCheckoutAsync(It.IsAny<PaymentGatewayCheckoutRequest>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new PayeeChannelException("payOS từ chối khoá", unauthorized: true));
        var alerts = new Mock<IPaymentAlertNotifier>();

        var act = () => Checkouts(payees, transactions, gateway, alerts).Object.OpenAsync(Ward, Request(), default);

        await act.Should().ThrowAsync<PayeeChannelException>();
        payees.Verify(x => x.MarkErrorAsync(11, It.IsAny<string>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()), Times.Once);
        alerts.Verify(x => x.NotifyAsync(It.Is<PaymentAlert>(a => a.Provider == "PAYOS" && a.Outcome == "CHANNEL_ERROR" && a.TransactionId == 101), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task A_provider_outage_is_not_an_account_error()
    {
        var payees = new Mock<IPayeeAccountRepository>();
        payees.Setup(x => x.GetCredentialsAsync(11, It.IsAny<CancellationToken>())).ReturnsAsync(WardKeys);
        var transactions = Transactions(101, 11, null, null);
        var gateway = new Mock<IPaymentGateway>();
        gateway.Setup(x => x.CreateCheckoutAsync(It.IsAny<PaymentGatewayCheckoutRequest>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new PayeeChannelException("payOS từ chối tạo mã thanh toán"));

        var act = () => Checkouts(payees, transactions, gateway).Object.OpenAsync(Ward, Request(), default);

        await act.Should().ThrowAsync<PayeeChannelException>();
        payees.Verify(x => x.MarkErrorAsync(It.IsAny<long>(), It.IsAny<string>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // -------------------------------------------------------------------------------- orders

    private static Mock<ICommerceRepository> OrderRepository()
    {
        var commerce = new Mock<ICommerceRepository>();
        commerce.Setup(x => x.CheckoutAsync(2, 7, "PAYOS", "idem-o", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OrderMutationResult(OrderMutationOutcome.Updated, OrderRow("PENDING_PAYMENT")));
        return commerce;
    }

    private static CommerceOrderRow OrderRow(string status) => new(
        19, "SB-000019", 2, "Khách", 1, "Bánh mì Cô Lan", status, 65_000m, 65_000m, null, "PAYOS", "PENDING",
        null, null, null, null, null, null, null, DateTime.UtcNow, [], [],
        PaymentTransactionId: 55, PaymentIdempotencyKey: "idem-o", PaymentAmount: 65_000m, PaymentProviderReference: null);

    [Fact]
    public async Task A_store_without_an_account_takes_no_online_order_and_nothing_is_written()
    {
        var commerce = OrderRepository();
        var gateway = new Mock<IPaymentGateway>();
        gateway.Setup(x => x.IsProviderAvailable("PAYOS")).Returns(true);
        var payees = new Mock<IPayeeAccountRepository>();
        payees.Setup(x => x.GetForCartAsync(7, 2, It.IsAny<CancellationToken>())).ReturnsAsync((PayeeSummary?)null);

        var act = () => new CheckoutOrderCommandHandler(CustomerContext(), commerce.Object, gateway.Object, payees.Object, Checkouts(payees).Object)
            .Handle(new CheckoutOrderCommand(7, "payos", "idem-o"), CancellationToken.None);

        (await act.Should().ThrowAsync<DomainRuleException>()).WithMessage(CommerceMessages.StoreNotAcceptingOnlinePayment);
        commerce.Verify(x => x.CheckoutAsync(It.IsAny<long>(), It.IsAny<long>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task An_order_to_payos_pays_the_household_that_owns_the_store()
    {
        var commerce = OrderRepository();
        var gateway = new Mock<IPaymentGateway>();
        gateway.Setup(x => x.IsProviderAvailable("PAYOS")).Returns(true);
        var payees = new Mock<IPayeeAccountRepository>();
        payees.Setup(x => x.GetForCartAsync(7, 2, It.IsAny<CancellationToken>())).ReturnsAsync(Store);
        payees.Setup(x => x.GetCredentialsAsync(21, It.IsAny<CancellationToken>())).ReturnsAsync(new PayeeCredentials(21, "store-client", "store-api", "store-checksum"));
        var transactions = Transactions(55, 21, null, null);
        PaymentGatewayCheckoutRequest? sent = null;
        gateway.Setup(x => x.CreateCheckoutAsync(It.IsAny<PaymentGatewayCheckoutRequest>(), It.IsAny<CancellationToken>()))
            .Callback<PaymentGatewayCheckoutRequest, CancellationToken>((r, _) => sent = r)
            .ReturnsAsync(new PaymentGatewayCheckoutResult("https://pay.payos.vn/web/o", "PAYOS-55001", Qr) { CheckoutPayload = "{}" });

        var result = await new CheckoutOrderCommandHandler(
                CustomerContext(), commerce.Object, gateway.Object, payees.Object, Checkouts(payees, transactions, gateway).Object)
            .Handle(new CheckoutOrderCommand(7, "payos", "idem-o"), CancellationToken.None);

        result.OrderId.Should().Be(19);
        result.BankTransfer!.QrContent.Should().Be("00020101QR");
        sent!.Payee!.ClientId.Should().Be("store-client"); // the household's channel, not the platform's
        sent.Purpose.Should().Be("ORDER");
        transactions.Verify(x => x.AttachPayeeAsync(55, 21, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task When_the_link_cannot_be_opened_the_order_is_released_and_the_original_error_is_kept()
    {
        var commerce = OrderRepository();
        commerce.Setup(x => x.CancelCustomerOrderAsync(2, 19, "PENDING_PAYMENT", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OrderMutationResult(OrderMutationOutcome.Updated, OrderRow("CANCELLED")));
        var gateway = new Mock<IPaymentGateway>();
        gateway.Setup(x => x.IsProviderAvailable("PAYOS")).Returns(true);
        var payees = new Mock<IPayeeAccountRepository>();
        payees.Setup(x => x.GetForCartAsync(7, 2, It.IsAny<CancellationToken>())).ReturnsAsync(Store);
        payees.Setup(x => x.GetCredentialsAsync(21, It.IsAny<CancellationToken>())).ReturnsAsync(new PayeeCredentials(21, "c", "a", "k"));
        gateway.Setup(x => x.CreateCheckoutAsync(It.IsAny<PaymentGatewayCheckoutRequest>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new PayeeChannelException("payOS không phản hồi"));

        var act = () => new CheckoutOrderCommandHandler(
                CustomerContext(), commerce.Object, gateway.Object, payees.Object, Checkouts(payees, Transactions(55, 21, null, null), gateway).Object)
            .Handle(new CheckoutOrderCommand(7, "payos", "idem-o"), CancellationToken.None);

        (await act.Should().ThrowAsync<PayeeChannelException>()).WithMessage("payOS không phản hồi");
        commerce.Verify(x => x.CancelCustomerOrderAsync(2, 19, "PENDING_PAYMENT", It.IsAny<CancellationToken>()), Times.Once);
    }

    // ---------------------------------------------------------------------------- the webhook

    [Fact]
    public async Task A_webhook_for_an_unknown_token_is_answered_but_neither_recorded_nor_alerted()
    {
        var gateway = new Mock<IPaymentGateway>(MockBehavior.Strict);
        var finance = new Mock<IFinanceRepository>(MockBehavior.Strict);
        var commerce = new Mock<ICommerceRepository>(MockBehavior.Strict);
        var alerts = new Mock<IPaymentAlertNotifier>(MockBehavior.Strict);
        var payees = new Mock<IPayeeAccountRepository>();
        payees.Setup(x => x.FindByWebhookTokenAsync("nope", It.IsAny<CancellationToken>())).ReturnsAsync(((PayeeAccountRow, PayeeCredentials)?)null);

        var receipt = await new ProcessPaymentCallbackCommandHandler(gateway.Object, commerce.Object, finance.Object, alerts.Object, payees.Object)
            .Handle(new ProcessPaymentCallbackCommand("payos", "{}", null) { PayeeToken = "nope" }, CancellationToken.None);

        receipt.Result.Should().Be("IGNORED");
    }

    [Fact]
    public async Task A_webhook_is_verified_with_its_own_payees_keys_and_applied_for_that_payee_only()
    {
        var account = AccountRow(11);
        var payees = new Mock<IPayeeAccountRepository>();
        payees.Setup(x => x.FindByWebhookTokenAsync("good-token", It.IsAny<CancellationToken>())).ReturnsAsync((account, WardKeys));
        PaymentCallbackHeaders? headers = null;
        var gateway = new Mock<IPaymentGateway>();
        gateway.Setup(x => x.VerifyCallbackAsync("PAYOS", "{}", It.IsAny<PaymentCallbackHeaders?>(), It.IsAny<CancellationToken>()))
            .Callback<string, string, PaymentCallbackHeaders?, CancellationToken>((_, _, h, _) => headers = h)
            .ReturnsAsync(new PaymentGatewayCallback("PAYOS-101001", null, 1_040_000m, "SUCCESS", true, "{}", "FT-1"));
        PaymentCallbackData? applied = null;
        var finance = new Mock<IFinanceRepository>();
        finance.Setup(x => x.FindPaymentPurposeAsync("PAYOS-101001", null, It.IsAny<CancellationToken>())).ReturnsAsync(PaymentPurposes.RentalFee);
        finance.Setup(x => x.ApplyPaymentCallbackAsync(It.IsAny<PaymentCallbackData>(), It.IsAny<CancellationToken>()))
            .Callback<PaymentCallbackData, CancellationToken>((d, _) => applied = d)
            .ReturnsAsync(new FinanceCallbackMutationResult(PaymentCallbackOutcome.Applied, 7, 101));

        var receipt = await new ProcessPaymentCallbackCommandHandler(gateway.Object, Mock.Of<ICommerceRepository>(), finance.Object, null, payees.Object)
            .Handle(new ProcessPaymentCallbackCommand("payos", "{}", null) { PayeeToken = "good-token" }, CancellationToken.None);

        receipt.Result.Should().Be("APPLIED");
        headers!.Payee.Should().Be(WardKeys);
        applied!.PayeeAccountId.Should().Be(11);
        applied.ProviderEventId.Should().Be("FT-1");
    }

    [Fact]
    public async Task A_webhook_to_a_payee_that_cannot_be_applied_raises_the_same_alerts_as_any_provider()
    {
        var payees = new Mock<IPayeeAccountRepository>();
        payees.Setup(x => x.FindByWebhookTokenAsync("t", It.IsAny<CancellationToken>())).ReturnsAsync((AccountRow(11), WardKeys));
        var gateway = new Mock<IPaymentGateway>();
        gateway.Setup(x => x.VerifyCallbackAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<PaymentCallbackHeaders?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PaymentGatewayCallback("PAYOS-101001", null, 1_040_000m, "SUCCESS", true, "{}", "FT-1"));
        var finance = new Mock<IFinanceRepository>();
        finance.Setup(x => x.FindPaymentPurposeAsync(It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>())).ReturnsAsync(PaymentPurposes.RentalFee);
        finance.Setup(x => x.ApplyPaymentCallbackAsync(It.IsAny<PaymentCallbackData>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new FinanceCallbackMutationResult(PaymentCallbackOutcome.Rejected, 7, 101));
        var alerts = new Mock<IPaymentAlertNotifier>();

        await new ProcessPaymentCallbackCommandHandler(gateway.Object, Mock.Of<ICommerceRepository>(), finance.Object, alerts.Object, payees.Object)
            .Handle(new ProcessPaymentCallbackCommand("payos", "{}", null) { PayeeToken = "t" }, CancellationToken.None);

        alerts.Verify(x => x.NotifyAsync(It.Is<PaymentAlert>(a => a.Provider == "PAYOS" && a.Outcome == "REJECTED"), It.IsAny<CancellationToken>()), Times.Once);
    }

    // ---------------------------------------------------------------- status checks (layer 2)

    [Fact]
    public async Task Checking_a_fee_asks_payos_with_the_wards_keys_and_applies_a_paid_answer_for_that_payee()
    {
        var finance = new Mock<IFinanceRepository>();
        finance.SetupSequence(x => x.GetVendorPaymentAsync(4, 101, It.IsAny<CancellationToken>()))
            .ReturnsAsync(PaymentRow("PENDING"))
            .ReturnsAsync(PaymentRow("SUCCESS"));
        PaymentCallbackData? applied = null;
        finance.Setup(x => x.ApplyPaymentCallbackAsync(It.IsAny<PaymentCallbackData>(), It.IsAny<CancellationToken>()))
            .Callback<PaymentCallbackData, CancellationToken>((d, _) => applied = d)
            .ReturnsAsync(new FinanceCallbackMutationResult(PaymentCallbackOutcome.Applied, 7, 101));
        var payees = new Mock<IPayeeAccountRepository>();
        payees.Setup(x => x.GetCredentialsAsync(11, It.IsAny<CancellationToken>())).ReturnsAsync(WardKeys);
        var gateway = new Mock<IPaymentGateway>();
        gateway.Setup(x => x.QueryPaymentAsync("PAYOS", "PAYOS-101001", WardKeys, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PaymentGatewayCallback("PAYOS-101001", null, 1_040_000m, "SUCCESS", true, "{}", "FT-1"));

        var result = await new SyncFinancePaymentCommandHandler(
                VendorContext(), finance.Object, gateway.Object, Transactions(101, 11, "{}", "PAYOS-101001").Object, Checkouts(payees, gateway: gateway).Object)
            .Handle(new SyncFinancePaymentCommand(101), CancellationToken.None);

        result.Status.Should().Be("SUCCESS");
        applied!.PayeeAccountId.Should().Be(11);
        applied.Provider.Should().Be("PAYOS");
    }

    [Fact]
    public async Task Checking_with_refused_keys_changes_nothing_and_reports_the_account()
    {
        var finance = new Mock<IFinanceRepository>();
        finance.Setup(x => x.GetVendorPaymentAsync(4, 101, It.IsAny<CancellationToken>())).ReturnsAsync(PaymentRow("PENDING"));
        var payees = new Mock<IPayeeAccountRepository>();
        payees.Setup(x => x.GetCredentialsAsync(11, It.IsAny<CancellationToken>())).ReturnsAsync(WardKeys);
        payees.Setup(x => x.MarkErrorAsync(11, It.IsAny<string>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>())).ReturnsAsync(true);
        var gateway = new Mock<IPaymentGateway>();
        gateway.Setup(x => x.QueryPaymentAsync("PAYOS", "PAYOS-101001", WardKeys, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new PayeeChannelException("refused", unauthorized: true));

        var result = await new SyncFinancePaymentCommandHandler(
                VendorContext(), finance.Object, gateway.Object, Transactions(101, 11, "{}", "PAYOS-101001").Object, Checkouts(payees, gateway: gateway).Object)
            .Handle(new SyncFinancePaymentCommand(101), CancellationToken.None);

        result.Status.Should().Be("PENDING");
        finance.Verify(x => x.ApplyPaymentCallbackAsync(It.IsAny<PaymentCallbackData>(), It.IsAny<CancellationToken>()), Times.Never);
        payees.Verify(x => x.MarkErrorAsync(11, It.IsAny<string>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task The_sweep_settles_a_household_order_whose_webhook_never_arrived_with_the_households_own_keys()
    {
        var finance = new Mock<IFinanceRepository>();
        finance.Setup(x => x.ListStalePaymentsAsync(It.IsAny<DateTime>(), It.IsAny<CancellationToken>())).ReturnsAsync([]);
        var orderPayment = new PaymentCheckoutState(55, "ORDER", "PAYOS", "PAYOS-55001", "idem-o", 65_000m, "PENDING", 21, "{}", 19, Now.AddHours(-1));
        var transactions = new Mock<IPaymentTransactionRepository>();
        transactions.Setup(x => x.ListStalePayeeOrderPaymentsAsync(It.IsAny<DateTime>(), It.IsAny<CancellationToken>())).ReturnsAsync([orderPayment]);
        var keys = new PayeeCredentials(21, "store-client", "store-api", "store-checksum");
        var payees = new Mock<IPayeeAccountRepository>();
        payees.Setup(x => x.GetCredentialsAsync(21, It.IsAny<CancellationToken>())).ReturnsAsync(keys);
        var gateway = new Mock<IPaymentGateway>();
        gateway.Setup(x => x.QueryPaymentAsync("PAYOS", "PAYOS-55001", keys, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PaymentGatewayCallback("PAYOS-55001", null, 65_000m, "SUCCESS", true, "{}", "FT-9"));
        PaymentCallbackData? applied = null;
        var commerce = new Mock<ICommerceRepository>();
        commerce.Setup(x => x.ApplyPaymentCallbackAsync(It.IsAny<PaymentCallbackData>(), It.IsAny<CancellationToken>()))
            .Callback<PaymentCallbackData, CancellationToken>((d, _) => applied = d)
            .ReturnsAsync(new PaymentCallbackMutationResult(PaymentCallbackOutcome.Applied, 9, 55, 19, "PLACED"));

        var result = await new SettleStalePaymentsCommandHandler(
                finance.Object, gateway.Object, new FixedClock(Now), transactions.Object, Checkouts(payees, transactions, gateway).Object, commerce.Object)
            .Handle(new SettleStalePaymentsCommand(), CancellationToken.None);

        result.SettledCount.Should().Be(1);
        result.ClosedCount.Should().Be(0); // an unpaid order is never closed by this sweep
        applied!.PayeeAccountId.Should().Be(21);
        applied.IdempotencyKey.Should().Be("idem-o");
    }

    [Fact]
    public async Task The_sweep_asks_about_a_stale_fee_with_the_wards_keys()
    {
        var finance = new Mock<IFinanceRepository>();
        finance.Setup(x => x.ListStalePaymentsAsync(It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([new StalePaymentRow(101, "PAYOS", "PAYOS-101001", "idem-1", Now.AddHours(-1)) { PayeeAccountId = 11 }]);
        PaymentCallbackData? applied = null;
        finance.Setup(x => x.ApplyPaymentCallbackAsync(It.IsAny<PaymentCallbackData>(), It.IsAny<CancellationToken>()))
            .Callback<PaymentCallbackData, CancellationToken>((d, _) => applied = d)
            .ReturnsAsync(new FinanceCallbackMutationResult(PaymentCallbackOutcome.Applied, 7, 101));
        var payees = new Mock<IPayeeAccountRepository>();
        payees.Setup(x => x.GetCredentialsAsync(11, It.IsAny<CancellationToken>())).ReturnsAsync(WardKeys);
        var gateway = new Mock<IPaymentGateway>();
        gateway.Setup(x => x.QueryPaymentAsync("PAYOS", "PAYOS-101001", WardKeys, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PaymentGatewayCallback("PAYOS-101001", null, 1_040_000m, "SUCCESS", true, "{}", "FT-1"));
        var transactions = Transactions(101, 11, "{}", "PAYOS-101001");

        var result = await new SettleStalePaymentsCommandHandler(
                finance.Object, gateway.Object, new FixedClock(Now), transactions.Object, Checkouts(payees, transactions, gateway).Object)
            .Handle(new SettleStalePaymentsCommand(), CancellationToken.None);

        result.SettledCount.Should().Be(1);
        applied!.PayeeAccountId.Should().Be(11);
    }

    // ------------------------------------------------------------------------- showing the QR

    [Fact]
    public async Task A_fee_page_reloaded_shows_the_same_stored_qr()
    {
        var finance = new Mock<IFinanceRepository>();
        finance.Setup(x => x.GetVendorPaymentAsync(4, 101, It.IsAny<CancellationToken>())).ReturnsAsync(PaymentRow("PENDING"));
        var gateway = new Mock<IPaymentGateway>();
        gateway.Setup(x => x.DescribeStoredCheckout("PAYOS", "{\"attempt\":1}", 1_040_000m)).Returns(Qr);

        var dto = await new GetFinanceBankTransferQueryHandler(
                VendorContext(), finance.Object, gateway.Object, Transactions(101, 11, "{\"attempt\":1}", "PAYOS-101001").Object)
            .Handle(new GetFinanceBankTransferQuery(101), CancellationToken.None);

        dto.QrContent.Should().Be("00020101QR");
        dto.CheckoutUrl.Should().Be("https://pay.payos.vn/web/x");
    }

    [Fact]
    public async Task Once_the_stored_link_has_expired_there_is_no_qr_to_show()
    {
        var finance = new Mock<IFinanceRepository>();
        finance.Setup(x => x.GetVendorPaymentAsync(4, 101, It.IsAny<CancellationToken>())).ReturnsAsync(PaymentRow("PENDING"));
        var gateway = new Mock<IPaymentGateway>();
        gateway.Setup(x => x.DescribeStoredCheckout(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<decimal>())).Returns((BankTransferInstructions?)null);

        var act = () => new GetFinanceBankTransferQueryHandler(
                VendorContext(), finance.Object, gateway.Object, Transactions(101, 11, "{\"attempt\":1}", "PAYOS-101001").Object)
            .Handle(new GetFinanceBankTransferQuery(101), CancellationToken.None);

        await act.Should().ThrowAsync<NotFoundException>();
    }

    [Fact]
    public async Task An_order_page_reloaded_shows_the_same_stored_qr()
    {
        var commerce = new Mock<ICommerceRepository>();
        commerce.Setup(x => x.GetCustomerOrderAsync(2, 19, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Order(OrderStatuses.PendingPayment));
        var gateway = new Mock<IPaymentGateway>();
        gateway.Setup(x => x.DescribeStoredCheckout("PAYOS", "{\"attempt\":1}", 1_040_000m)).Returns(Qr);

        var dto = await new GetOrderBankTransferQueryHandler(
                CustomerContext(), commerce.Object, gateway.Object, Transactions(55, 21, "{\"attempt\":1}", "PAYOS-55001").Object)
            .Handle(new GetOrderBankTransferQuery(19), CancellationToken.None);

        dto.QrContent.Should().Be("00020101QR");
    }

    [Fact]
    public async Task A_settled_order_has_no_qr_to_show()
    {
        var commerce = new Mock<ICommerceRepository>();
        commerce.Setup(x => x.GetCustomerOrderAsync(2, 19, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Order(OrderStatuses.Placed));
        var gateway = new Mock<IPaymentGateway>(MockBehavior.Strict);

        var act = () => new GetOrderBankTransferQueryHandler(
                CustomerContext(), commerce.Object, gateway.Object, new Mock<IPaymentTransactionRepository>(MockBehavior.Strict).Object)
            .Handle(new GetOrderBankTransferQuery(19), CancellationToken.None);

        await act.Should().ThrowAsync<NotFoundException>();
    }

    [Fact]
    public void Payos_is_an_accepted_provider_for_checkout_and_sepay_no_longer_is()
    {
        new PayFeeCommandValidator().Validate(new PayFeeCommand(2, "payos", "idem-1")).IsValid.Should().BeTrue();
        new CheckoutOrderCommandValidator().Validate(new CheckoutOrderCommand(1, "PAYOS", "idem-1")).IsValid.Should().BeTrue();
        PaymentProviders.IsValid("PAYOS").Should().BeTrue();
        new PayFeeCommandValidator().Validate(new PayFeeCommand(2, "sepay", "idem-1")).IsValid.Should().BeFalse();
        PaymentProviders.IsValid("SEPAY").Should().BeFalse();
    }

    // ----------------------------------------------------------------------------- the payee

    [Fact]
    public async Task The_payment_screen_learns_who_a_fee_is_paid_to_only_for_the_callers_own_fee()
    {
        var finance = new Mock<IFinanceRepository>();
        finance.Setup(x => x.GetFeeItemForCheckoutAsync(4, 2, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new FeeItemCheckoutRow(2, 4, 5, "NVL-01", 2, 3, new DateOnly(2026, 11, 10), 1_040_000m, FeeItemStatuses.Pending));
        var payees = new Mock<IPayeeAccountRepository>();
        payees.Setup(x => x.GetForFeeItemAsync(2, It.IsAny<CancellationToken>())).ReturnsAsync(Ward);

        var own = await new GetFeePayeeQueryHandler(VendorContext(), finance.Object, payees.Object)
            .Handle(new GetFeePayeeQuery(2), CancellationToken.None);
        var notOwn = () => new GetFeePayeeQueryHandler(VendorContext(), finance.Object, payees.Object)
            .Handle(new GetFeePayeeQuery(99), CancellationToken.None);

        own.Should().Be(Ward);
        await notOwn.Should().ThrowAsync<NotFoundException>();
        payees.Verify(x => x.GetForFeeItemAsync(99, It.IsAny<CancellationToken>()), Times.Never);
    }

    // ------------------------------------------------------------------------------- helpers

    private static PaymentGatewayCheckoutRequest Request() =>
        new(2, "Kỳ 2/3", 101, "idem-1", "PAYOS", 1_040_000m, PaymentPurposes.RentalFee);

    private static PayeeAccountRow AccountRow(long id) =>
        new(id, "WARD", null, 1, "client", "MB Bank", "0123456789", "UBND", "good-token", "ACTIVE", null, Now, Now, Now, Now, null);

    private static FinancePaymentStateRow PaymentRow(string status) =>
        new(101, "PAYOS", "PAYOS-101001", "idem-1", status, 1_040_000m);

    private static CommerceOrderRow Order(string status) => new(
        19, "SB-000019", 2, "Khách", 1, "Bếp Việt", status, 1_040_000m, 1_040_000m, null, "PAYOS", "PENDING",
        null, null, null, null, null, null, null, DateTime.UtcNow, [], [],
        PaymentTransactionId: 55, PaymentIdempotencyKey: "idem-o", PaymentAmount: 1_040_000m, PaymentProviderReference: "PAYOS-55001");

    private static Mock<IPaymentTransactionRepository> Transactions(
        long id, long? payeeAccountId, string? checkoutPayload, string? providerReference)
    {
        var transactions = new Mock<IPaymentTransactionRepository>();
        transactions.Setup(x => x.GetAsync(id, It.IsAny<CancellationToken>())).ReturnsAsync(
            new PaymentCheckoutState(id, "RENTAL_FEE", "PAYOS", providerReference, "idem-1", 1_040_000m, "PENDING", payeeAccountId, checkoutPayload, null, Now));
        return transactions;
    }

    /// <summary>The real service over mocked collaborators (it is sealed); <c>.Object</c> mirrors Moq.</summary>
    private sealed record Wired(PayeeCheckouts Object);

    private static Wired Checkouts(
        Mock<IPayeeAccountRepository> payees,
        Mock<IPaymentTransactionRepository>? transactions = null,
        Mock<IPaymentGateway>? gateway = null,
        Mock<IPaymentAlertNotifier>? alerts = null)
    {
        var failures = new PayeeChannelFailures(payees.Object, new FixedClock(Now), alerts?.Object);
        return new Wired(new PayeeCheckouts(
            payees.Object,
            (transactions ?? new Mock<IPaymentTransactionRepository>()).Object,
            (gateway ?? new Mock<IPaymentGateway>()).Object,
            failures));
    }

    private static IVendorContext VendorContext()
    {
        var context = new Mock<IVendorContext>();
        context.Setup(x => x.RequireVendorIdAsync(It.IsAny<CancellationToken>())).ReturnsAsync(4);
        return context.Object;
    }

    private static ICustomerContext CustomerContext()
    {
        var context = new Mock<ICustomerContext>();
        context.Setup(x => x.RequireCustomerUserIdAsync(It.IsAny<CancellationToken>())).ReturnsAsync(2);
        return context.Object;
    }

    private sealed class FixedClock(DateTime now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(now, TimeSpan.Zero);
    }
}
