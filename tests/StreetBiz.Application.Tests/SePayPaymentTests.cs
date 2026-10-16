using FluentAssertions;
using Moq;
using StreetBiz.Application.Common.Exceptions;
using StreetBiz.Application.Common.Interfaces;
using StreetBiz.Application.Common.Models;
using StreetBiz.Application.Common.Security;
using StreetBiz.Application.Features.Commerce;
using StreetBiz.Application.Features.Finance.BankTransfer;
using StreetBiz.Application.Features.Finance.PayFee;

namespace StreetBiz.Application.Tests;

/// <summary>SePay bank transfers: the VietQR at checkout, the webhook and the alerts it raises.</summary>
public sealed class SePayPaymentTests
{
    private static readonly BankTransferInstructions Qr = new(
        "MBBank", "0123456789", "UBND PHUONG HAI CHAU 1", 1_040_000m, "SBP101",
        "https://qr.sepay.vn/img?acc=0123456789&bank=MBBank&amount=1040000&des=SBP101");

    [Fact]
    public async Task Paying_a_fee_by_sepay_returns_the_vietqr_and_keeps_the_payment_code_as_its_reference()
    {
        var finance = new Mock<IFinanceRepository>();
        finance.Setup(x => x.GetFeeItemForCheckoutAsync(4, 2, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new FeeItemCheckoutRow(2, 4, 5, "NVL-01", 2, 3, new DateOnly(2026, 10, 2), 1_040_000m, FeeItemStatuses.Pending));
        finance.Setup(x => x.CreateFeeCheckoutAsync(4, 2, "SEPAY", "idem-1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new FinanceCheckoutTransactionRow(101, "idem-1", "SEPAY", 1_040_000m));
        var gateway = new Mock<IPaymentGateway>();
        gateway.Setup(x => x.IsProviderAvailable("SEPAY")).Returns(true);
        gateway.Setup(x => x.CreateCheckoutAsync(It.IsAny<PaymentGatewayCheckoutRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PaymentGatewayCheckoutResult(Qr.QrImageUrl, "SBP101", Qr));

        var result = await new PayFeeCommandHandler(VendorContext(), finance.Object, gateway.Object)
            .Handle(new PayFeeCommand(2, "sepay", "idem-1"), CancellationToken.None);

        result.Provider.Should().Be("SEPAY");
        result.BankTransfer.Should().NotBeNull();
        result.BankTransfer!.TransferContent.Should().Be("SBP101");
        result.BankTransfer.QrImageUrl.Should().Be(Qr.QrImageUrl);
        finance.Verify(x => x.SetPaymentProviderReferenceAsync(101, "SBP101", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public void Sepay_is_an_accepted_provider_for_checkout()
    {
        new PayFeeCommandValidator().Validate(new PayFeeCommand(2, "sepay", "idem-1")).IsValid.Should().BeTrue();
        new CheckoutOrderCommandValidator().Validate(new CheckoutOrderCommand(1, "SEPAY", "idem-1")).IsValid.Should().BeTrue();
    }

    [Fact]
    public async Task The_webhook_headers_reach_the_gateway()
    {
        var gateway = new Mock<IPaymentGateway>();
        gateway.Setup(x => x.VerifyCallbackAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<PaymentCallbackHeaders?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PaymentGatewayCallback("SBP101", null, 1_040_000m, "SUCCESS", true, ProviderEventId: "92704"));
        var finance = FinanceApplying(PaymentCallbackOutcome.Applied);

        await Handler(gateway, finance).Handle(
            new ProcessPaymentCallbackCommand("sepay", "{}", "sha256=abc", "1790000000", "Apikey k"),
            CancellationToken.None);

        gateway.Verify(x => x.VerifyCallbackAsync(
            "SEPAY", "{}", new PaymentCallbackHeaders("sha256=abc", "1790000000", "Apikey k"), It.IsAny<CancellationToken>()));
        finance.Verify(x => x.ApplyPaymentCallbackAsync(
            It.Is<PaymentCallbackData>(d => d.ProviderEventId == "92704" && d.ProviderReference == "SBP101"),
            It.IsAny<CancellationToken>()));
    }

    [Theory]
    [InlineData(PaymentCallbackOutcome.Unmatched, "UNMATCHED", "khớp mã thanh toán")]
    [InlineData(PaymentCallbackOutcome.Surplus, "SURPLUS", "hoàn tiền")]
    [InlineData(PaymentCallbackOutcome.Rejected, "REJECTED", "không khớp")]
    public async Task A_transfer_that_cannot_be_applied_is_sent_to_the_alerts(
        PaymentCallbackOutcome outcome, string expectedOutcome, string reasonPart)
    {
        var alerts = new Mock<IPaymentAlertNotifier>();
        var finance = FinanceApplying(outcome);

        var receipt = await Handler(SePayWebhook(), finance, alerts).Handle(
            new ProcessPaymentCallbackCommand("sepay", "{}", null), CancellationToken.None);

        receipt.Result.Should().Be(expectedOutcome);
        alerts.Verify(x => x.NotifyAsync(
            It.Is<PaymentAlert>(a => a.Provider == "SEPAY"
                && a.Outcome == expectedOutcome
                && a.Reason.Contains(reasonPart)
                && a.ProviderReference == "SBP101"
                && a.Amount == 1_040_000m
                && a.ProviderEventId == "92704"
                && a.CallbackEventId == 77),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Theory]
    [InlineData(PaymentCallbackOutcome.Applied)]
    [InlineData(PaymentCallbackOutcome.Duplicate)]
    public async Task An_applied_or_repeated_transfer_raises_no_alert(PaymentCallbackOutcome outcome)
    {
        var alerts = new Mock<IPaymentAlertNotifier>(MockBehavior.Strict);

        await Handler(SePayWebhook(), FinanceApplying(outcome), alerts).Handle(
            new ProcessPaymentCallbackCommand("sepay", "{}", null), CancellationToken.None);

        alerts.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task A_bad_signature_is_raised_as_such()
    {
        var gateway = new Mock<IPaymentGateway>();
        gateway.Setup(x => x.VerifyCallbackAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<PaymentCallbackHeaders?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PaymentGatewayCallback("SBP101", null, 1_040_000m, "SUCCESS", false));
        var alerts = new Mock<IPaymentAlertNotifier>();

        await Handler(gateway, FinanceApplying(PaymentCallbackOutcome.Rejected), alerts).Handle(
            new ProcessPaymentCallbackCommand("sepay", "{}", "sha256=bad", "1"), CancellationToken.None);

        alerts.Verify(x => x.NotifyAsync(
            It.Is<PaymentAlert>(a => a.Reason.Contains("Chữ ký hoặc API key")), It.IsAny<CancellationToken>()));
    }

    [Fact]
    public async Task Money_leaving_the_account_is_acknowledged_without_touching_any_payment()
    {
        var gateway = new Mock<IPaymentGateway>();
        gateway.Setup(x => x.VerifyCallbackAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<PaymentCallbackHeaders?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PaymentGatewayCallback(null, null, null, null, true, Ignored: true));
        var finance = new Mock<IFinanceRepository>(MockBehavior.Strict);
        var commerce = new Mock<ICommerceRepository>(MockBehavior.Strict);
        var alerts = new Mock<IPaymentAlertNotifier>(MockBehavior.Strict);

        var receipt = await new ProcessPaymentCallbackCommandHandler(gateway.Object, commerce.Object, finance.Object, alerts.Object)
            .Handle(new ProcessPaymentCallbackCommand("sepay", "{}", null), CancellationToken.None);

        receipt.Result.Should().Be("IGNORED");
    }

    [Fact]
    public async Task A_server_error_while_applying_is_raised_and_still_fails_the_callback_so_sepay_retries()
    {
        var finance = new Mock<IFinanceRepository>();
        finance.Setup(x => x.FindPaymentPurposeAsync(It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(PaymentPurposes.RentalFee);
        finance.Setup(x => x.ApplyPaymentCallbackAsync(It.IsAny<PaymentCallbackData>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("database is down"));
        var alerts = new Mock<IPaymentAlertNotifier>();

        var act = () => Handler(SePayWebhook(), finance, alerts).Handle(
            new ProcessPaymentCallbackCommand("sepay", "{}", null), CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>();
        alerts.Verify(x => x.NotifyAsync(
            It.Is<PaymentAlert>(a => a.Outcome == "ERROR" && a.Reason.Contains("database is down")),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task The_qr_of_a_pending_fee_payment_can_be_shown_again()
    {
        var finance = new Mock<IFinanceRepository>();
        finance.Setup(x => x.GetVendorPaymentAsync(4, 101, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new FinancePaymentStateRow(101, "SEPAY", "SBP101", "idem-1", PaymentStatuses.Pending, 1_040_000m));
        var gateway = new Mock<IPaymentGateway>();
        gateway.Setup(x => x.DescribeBankTransfer("SEPAY", "SBP101", 1_040_000m)).Returns(Qr);

        var result = await new GetFinanceBankTransferQueryHandler(VendorContext(), finance.Object, gateway.Object)
            .Handle(new GetFinanceBankTransferQuery(101), CancellationToken.None);

        result.TransferContent.Should().Be("SBP101");
        result.AccountNumber.Should().Be("0123456789");
    }

    [Fact]
    public async Task A_settled_payment_has_no_qr_to_show()
    {
        var finance = new Mock<IFinanceRepository>();
        finance.Setup(x => x.GetVendorPaymentAsync(4, 101, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new FinancePaymentStateRow(101, "SEPAY", "SBP101", "idem-1", PaymentStatuses.Success, 1_040_000m));
        var gateway = new Mock<IPaymentGateway>(MockBehavior.Strict);

        var act = () => new GetFinanceBankTransferQueryHandler(VendorContext(), finance.Object, gateway.Object)
            .Handle(new GetFinanceBankTransferQuery(101), CancellationToken.None);

        await act.Should().ThrowAsync<NotFoundException>();
    }

    [Fact]
    public async Task The_qr_of_an_order_waiting_for_its_transfer_can_be_shown_again()
    {
        var customer = new Mock<ICustomerContext>();
        customer.Setup(x => x.RequireCustomerUserIdAsync(It.IsAny<CancellationToken>())).ReturnsAsync(2);
        var commerce = new Mock<ICommerceRepository>();
        commerce.Setup(x => x.GetCustomerOrderAsync(2, 19, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Order(OrderStatuses.PendingPayment));
        var gateway = new Mock<IPaymentGateway>();
        gateway.Setup(x => x.DescribeBankTransfer("SEPAY", "SBP55", 25_000m)).Returns(Qr with { Amount = 25_000m, TransferContent = "SBP55" });

        var result = await new GetOrderBankTransferQueryHandler(customer.Object, commerce.Object, gateway.Object)
            .Handle(new GetOrderBankTransferQuery(19), CancellationToken.None);

        result.TransferContent.Should().Be("SBP55");
        result.Amount.Should().Be(25_000m);
    }

    private static CommerceOrderRow Order(string status) => new(
        19, "SB-000019", 2, "Khách", 1, "Bếp Việt", status, 25_000m, 25_000m, null, "SEPAY", "PENDING",
        null, null, null, null, null, null, null, DateTime.UtcNow, [], [],
        PaymentTransactionId: 55, PaymentIdempotencyKey: "idem-o", PaymentAmount: 25_000m, PaymentProviderReference: "SBP55");

    private static Mock<IPaymentGateway> SePayWebhook()
    {
        var gateway = new Mock<IPaymentGateway>();
        gateway.Setup(x => x.VerifyCallbackAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<PaymentCallbackHeaders?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PaymentGatewayCallback("SBP101", null, 1_040_000m, "SUCCESS", true, "{}", "92704"));
        return gateway;
    }

    private static Mock<IFinanceRepository> FinanceApplying(PaymentCallbackOutcome outcome)
    {
        var finance = new Mock<IFinanceRepository>();
        finance.Setup(x => x.FindPaymentPurposeAsync(It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(PaymentPurposes.RentalFee);
        finance.Setup(x => x.ApplyPaymentCallbackAsync(It.IsAny<PaymentCallbackData>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new FinanceCallbackMutationResult(outcome, 77, 101));
        return finance;
    }

    private static ProcessPaymentCallbackCommandHandler Handler(
        Mock<IPaymentGateway> gateway, Mock<IFinanceRepository> finance, Mock<IPaymentAlertNotifier>? alerts = null) =>
        new(gateway.Object, Mock.Of<ICommerceRepository>(), finance.Object, alerts?.Object);

    private static IVendorContext VendorContext()
    {
        var context = new Mock<IVendorContext>();
        context.Setup(x => x.RequireVendorIdAsync(It.IsAny<CancellationToken>())).ReturnsAsync(4);
        return context.Object;
    }
}
