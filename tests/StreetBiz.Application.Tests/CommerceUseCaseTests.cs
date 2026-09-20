using FluentAssertions;
using Moq;
using StreetBiz.Application.Common.Exceptions;
using StreetBiz.Application.Common.Interfaces;
using StreetBiz.Application.Common.Models;
using StreetBiz.Application.Common.Security;
using StreetBiz.Application.Features.Commerce;

namespace StreetBiz.Application.Tests;

public sealed class CommerceUseCaseTests
{
    [Fact]
    public async Task Sql_order_timestamps_are_serialized_as_utc_not_local_wall_clock()
    {
        var sqlTime = new DateTime(2026, 9, 19, 10, 30, 0, DateTimeKind.Unspecified);
        var row = Order() with { CreatedAt = sqlTime, PlacedAt = sqlTime,
            StorefrontAddress = "12 Nguyễn Văn Linh, Hải Châu, Đà Nẵng",
            RefundRequestedAt = sqlTime, RefundCompletedAt = sqlTime,
            History = [new(1, null, "PLACED", null, sqlTime)] };
        var repository = new Mock<ICommerceRepository>();
        repository.Setup(x => x.GetCustomerOrderAsync(7, row.OrderId, It.IsAny<CancellationToken>())).ReturnsAsync(row);
        var dto = await new GetCustomerOrderQueryHandler(CustomerContext(), repository.Object)
            .Handle(new(row.OrderId), default);
        Assert.Equal(DateTimeKind.Utc, dto.CreatedAt.Kind);
        Assert.Equal(DateTimeKind.Utc, dto.History[0].ChangedAt.Kind);
        Assert.Equal(DateTimeKind.Utc, dto.RefundCompletedAt!.Value.Kind);
        Assert.Equal(sqlTime.Ticks, dto.CreatedAt.Ticks);
        Assert.Equal("12 Nguyễn Văn Linh, Hải Châu, Đà Nẵng", dto.Storefront.Address);
    }

