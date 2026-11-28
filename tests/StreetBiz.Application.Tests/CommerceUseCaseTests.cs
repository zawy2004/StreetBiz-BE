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
        gateway.Setup(x => x.IsProviderAvailable(PaymentProviders.Momo)).Returns(true);
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

    [Theory]
    [InlineData(OrderStatuses.Cancelled, "thất bại")]
    [InlineData(OrderStatuses.Placed, "đã được thanh toán")]
    public async Task Replaying_a_checkout_whose_payment_already_ended_never_opens_a_new_payment_page(
        string status, string expectedFragment)
    {
        var repository = new Mock<ICommerceRepository>();
        repository.Setup(x => x.CheckoutAsync(7, 3, PaymentProviders.Momo, "old-key", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OrderMutationResult(
                OrderMutationOutcome.Updated,
                Order(status) with
                {
                    PaymentTransactionId = 42,
                    PaymentIdempotencyKey = "old-key",
                    PaymentAmount = 50_000,
                    PaymentProvider = PaymentProviders.Momo,
                    PaymentStatus = status == OrderStatuses.Cancelled ? "FAILED" : "SUCCESS",
                }));
        var gateway = new Mock<IPaymentGateway>(MockBehavior.Strict);
        gateway.Setup(x => x.IsProviderAvailable(PaymentProviders.Momo)).Returns(true);

        var action = () => new CheckoutOrderCommandHandler(CustomerContext(), repository.Object, gateway.Object)
            .Handle(new CheckoutOrderCommand(3, "momo", "old-key"), default);

        (await action.Should().ThrowAsync<ConflictException>()).Which.Message.Should().Contain(expectedFragment);
        // The strict gateway mock fails the test if a checkout was created for the dead order.
        gateway.Verify(x => x.CreateCheckoutAsync(It.IsAny<PaymentGatewayCheckoutRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Checkout_refuses_an_unconfigured_provider_before_writing_an_order()
    {
        var repository = new Mock<ICommerceRepository>(MockBehavior.Strict);
        var gateway = new Mock<IPaymentGateway>();
        gateway.Setup(x => x.IsProviderAvailable(PaymentProviders.Momo)).Returns(false);
        var handler = new CheckoutOrderCommandHandler(
            CustomerContext(), repository.Object, gateway.Object);

        var action = () => handler.Handle(
            new CheckoutOrderCommand(3, "momo", "checkout-unconfigured"), default);

        await action.Should().ThrowAsync<DomainRuleException>();
        // The strict repository mock fails the test if any order was written.
        repository.VerifyNoOtherCalls();
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

    [Fact]
    public async Task Changing_a_cart_line_trims_the_note_and_returns_the_cart()
    {
        var repository = new Mock<ICommerceRepository>();
        repository.Setup(x => x.UpdateCartItemAsync(7, 11, 3, "Không hành", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CartMutationResult(CartMutationOutcome.Updated, Cart()));

        var cart = await new UpdateCartItemCommandHandler(CustomerContext(), repository.Object)
            .Handle(new UpdateCartItemCommand(11, 3, "  Không hành  "), CancellationToken.None);

        cart.CartId.Should().Be(3);
        repository.VerifyAll();
    }

    [Theory]
    [InlineData(CartMutationOutcome.NotFound, typeof(NotFoundException))]
    [InlineData(CartMutationOutcome.MenuItemUnavailable, typeof(DomainRuleException))]
    [InlineData(CartMutationOutcome.StorefrontUnavailable, typeof(DomainRuleException))]
    [InlineData(CartMutationOutcome.CheckoutPending, typeof(ConflictException))]
    [InlineData(CartMutationOutcome.QuantityLimit, typeof(DomainRuleException))]
    [InlineData(CartMutationOutcome.Conflict, typeof(ConflictException))]
    public async Task A_cart_line_that_cannot_change_says_why(CartMutationOutcome outcome, Type expected)
    {
        var repository = new Mock<ICommerceRepository>();
        repository.Setup(x => x.UpdateCartItemAsync(7, 11, 3, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CartMutationResult(outcome, null));

        var act = () => new UpdateCartItemCommandHandler(CustomerContext(), repository.Object)
            .Handle(new UpdateCartItemCommand(11, 3, "   "), CancellationToken.None);

        (await act.Should().ThrowAsync<Exception>()).Which.Should().BeOfType(expected);
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(1, true)]
    [InlineData(99, true)]
    [InlineData(100, false)]
    public void A_cart_line_holds_one_to_ninety_nine(int quantity, bool valid) =>
        new UpdateCartItemCommandValidator().Validate(new UpdateCartItemCommand(11, quantity, null))
            .IsValid.Should().Be(valid);

    [Fact]
    public async Task Removing_the_last_line_returns_no_cart()
    {
        var repository = new Mock<ICommerceRepository>();
        repository.Setup(x => x.RemoveCartItemAsync(7, 11, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CartMutationResult(CartMutationOutcome.Updated, null));

        var cart = await new RemoveCartItemCommandHandler(CustomerContext(), repository.Object)
            .Handle(new RemoveCartItemCommand(11), CancellationToken.None);

        cart.Should().BeNull();
    }

    [Theory]
    [InlineData(CartMutationOutcome.NotFound, typeof(NotFoundException))]
    [InlineData(CartMutationOutcome.CheckoutPending, typeof(ConflictException))]
    [InlineData(CartMutationOutcome.Conflict, typeof(ConflictException))]
    public async Task A_line_that_cannot_be_removed_says_why(CartMutationOutcome outcome, Type expected)
    {
        var repository = new Mock<ICommerceRepository>();
        repository.Setup(x => x.RemoveCartItemAsync(7, 11, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CartMutationResult(outcome, null));

        var act = () => new RemoveCartItemCommandHandler(CustomerContext(), repository.Object)
            .Handle(new RemoveCartItemCommand(11), CancellationToken.None);

        (await act.Should().ThrowAsync<Exception>()).Which.Should().BeOfType(expected);
    }

    [Fact]
    public async Task Clearing_the_cart_clears_the_callers_own()
    {
        var repository = new Mock<ICommerceRepository>();

        await new ClearCartCommandHandler(CustomerContext(), repository.Object).Handle(new ClearCartCommand(), CancellationToken.None);

        repository.Verify(x => x.ClearCartAsync(7, null, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Clearing_one_stalls_cart_names_that_stall()
    {
        var repository = new Mock<ICommerceRepository>();

        await new ClearCartCommandHandler(CustomerContext(), repository.Object).Handle(new ClearCartCommand(42), CancellationToken.None);

        repository.Verify(x => x.ClearCartAsync(7, 42, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Listing_the_carts_returns_the_callers_own_drafts_one_per_stall()
    {
        var repository = new Mock<ICommerceRepository>();
        repository.Setup(x => x.GetActiveCartsAsync(7, It.IsAny<CancellationToken>())).ReturnsAsync(new[]
        {
            new CommerceCartRow(1, 10, "Stall A", "OPEN", Array.Empty<CommerceCartItemRow>(), 0, null),
            new CommerceCartRow(2, 20, "Stall B", "OPEN", Array.Empty<CommerceCartItemRow>(), 0, null),
        });

        var carts = await new GetCartsQueryHandler(CustomerContext(), repository.Object).Handle(new GetCartsQuery(), CancellationToken.None);

        carts.Select(c => c.StorefrontId).Should().Equal(10, 20);
    }

    [Fact]
    public async Task A_seller_moves_an_order_on_as_the_signed_in_user()
    {
        var user = new Mock<ICurrentUser>();
        user.SetupGet(x => x.UserId).Returns(40);
        var repository = new Mock<ICommerceRepository>();
        repository.Setup(x => x.UpdateSellerOrderStatusAsync(4, 40, 15, OrderStatuses.ReadyForPickup, OrderStatuses.Preparing, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OrderMutationResult(OrderMutationOutcome.Updated, Order(OrderStatuses.ReadyForPickup)));

        var order = await new UpdateSellerOrderStatusCommandHandler(VendorContext(), user.Object, repository.Object)
            .Handle(new UpdateSellerOrderStatusCommand(15, OrderStatuses.ReadyForPickup, OrderStatuses.Preparing), CancellationToken.None);

        order.OrderStatus.Should().Be(OrderStatuses.ReadyForPickup);
    }

    [Fact]
    public async Task A_seller_order_that_moved_on_meanwhile_is_a_conflict()
    {
        var user = new Mock<ICurrentUser>();
        user.SetupGet(x => x.UserId).Returns(40);
        var repository = new Mock<ICommerceRepository>();
        repository.Setup(x => x.UpdateSellerOrderStatusAsync(4, 40, 15, It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OrderMutationResult(OrderMutationOutcome.Conflict, null));

        var act = () => new UpdateSellerOrderStatusCommandHandler(VendorContext(), user.Object, repository.Object)
            .Handle(new UpdateSellerOrderStatusCommand(15, OrderStatuses.Preparing, OrderStatuses.Accepted), CancellationToken.None);

        await act.Should().ThrowAsync<ConflictException>().WithMessage(CommerceMessages.OrderConflict);
    }

    [Theory]
    [InlineData(OrderStatuses.Preparing, OrderStatuses.Accepted, true)]
    [InlineData(OrderStatuses.ReadyForPickup, OrderStatuses.Preparing, true)]
    [InlineData(OrderStatuses.Completed, OrderStatuses.ReadyForPickup, false)]
    [InlineData(OrderStatuses.Preparing, OrderStatuses.Placed, false)]
    public void A_seller_only_moves_an_order_to_preparing_or_ready(string target, string expected, bool valid) =>
        new UpdateSellerOrderStatusCommandValidator().Validate(new UpdateSellerOrderStatusCommand(15, target, expected))
            .IsValid.Should().Be(valid);

    // ---------------- returning from the payment page (SyncOrderPayment) ----------------

    private static CommerceOrderRow Pending() => Order(OrderStatuses.PendingPayment) with
    {
        PaymentProvider = PaymentProviders.Momo,
        PaymentProviderReference = "momo-ref-1",
        PaymentIdempotencyKey = "key-1",
        PaymentStatus = "PENDING",
    };

    [Theory]
    [InlineData(OrderStatuses.Placed, "momo-ref-1")]
    [InlineData(OrderStatuses.PendingPayment, null)]
    [InlineData(OrderStatuses.PendingPayment, "  ")]
    public async Task Sync_does_not_ask_the_provider_about_a_settled_order_or_one_without_a_reference(string status, string? reference)
    {
        var repository = new Mock<ICommerceRepository>();
        repository.Setup(x => x.GetCustomerOrderAsync(7, 15, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Pending() with { OrderStatus = status, PaymentProviderReference = reference });
        var gateway = new Mock<IPaymentGateway>(MockBehavior.Strict);

        var dto = await new SyncOrderPaymentCommandHandler(CustomerContext(), repository.Object, gateway.Object)
            .Handle(new SyncOrderPaymentCommand(15), default);

        dto.OrderStatus.Should().Be(status);
        repository.Verify(x => x.ApplyPaymentCallbackAsync(It.IsAny<PaymentCallbackData>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Sync_leaves_the_order_alone_while_the_provider_has_no_answer_yet()
    {
        var repository = new Mock<ICommerceRepository>();
        repository.Setup(x => x.GetCustomerOrderAsync(7, 15, It.IsAny<CancellationToken>())).ReturnsAsync(Pending());
        var gateway = new Mock<IPaymentGateway>();
        gateway.Setup(x => x.QueryPaymentAsync(PaymentProviders.Momo, "momo-ref-1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PaymentGatewayCallback("momo-ref-1", null, null, null, true));

        var dto = await new SyncOrderPaymentCommandHandler(CustomerContext(), repository.Object, gateway.Object)
            .Handle(new SyncOrderPaymentCommand(15), default);

        dto.OrderStatus.Should().Be(OrderStatuses.PendingPayment);
        repository.Verify(x => x.ApplyPaymentCallbackAsync(It.IsAny<PaymentCallbackData>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Sync_applies_the_provider_answer_like_a_verified_callback_and_returns_the_fresh_order()
    {
        var repository = new Mock<ICommerceRepository>();
        repository.SetupSequence(x => x.GetCustomerOrderAsync(7, 15, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Pending())
            .ReturnsAsync(Order(OrderStatuses.Placed));
        PaymentCallbackData? applied = null;
        repository.Setup(x => x.ApplyPaymentCallbackAsync(It.IsAny<PaymentCallbackData>(), It.IsAny<CancellationToken>()))
            .Callback<PaymentCallbackData, CancellationToken>((data, _) => applied = data)
            .ReturnsAsync(default(PaymentCallbackMutationResult)!);
        var gateway = new Mock<IPaymentGateway>();
        gateway.Setup(x => x.QueryPaymentAsync(PaymentProviders.Momo, "momo-ref-1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PaymentGatewayCallback(null, null, 50_000, "SUCCESS", true, null, "evt-9"));

        var dto = await new SyncOrderPaymentCommandHandler(CustomerContext(), repository.Object, gateway.Object)
            .Handle(new SyncOrderPaymentCommand(15), default);

        dto.OrderStatus.Should().Be(OrderStatuses.Placed);
        applied.Should().NotBeNull();
        applied!.Provider.Should().Be(PaymentProviders.Momo);
        // The provider did not echo its reference: the order's own is used, and the raw body defaults to "{}".
        applied.ProviderReference.Should().Be("momo-ref-1");
        applied.IdempotencyKey.Should().Be("key-1");
        applied.Amount.Should().Be(50_000);
        applied.Status.Should().Be("SUCCESS");
        applied.RawPayload.Should().Be("{}");
        applied.ProviderEventId.Should().Be("evt-9");
    }

    [Fact]
    public async Task Sync_falls_back_to_the_order_it_read_when_the_reread_finds_nothing()
    {
        var repository = new Mock<ICommerceRepository>();
        repository.SetupSequence(x => x.GetCustomerOrderAsync(7, 15, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Pending())
            .ReturnsAsync((CommerceOrderRow?)null);
        var gateway = new Mock<IPaymentGateway>();
        gateway.Setup(x => x.QueryPaymentAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PaymentGatewayCallback("momo-ref-1", null, 50_000, "FAILED", true));

        var dto = await new SyncOrderPaymentCommandHandler(CustomerContext(), repository.Object, gateway.Object)
            .Handle(new SyncOrderPaymentCommand(15), default);

        dto.OrderId.Should().Be(15);
    }

    [Fact]
    public async Task Sync_of_an_order_that_is_not_the_buyer_own_is_not_found()
    {
        var repository = new Mock<ICommerceRepository>();
        var action = () => new SyncOrderPaymentCommandHandler(CustomerContext(), repository.Object, Mock.Of<IPaymentGateway>())
            .Handle(new SyncOrderPaymentCommand(99), default);

        await action.Should().ThrowAsync<NotFoundException>();
    }

    // ---------------- read-only order and cart queries ----------------

    [Theory]
    [InlineData(null, null)]
    [InlineData("  ", null)]
    [InlineData(" placed ", "PLACED")]
    public async Task Seller_order_list_normalises_the_status_filter(string? status, string? expected)
    {
        var repository = new Mock<ICommerceRepository>();
        repository.Setup(x => x.ListSellerOrdersAsync(4, expected, It.IsAny<CancellationToken>()))
            .ReturnsAsync([Order(), Order(OrderStatuses.Completed)]);

        var list = await new ListSellerOrdersQueryHandler(VendorContext(), repository.Object)
            .Handle(new ListSellerOrdersQuery(status), default);

        list.Should().HaveCount(2);
        repository.VerifyAll();
    }

    [Fact]
    public async Task A_seller_sees_only_their_own_order()
    {
        var repository = new Mock<ICommerceRepository>();
        repository.Setup(x => x.GetSellerOrderAsync(4, 15, It.IsAny<CancellationToken>())).ReturnsAsync(Order());

        (await new GetSellerOrderQueryHandler(VendorContext(), repository.Object).Handle(new GetSellerOrderQuery(15), default))
            .OrderId.Should().Be(15);
        var other = () => new GetSellerOrderQueryHandler(VendorContext(), repository.Object).Handle(new GetSellerOrderQuery(16), default);
        await other.Should().ThrowAsync<NotFoundException>();
    }

    [Fact]
    public async Task Buyer_order_list_and_an_empty_cart()
    {
        var repository = new Mock<ICommerceRepository>();
        repository.Setup(x => x.ListCustomerOrdersAsync(7, It.IsAny<CancellationToken>())).ReturnsAsync([Order()]);

        (await new ListCustomerOrdersQueryHandler(CustomerContext(), repository.Object).Handle(new ListCustomerOrdersQuery(), default))
            .Should().ContainSingle();
        (await new GetCartQueryHandler(CustomerContext(), repository.Object).Handle(new GetCartQuery(), default))
            .Should().BeNull();
    }

    [Theory]
    [InlineData(OrderMutationOutcome.NotFound, typeof(NotFoundException))]
    [InlineData(OrderMutationOutcome.EmptyCart, typeof(DomainRuleException))]
    [InlineData(OrderMutationOutcome.MenuItemUnavailable, typeof(DomainRuleException))]
    [InlineData(OrderMutationOutcome.StorefrontUnavailable, typeof(DomainRuleException))]
    [InlineData(OrderMutationOutcome.PaymentNotFound, typeof(DomainRuleException))]
    [InlineData(OrderMutationOutcome.PendingCheckout, typeof(ConflictException))]
    [InlineData(OrderMutationOutcome.Conflict, typeof(ConflictException))]
    public async Task Each_failed_order_change_becomes_the_right_error(OrderMutationOutcome outcome, Type expected)
    {
        var repository = new Mock<ICommerceRepository>();
        repository.Setup(x => x.ConfirmSandboxPaymentAsync(7, 15, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OrderMutationResult(outcome, null));

        var ex = await Record.ExceptionAsync(() => new ConfirmSandboxOrderPaymentCommandHandler(CustomerContext(), repository.Object)
            .Handle(new ConfirmSandboxOrderPaymentCommand(15), default));

        ex.Should().BeOfType(expected);
    }

    [Fact]
    public async Task A_confirmed_sandbox_payment_returns_the_order()
    {
        var repository = new Mock<ICommerceRepository>();
        repository.Setup(x => x.ConfirmSandboxPaymentAsync(7, 15, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OrderMutationResult(OrderMutationOutcome.Updated, Order()));

        (await new ConfirmSandboxOrderPaymentCommandHandler(CustomerContext(), repository.Object)
            .Handle(new ConfirmSandboxOrderPaymentCommand(15), default)).OrderStatus.Should().Be(OrderStatuses.Placed);
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
