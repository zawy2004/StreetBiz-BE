using FluentAssertions;
using Moq;
using StreetBiz.Application.Common.Exceptions;
using StreetBiz.Application.Common.Interfaces;
using StreetBiz.Application.Common.Models;
using StreetBiz.Application.Common.Security;
using StreetBiz.Application.Features.Commerce;
using StreetBiz.Application.Features.Finance.PayFee;
using StreetBiz.Application.Features.Finance.PayPenalty;

namespace StreetBiz.Application.Tests;

public sealed class FinancePaymentTests
{
    [Fact]
    public async Task Pay_fee_opens_a_checkout_for_an_outstanding_instalment()
    {
        var finance = new Mock<IFinanceRepository>();
        finance.Setup(x => x.GetFeeItemForCheckoutAsync(4, 2, It.IsAny<CancellationToken>()))
            .ReturnsAsync(FeeItem(status: FeeItemStatuses.Overdue));
        finance.Setup(x => x.CreateFeeCheckoutAsync(4, 2, "MOMO", "idem-1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new FinanceCheckoutTransactionRow(101, "idem-1", "MOMO", 1_040_000m));
        var gateway = new Mock<IPaymentGateway>();
        gateway.Setup(x => x.CreateCheckoutAsync(
                It.Is<PaymentGatewayCheckoutRequest>(r => r.ReferenceId == 2 && r.TransactionId == 101),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PaymentGatewayCheckoutResult("streetbiz://pay/momo", null));

        var result = await new PayFeeCommandHandler(VendorContext(), finance.Object, gateway.Object)
            .Handle(new PayFeeCommand(2, "momo", "idem-1"), CancellationToken.None);

        result.TransactionId.Should().Be(101);
        result.Purpose.Should().Be(PaymentPurposes.RentalFee);
        result.PaymentUrl.Should().Be("streetbiz://pay/momo");
    }

    [Fact]
    public async Task Pay_fee_refuses_an_already_paid_instalment_without_opening_a_checkout()
    {
        var finance = new Mock<IFinanceRepository>();
        finance.Setup(x => x.GetFeeItemForCheckoutAsync(4, 1, It.IsAny<CancellationToken>()))
            .ReturnsAsync(FeeItem(status: FeeItemStatuses.Paid));

        var act = () => new PayFeeCommandHandler(VendorContext(), finance.Object, Mock.Of<IPaymentGateway>())
            .Handle(new PayFeeCommand(1, "momo", "idem-1"), CancellationToken.None);

        await act.Should().ThrowAsync<DomainRuleException>();
        finance.Verify(x => x.CreateFeeCheckoutAsync(
            It.IsAny<long>(), It.IsAny<long>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task Pay_fee_reports_a_fee_item_belonging_to_someone_else_as_not_found()
    {
        var finance = new Mock<IFinanceRepository>();
        finance.Setup(x => x.GetFeeItemForCheckoutAsync(4, 99, It.IsAny<CancellationToken>()))
            .ReturnsAsync((FeeItemCheckoutRow?)null);

        var act = () => new PayFeeCommandHandler(VendorContext(), finance.Object, Mock.Of<IPaymentGateway>())
            .Handle(new PayFeeCommand(99, "momo", "idem-1"), CancellationToken.None);

        await act.Should().ThrowAsync<NotFoundException>();
    }

    [Fact]
    public async Task Pay_penalty_refuses_a_waived_penalty()
    {
        var finance = new Mock<IFinanceRepository>();
        finance.Setup(x => x.GetPenaltyForCheckoutAsync(4, 3, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PenaltyCheckoutRow(3, 4, 5, "Đổ nước thải ra vỉa hè", "NVL-01", 750_000m, PenaltyStatuses.Waived));

        var act = () => new PayPenaltyCommandHandler(VendorContext(), finance.Object, Mock.Of<IPaymentGateway>())
            .Handle(new PayPenaltyCommand(3, "zalopay", "idem-2"), CancellationToken.None);

        await act.Should().ThrowAsync<DomainRuleException>();
    }

    [Fact]
    public async Task Pay_penalty_opens_a_checkout_for_an_unpaid_penalty()
    {
        var finance = new Mock<IFinanceRepository>();
        finance.Setup(x => x.GetPenaltyForCheckoutAsync(4, 2, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PenaltyCheckoutRow(2, 4, 5, "Kê bàn ghế chắn lối đi bộ", "NVL-08", 1_000_000m, PenaltyStatuses.Unpaid));
        finance.Setup(x => x.CreatePenaltyCheckoutAsync(4, 2, "ZALOPAY", "idem-2", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new FinanceCheckoutTransactionRow(202, "idem-2", "ZALOPAY", 1_000_000m));
        var gateway = new Mock<IPaymentGateway>();
        gateway.Setup(x => x.CreateCheckoutAsync(It.IsAny<PaymentGatewayCheckoutRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PaymentGatewayCheckoutResult("streetbiz://pay/zalopay", "ZALO-REF-1"));

        var result = await new PayPenaltyCommandHandler(VendorContext(), finance.Object, gateway.Object)
            .Handle(new PayPenaltyCommand(2, "zalopay", "idem-2"), CancellationToken.None);

        result.Purpose.Should().Be(PaymentPurposes.Penalty);
        result.TransactionId.Should().Be(202);
        finance.Verify(x => x.SetPaymentProviderReferenceAsync(202, "ZALO-REF-1", It.IsAny<CancellationToken>()));
    }

    [Fact]
    public async Task Callback_dispatches_a_rental_fee_purpose_to_finance_not_commerce()
    {
        var gateway = new Mock<IPaymentGateway>();
        gateway.Setup(x => x.VerifyCallbackAsync("MOMO", "payload", "sig", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PaymentGatewayCallback("MOMO-REF", "idem-1", 1_040_000m, "SUCCESS", true));
        var commerce = new Mock<ICommerceRepository>(MockBehavior.Strict);
        var finance = new Mock<IFinanceRepository>();
        finance.Setup(x => x.FindPaymentPurposeAsync("MOMO-REF", "idem-1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(PaymentPurposes.RentalFee);
        finance.Setup(x => x.ApplyPaymentCallbackAsync(It.IsAny<PaymentCallbackData>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new FinanceCallbackMutationResult(PaymentCallbackOutcome.Applied, 55, 101));

        var receipt = await new ProcessPaymentCallbackCommandHandler(gateway.Object, commerce.Object, finance.Object)
            .Handle(new ProcessPaymentCallbackCommand("momo", "payload", "sig"), CancellationToken.None);

        receipt.Result.Should().Be("APPLIED");
        receipt.CallbackEventId.Should().Be(55);
        receipt.OrderId.Should().BeNull();
        receipt.OrderStatus.Should().BeNull();
        commerce.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Callback_falls_back_to_commerce_when_the_purpose_is_an_order_or_unknown()
    {
        var gateway = new Mock<IPaymentGateway>();
        gateway.Setup(x => x.VerifyCallbackAsync("MOMO", "payload", "sig", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PaymentGatewayCallback("MOMO-REF", "idem-9", 25_000m, "SUCCESS", true));
        var finance = new Mock<IFinanceRepository>();
        finance.Setup(x => x.FindPaymentPurposeAsync("MOMO-REF", "idem-9", It.IsAny<CancellationToken>()))
            .ReturnsAsync(PaymentPurposes.Order);
        var commerce = new Mock<ICommerceRepository>();
        commerce.Setup(x => x.ApplyPaymentCallbackAsync(It.IsAny<PaymentCallbackData>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PaymentCallbackMutationResult(PaymentCallbackOutcome.Applied, 56, 9, 8, "PLACED"));

        var receipt = await new ProcessPaymentCallbackCommandHandler(gateway.Object, commerce.Object, finance.Object)
            .Handle(new ProcessPaymentCallbackCommand("momo", "payload", "sig"), CancellationToken.None);

        receipt.OrderId.Should().Be(8);
        receipt.OrderStatus.Should().Be("PLACED");
        finance.Verify(x => x.ApplyPaymentCallbackAsync(It.IsAny<PaymentCallbackData>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    private static FeeItemCheckoutRow FeeItem(string status) => new(
        2, 4, 5, "NVL-01", 2, 3, new DateOnly(2026, 10, 2), 1_040_000m, status);

    private static IVendorContext VendorContext()
    {
        var context = new Mock<IVendorContext>();
        context.Setup(x => x.RequireVendorIdAsync(It.IsAny<CancellationToken>())).ReturnsAsync(4);
        return context.Object;
    }
}