    [Fact]
    public async Task Add_cart_item_uses_the_verified_customer_and_normalizes_the_note()
    {
        var repository = new Mock<ICommerceRepository>();
        repository.Setup(x => x.AddCartItemAsync(
                7,
                11,
                2,
                "Ít cay",
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CartMutationResult(CartMutationOutcome.Updated, Cart()));

        var result = await new AddCartItemCommandHandler(CustomerContext(), repository.Object)
            .Handle(new AddCartItemCommand(11, 2, "  Ít cay  "), CancellationToken.None);

        result.CartId.Should().Be(3);
        result.Subtotal.Should().Be(50_000);
        repository.VerifyAll();
    }

    [Fact]
    public async Task Place_prepaid_order_normalizes_the_provider_and_idempotency_key()
    {
        var repository = new Mock<ICommerceRepository>();
        repository.Setup(x => x.PlacePrepaidOrderAsync(
                7,
                PaymentProviders.Momo,
                "checkout-1",
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OrderMutationResult(OrderMutationOutcome.Updated, Order()));

        var result = await new PlacePrepaidOrderCommandHandler(CustomerContext(), repository.Object)
            .Handle(new PlacePrepaidOrderCommand("momo", "  checkout-1  "), CancellationToken.None);

        result.OrderStatus.Should().Be(OrderStatuses.Placed);
        result.PaymentProvider.Should().Be(PaymentProviders.Momo);
        repository.VerifyAll();
    }

    [Fact]
    public async Task Checkout_returns_the_flat_payment_contract_and_uses_header_key()
    {
        var row = Order(OrderStatuses.PendingPayment) with
        {
            PaymentTransactionId = 42,
            PaymentIdempotencyKey = "checkout-header-key",
            PaymentAmount = 50_000,
            PaymentProvider = PaymentProviders.Momo,
            PaymentStatus = "PENDING",
        };
        var repository = new Mock<ICommerceRepository>();
        repository.Setup(x => x.CheckoutAsync(
                7, 3, PaymentProviders.Momo, "checkout-header-key",
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OrderMutationResult(OrderMutationOutcome.Updated, row));
        var gateway = new Mock<IPaymentGateway>();
        gateway.Setup(x => x.CreateCheckoutAsync(
                It.Is<PaymentGatewayCheckoutRequest>(request =>
                    request.TransactionId == 42
                    && request.Amount == 50_000
                    && request.IdempotencyKey == "checkout-header-key"),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PaymentGatewayCheckoutResult(
                "streetbiz://payment/momo", null));

        var result = await new CheckoutOrderCommandHandler(
                CustomerContext(), repository.Object, gateway.Object)
            .Handle(new CheckoutOrderCommand(
                3, "momo", "  checkout-header-key  "), default);

        result.OrderId.Should().Be(row.OrderId);
        result.OrderStatus.Should().Be(OrderStatuses.PendingPayment);
        result.PaymentTransactionId.Should().Be(42);
        result.Amount.Should().Be(50_000);
        result.PaymentUrl.Should().Be("streetbiz://payment/momo");
        repository.VerifyAll();
        gateway.VerifyAll();
    }

    [Fact]
    public async Task Empty_cart_is_reported_as_a_domain_rule_failure()
    {
        var repository = new Mock<ICommerceRepository>();
        repository.Setup(x => x.PlacePrepaidOrderAsync(
                7,
                PaymentProviders.ZaloPay,
                "checkout-2",
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OrderMutationResult(OrderMutationOutcome.EmptyCart, null));
        var handler = new PlacePrepaidOrderCommandHandler(CustomerContext(), repository.Object);

        var action = () => handler.Handle(
            new PlacePrepaidOrderCommand("ZALOPAY", "checkout-2"),
            CancellationToken.None);

        await action.Should().ThrowAsync<DomainRuleException>()
            .WithMessage(CommerceMessages.CartEmpty);
    }

    [Fact]
    public async Task Stale_customer_order_status_is_reported_as_a_conflict()
    {
        var repository = new Mock<ICommerceRepository>();
        repository.Setup(x => x.CancelCustomerOrderAsync(
                7,
                15,
                OrderStatuses.Placed,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OrderMutationResult(OrderMutationOutcome.Conflict, null));
        var handler = new CancelCustomerOrderCommandHandler(CustomerContext(), repository.Object);

        var action = () => handler.Handle(
            new CancelCustomerOrderCommand(15, OrderStatuses.Placed),
            CancellationToken.None);

        await action.Should().ThrowAsync<ConflictException>()
            .WithMessage(CommerceMessages.OrderConflict);
    }

    [Fact]
    public async Task Customer_order_tracking_includes_the_latest_refund_state()
    {
        var repository = new Mock<ICommerceRepository>();
        repository.Setup(x => x.GetCustomerOrderAsync(7, 15, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Order(OrderStatuses.Cancelled, refundStatus: "SUCCESS"));

        var result = await new GetCustomerOrderQueryHandler(CustomerContext(), repository.Object)
            .Handle(new GetCustomerOrderQuery(15), CancellationToken.None);

        result.RefundAmount.Should().Be(50_000);
        result.RefundReason.Should().Be("ORDER_CANCELLED");
        result.RefundStatus.Should().Be("SUCCESS");
        result.RefundRequestedAt.Should().NotBeNull();
        result.RefundCompletedAt.Should().NotBeNull();
    }

    [Fact]
    public async Task Seller_rejection_uses_the_verified_vendor_actor_and_trimmed_reason()
    {
        var repository = new Mock<ICommerceRepository>();
        repository.Setup(x => x.DecideSellerOrderAsync(
                4,
                8,
                15,
                SellerOrderDecisions.Reject,
                "Món đã hết",
                OrderStatuses.Placed,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OrderMutationResult(
                OrderMutationOutcome.Updated,
                Order(OrderStatuses.Rejected, "Món đã hết")));
        var currentUser = new Mock<ICurrentUser>();
        currentUser.SetupGet(x => x.UserId).Returns(8);

        var result = await new DecideSellerOrderCommandHandler(
                VendorContext(),
                currentUser.Object,
                repository.Object)
            .Handle(
                new DecideSellerOrderCommand(
                    15,
                    "reject",
                    "  Món đã hết  ",
                    OrderStatuses.Placed),
                CancellationToken.None);

        result.OrderStatus.Should().Be(OrderStatuses.Rejected);
        result.RejectionReason.Should().Be("Món đã hết");
        repository.VerifyAll();
    }

    [Fact]
    public async Task Sales_summary_normalizes_the_period_and_uses_vendor_scope()
    {
        var from = new DateTime(2026, 9, 12, 0, 0, 0, DateTimeKind.Utc);
        var to = from.AddDays(7);
        var repository = new Mock<ICommerceRepository>();
        repository.Setup(x => x.GetSalesSummaryAsync(
                4,
                SalesPeriods.Week,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CommerceSalesSummaryRow(
                SalesPeriods.Week,
                from,
                to,
                1,
                50_000,
                10_000,
                40_000,
                [Order(OrderStatuses.Completed)]));

        var result = await new GetSalesSummaryQueryHandler(VendorContext(), repository.Object)
            .Handle(new GetSalesSummaryQuery("week"), CancellationToken.None);

        result.Period.Should().Be(SalesPeriods.Week);
        result.CompletedOrderCount.Should().Be(1);
        result.GrossSales.Should().Be(50_000);
        result.RefundedAmount.Should().Be(10_000);
        result.NetSales.Should().Be(40_000);
        repository.VerifyAll();
    }

    [Fact]
    public void Seller_rejection_requires_a_reason()
    {
        var result = new DecideSellerOrderCommandValidator().Validate(
            new DecideSellerOrderCommand(
                15,
                SellerOrderDecisions.Reject,
                null,
                OrderStatuses.Placed));

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(error => error.PropertyName == "Reason");
    }

    [Fact]
    public void Seller_order_filter_rejects_customer_only_pending_payment_status()
    {
        var result = new ListSellerOrdersQueryValidator().Validate(
            new ListSellerOrdersQuery(OrderStatuses.PendingPayment));

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(error => error.PropertyName == "Status");
    }

    [Theory]
    [InlineData(SalesPeriods.Day, "2026-09-17T17:00:00Z")]
    [InlineData(SalesPeriods.Week, "2026-09-13T17:00:00Z")]
    [InlineData(SalesPeriods.Month, "2026-08-31T17:00:00Z")]
    public void Sales_period_starts_at_midnight_in_Vietnam(
        string period,
        string expectedFromUtc)
    {
        var now = DateTimeOffset.Parse("2026-09-18T00:30:00Z");
        var timeZone = TimeZoneInfo.FindSystemTimeZoneById("Asia/Ho_Chi_Minh");

        var result = SalesPeriodWindowCalculator.Calculate(period, now, timeZone);

        result.FromUtc.Should().Be(DateTime.Parse(
            expectedFromUtc,
            null,
            System.Globalization.DateTimeStyles.AdjustToUniversal));
        result.ToUtc.Should().Be(now.UtcDateTime);
    }

    private static ICustomerContext CustomerContext()
    {
        var context = new Mock<ICustomerContext>();
        context.Setup(x => x.RequireCustomerUserIdAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(7);
        return context.Object;
    }

    private static IVendorContext VendorContext()
    {
        var context = new Mock<IVendorContext>();
        context.Setup(x => x.RequireVendorIdAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(4);
        return context.Object;
    }

    private static CommerceCartRow Cart() => new(
        3,
        9,
        "Bếp Việt",
        "OPEN",
        [new CommerceCartItemRow(5, 11, "Bánh mì", null, 25_000, "AVAILABLE", 2, "Ít cay")],
        50_000);

    private static CommerceOrderRow Order(
        string status = OrderStatuses.Placed,
        string? rejectionReason = null,
        string? refundStatus = null) => new(
        15,
        "ORD-000015",
        7,
        "Khách hàng",
        9,
        "Bếp Việt",
        status,
        50_000,
        50_000,
        rejectionReason,
        PaymentProviders.Momo,
        "SUCCESS",
        refundStatus is null ? null : 50_000,
        refundStatus is null ? null : "ORDER_CANCELLED",
        refundStatus,
        refundStatus is null ? null : DateTime.UtcNow.AddMinutes(-1),
        refundStatus == "SUCCESS" ? DateTime.UtcNow : null,
        DateTime.UtcNow,
        status == OrderStatuses.Completed ? DateTime.UtcNow : null,
        DateTime.UtcNow,
        [new CommerceOrderItemRow(20, 11, "Bánh mì", 25_000, 2, "Ít cay")],
        []);
}
