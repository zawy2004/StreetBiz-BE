using FluentAssertions;
using Moq;
using StreetBiz.Application.Common.Interfaces;
using StreetBiz.Application.Common.Models;
using StreetBiz.Application.Common.Security;
using StreetBiz.Application.Features.Commerce;

namespace StreetBiz.Application.Tests;

/// <summary>
/// The payment alerts every callback can raise, whichever provider sent it: what is alerted,
/// what is not, and that the provider's own transfer id reaches the repositories.
/// </summary>
public sealed class PaymentCallbackAlertTests
{
    [Fact]
    public async Task The_signature_header_reaches_the_gateway_and_the_transfer_id_reaches_the_repository()
    {
        var gateway = new Mock<IPaymentGateway>();
        gateway.Setup(x => x.VerifyCallbackAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<PaymentCallbackHeaders?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PaymentGatewayCallback("REF-101", null, 1_040_000m, "SUCCESS", true, ProviderEventId: "FT92704"));
        var finance = FinanceApplying(PaymentCallbackOutcome.Applied);

        await Handler(gateway, finance).Handle(
            new ProcessPaymentCallbackCommand("momo", "{}", "sha256=abc"), CancellationToken.None);

        gateway.Verify(x => x.VerifyCallbackAsync(
            "MOMO", "{}", new PaymentCallbackHeaders("sha256=abc"), It.IsAny<CancellationToken>()));
        finance.Verify(x => x.ApplyPaymentCallbackAsync(
            It.Is<PaymentCallbackData>(d => d.ProviderEventId == "FT92704" && d.ProviderReference == "REF-101"),
            It.IsAny<CancellationToken>()));
    }

    [Theory]
    [InlineData(PaymentCallbackOutcome.Unmatched, "UNMATCHED", "khớp mã thanh toán")]
    [InlineData(PaymentCallbackOutcome.Surplus, "SURPLUS", "hoàn tiền")]
    [InlineData(PaymentCallbackOutcome.Rejected, "REJECTED", "không khớp")]
    public async Task A_payment_that_cannot_be_applied_is_sent_to_the_alerts(
        PaymentCallbackOutcome outcome, string expectedOutcome, string reasonPart)
    {
        var alerts = new Mock<IPaymentAlertNotifier>();

        var receipt = await Handler(Verified(), FinanceApplying(outcome), alerts).Handle(
            new ProcessPaymentCallbackCommand("momo", "{}", null), CancellationToken.None);

        receipt.Result.Should().Be(expectedOutcome);
        alerts.Verify(x => x.NotifyAsync(
            It.Is<PaymentAlert>(a => a.Provider == "MOMO"
                && a.Outcome == expectedOutcome
                && a.Reason.Contains(reasonPart)
                && a.ProviderReference == "REF-101"
                && a.Amount == 1_040_000m
                && a.ProviderEventId == "FT92704"
                && a.CallbackEventId == 77),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Theory]
    [InlineData(PaymentCallbackOutcome.Applied)]
    [InlineData(PaymentCallbackOutcome.Duplicate)]
    public async Task An_applied_or_repeated_payment_raises_no_alert(PaymentCallbackOutcome outcome)
    {
        var alerts = new Mock<IPaymentAlertNotifier>(MockBehavior.Strict);

        await Handler(Verified(), FinanceApplying(outcome), alerts).Handle(
            new ProcessPaymentCallbackCommand("momo", "{}", null), CancellationToken.None);

        alerts.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task A_bad_signature_is_raised_as_such()
    {
        var gateway = new Mock<IPaymentGateway>();
        gateway.Setup(x => x.VerifyCallbackAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<PaymentCallbackHeaders?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PaymentGatewayCallback("REF-101", null, 1_040_000m, "SUCCESS", false));
        var alerts = new Mock<IPaymentAlertNotifier>();

        await Handler(gateway, FinanceApplying(PaymentCallbackOutcome.Rejected), alerts).Handle(
            new ProcessPaymentCallbackCommand("momo", "{}", "sha256=bad"), CancellationToken.None);

        alerts.Verify(x => x.NotifyAsync(
            It.Is<PaymentAlert>(a => a.Reason.Contains("Chữ ký không hợp lệ")), It.IsAny<CancellationToken>()));
    }

    [Fact]
    public async Task A_genuine_notification_that_is_not_a_streetbiz_payment_is_acknowledged_without_touching_anything()
    {
        var gateway = new Mock<IPaymentGateway>();
        gateway.Setup(x => x.VerifyCallbackAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<PaymentCallbackHeaders?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PaymentGatewayCallback(null, null, null, null, true, Ignored: true));
        var finance = new Mock<IFinanceRepository>(MockBehavior.Strict);
        var commerce = new Mock<ICommerceRepository>(MockBehavior.Strict);
        var alerts = new Mock<IPaymentAlertNotifier>(MockBehavior.Strict);

        var receipt = await new ProcessPaymentCallbackCommandHandler(gateway.Object, commerce.Object, finance.Object, alerts.Object)
            .Handle(new ProcessPaymentCallbackCommand("momo", "{}", null), CancellationToken.None);

        receipt.Result.Should().Be("IGNORED");
    }

    [Fact]
    public async Task A_server_error_while_applying_is_raised_and_still_fails_the_callback_so_the_provider_retries()
    {
        var finance = new Mock<IFinanceRepository>();
        finance.Setup(x => x.FindPaymentPurposeAsync(It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(PaymentPurposes.RentalFee);
        finance.Setup(x => x.ApplyPaymentCallbackAsync(It.IsAny<PaymentCallbackData>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("database is down"));
        var alerts = new Mock<IPaymentAlertNotifier>();

        var act = () => Handler(Verified(), finance, alerts).Handle(
            new ProcessPaymentCallbackCommand("momo", "{}", null), CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>();
        alerts.Verify(x => x.NotifyAsync(
            It.Is<PaymentAlert>(a => a.Outcome == "ERROR" && a.Reason.Contains("database is down")),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    private static Mock<IPaymentGateway> Verified()
    {
        var gateway = new Mock<IPaymentGateway>();
        gateway.Setup(x => x.VerifyCallbackAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<PaymentCallbackHeaders?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PaymentGatewayCallback("REF-101", null, 1_040_000m, "SUCCESS", true, "{}", "FT92704"));
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
}
