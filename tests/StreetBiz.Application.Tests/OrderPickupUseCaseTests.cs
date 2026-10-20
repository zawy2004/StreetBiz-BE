using FluentAssertions;
using Moq;
using StreetBiz.Application.Common.Exceptions;
using StreetBiz.Application.Common.Interfaces;
using StreetBiz.Application.Common.Models;
using StreetBiz.Application.Common.Security;
using StreetBiz.Application.Features.Commerce;

namespace StreetBiz.Application.Tests;

public sealed class OrderPickupUseCaseTests
{
    private const long CustomerUserId = 7;
    private const long VendorId = 4;
    private const long VendorUserId = 5;
    private const long OrderId = 15;

    /// <summary>
    /// Stands in for the signed implementation, which is Infrastructure's job and
    /// is tested there. These tests care about what the handlers do with a code
    /// once it parses, not how it is signed.
    /// </summary>
    private sealed class FakeTokens : IOrderPickupTokenService
    {
        public string Create(long orderId, long customerUserId) => $"FAKE.{orderId}.{customerUserId}";

        public bool TryParse(string token, out OrderPickupTokenClaims claims)
        {
            claims = null!;
            var parts = (token ?? string.Empty).Split('.');
            if (parts.Length != 3 || parts[0] != "FAKE"
                || !long.TryParse(parts[1], out var orderId)
                || !long.TryParse(parts[2], out var customerUserId))
            {
                return false;
            }

            claims = new OrderPickupTokenClaims(orderId, customerUserId);
            return true;
        }

        // Short codes here are just "<order>-<customer>": the real derivation is
        // Infrastructure's job and is tested there.
        public string CreateShortCode(long orderId, long customerUserId) =>
            $"{orderId}-{customerUserId}";

        public string? NormaliseShortCode(string? typed) =>
            string.IsNullOrWhiteSpace(typed) ? null : typed.Trim().ToUpperInvariant();

        public bool ShortCodeMatches(string normalisedCode, long orderId, long customerUserId) =>
            normalisedCode == CreateShortCode(orderId, customerUserId);
    }

    private static IOrderPickupTokenService Tokens() => new FakeTokens();

    private static ICustomerContext CustomerContext()
    {
        var context = new Mock<ICustomerContext>();
        context.Setup(x => x.RequireCustomerUserIdAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(CustomerUserId);
        return context.Object;
    }

    private static IVendorContext VendorContext()
    {
        var context = new Mock<IVendorContext>();
        context.Setup(x => x.RequireVendorIdAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(VendorId);
        return context.Object;
    }

    private static ICurrentUser SellerUser()
    {
        var user = new Mock<ICurrentUser>();
        user.SetupGet(x => x.UserId).Returns(VendorUserId);
        user.SetupGet(x => x.RoleCode).Returns(RoleCodes.Vendor);
        return user.Object;
    }

    private static CommerceOrderRow Order(
        string status = OrderStatuses.ReadyForPickup,
        long customerUserId = CustomerUserId) => new(
        OrderId,
        "SB-000015",
        customerUserId,
        "Nguyễn Khách Hàng",
        9,
        "Bánh mì & Xôi Cô Lan",
        status,
        50_000,
        50_000,
        null, "MOMO", "SUCCESS",
        null, null, null, null, null,
        new DateTime(2026, 9, 29, 3, 0, 0, DateTimeKind.Utc),
        null,
        new DateTime(2026, 9, 29, 2, 55, 0, DateTimeKind.Utc),
        [],
        [],
        null);

    // ---- showing the code ----

    [Fact]
    public async Task A_paid_order_shows_a_code()
    {
        var repository = new Mock<ICommerceRepository>();
        repository.Setup(x => x.GetCustomerOrderAsync(
                CustomerUserId, OrderId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Order());

        var result = await new GetOrderPickupCodeQueryHandler(
            CustomerContext(), repository.Object, Tokens()).Handle(new(OrderId), default);

        result.OrderCode.Should().Be("SB-000015");
        Tokens().TryParse(result.Token, out var claims).Should().BeTrue();
        claims.OrderId.Should().Be(OrderId);
        claims.CustomerUserId.Should().Be(CustomerUserId);
    }

    [Theory]
    // Each refusal names its own reason: "not paid yet" is wrong for a collected order.
    [InlineData(OrderStatuses.PendingPayment, "thanh toán")]
    [InlineData(OrderStatuses.Completed, "đã giao")]
    [InlineData(OrderStatuses.Cancelled, "huỷ")]
    [InlineData(OrderStatuses.Rejected, "huỷ")]
    public async Task An_order_with_nothing_to_collect_shows_no_code(string status, string reason)
    {
        var repository = new Mock<ICommerceRepository>();
        repository.Setup(x => x.GetCustomerOrderAsync(
                CustomerUserId, OrderId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Order(status));
        var handler = new GetOrderPickupCodeQueryHandler(
            CustomerContext(), repository.Object, Tokens());

        var action = () => handler.Handle(new(OrderId), default);

        (await action.Should().ThrowAsync<DomainRuleException>())
            .Which.Message.Should().Contain(reason);
    }

    [Fact]
    public async Task Another_customers_order_is_not_found()
    {
        var repository = new Mock<ICommerceRepository>();
        repository.Setup(x => x.GetCustomerOrderAsync(
                CustomerUserId, OrderId, It.IsAny<CancellationToken>()))
            .ReturnsAsync((CommerceOrderRow?)null);
        var handler = new GetOrderPickupCodeQueryHandler(
            CustomerContext(), repository.Object, Tokens());

        var action = () => handler.Handle(new(OrderId), default);

        await action.Should().ThrowAsync<NotFoundException>();
    }

    // ---- scanning the code ----

    [Fact]
    public async Task Scanning_hands_the_order_over_through_the_normal_completion_path()
    {
        var tokens = Tokens();
        var repository = new Mock<ICommerceRepository>();
        repository.Setup(x => x.GetSellerOrderAsync(VendorId, OrderId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Order());
        repository.Setup(x => x.ConfirmSellerHandoverAsync(
                VendorId, VendorUserId, OrderId, OrderStatuses.ReadyForPickup,
                OrderPickupMessages.ScannedNote, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OrderMutationResult(
                OrderMutationOutcome.Updated, Order(OrderStatuses.Completed)));

        var result = await new ScanOrderPickupCommandHandler(
                VendorContext(), SellerUser(), repository.Object, tokens)
            .Handle(new(tokens.Create(OrderId, CustomerUserId)), default);

        result.OrderStatus.Should().Be(OrderStatuses.Completed);
        // The scan must not invent a second way to finish an order.
        repository.VerifyAll();
    }

    [Fact]
    public async Task A_code_for_another_storefronts_order_is_refused_without_writing()
    {
        var tokens = Tokens();
        var repository = new Mock<ICommerceRepository>(MockBehavior.Strict);
        repository.Setup(x => x.GetSellerOrderAsync(VendorId, OrderId, It.IsAny<CancellationToken>()))
            .ReturnsAsync((CommerceOrderRow?)null);
        var handler = new ScanOrderPickupCommandHandler(
            VendorContext(), SellerUser(), repository.Object, tokens);

        var action = () => handler.Handle(new(tokens.Create(OrderId, CustomerUserId)), default);

        await action.Should().ThrowAsync<DomainRuleException>();
    }

    [Fact]
    public async Task A_code_naming_a_different_customer_than_the_order_is_refused()
    {
        var tokens = Tokens();
        var repository = new Mock<ICommerceRepository>(MockBehavior.Strict);
        // The order really belongs to customer 99; the code claims customer 7.
        repository.Setup(x => x.GetSellerOrderAsync(VendorId, OrderId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Order(customerUserId: 99));
        var handler = new ScanOrderPickupCommandHandler(
            VendorContext(), SellerUser(), repository.Object, tokens);

        var action = () => handler.Handle(new(tokens.Create(OrderId, CustomerUserId)), default);

        await action.Should().ThrowAsync<DomainRuleException>();
    }

    [Fact]
    public async Task An_unrecognised_code_never_reaches_the_database()
    {
        var repository = new Mock<ICommerceRepository>(MockBehavior.Strict);
        var handler = new ScanOrderPickupCommandHandler(
            VendorContext(), SellerUser(), repository.Object, Tokens());

        var action = () => handler.Handle(new("SBO1.garbage.garbage"), default);

        await action.Should().ThrowAsync<DomainRuleException>();
        repository.VerifyNoOtherCalls();
    }

    // ---- typing the code when the camera is broken ----

    [Fact]
    public async Task Typing_the_code_finds_the_waiting_order_and_hands_it_over()
    {
        var tokens = Tokens();
        var repository = new Mock<ICommerceRepository>();
        repository.Setup(x => x.ListSellerOrdersAsync(
                VendorId, OrderStatuses.ReadyForPickup, It.IsAny<CancellationToken>()))
            .ReturnsAsync([Order(customerUserId: 99), Order()]);
        repository.Setup(x => x.ConfirmSellerHandoverAsync(
                VendorId, VendorUserId, OrderId, OrderStatuses.ReadyForPickup,
                OrderPickupMessages.TypedCodeNote, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OrderMutationResult(
                OrderMutationOutcome.Updated, Order(OrderStatuses.Completed)));

        var result = await new ConfirmPickupByCodeCommandHandler(
                VendorContext(), SellerUser(), repository.Object, tokens)
            .Handle(new(tokens.CreateShortCode(OrderId, CustomerUserId)), default);

        result.OrderStatus.Should().Be(OrderStatuses.Completed);
        repository.VerifyAll();
    }

    [Fact]
    public async Task A_code_matching_nothing_waiting_is_refused_without_writing()
    {
        var repository = new Mock<ICommerceRepository>(MockBehavior.Strict);
        repository.Setup(x => x.ListSellerOrdersAsync(
                VendorId, OrderStatuses.ReadyForPickup, It.IsAny<CancellationToken>()))
            .ReturnsAsync([Order()]);
        repository.Setup(x => x.ListSellerOrdersAsync(
                VendorId, It.IsNotIn(OrderStatuses.ReadyForPickup), It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
        var handler = new ConfirmPickupByCodeCommandHandler(
            VendorContext(), SellerUser(), repository.Object, Tokens());

        var action = () => handler.Handle(new("999-999"), default);

        (await action.Should().ThrowAsync<DomainRuleException>())
            .Which.Message.Should().Be(OrderPickupMessages.ShortCodeNoMatch);
    }

    // The common miss: the buyer reads the code out before the seller pressed
    // "ready". Scanning names the missing step in that case; typing must too,
    // rather than suggest the code was misheard.
    [Theory]
    [InlineData(OrderStatuses.Placed, "Nhận đơn")]
    [InlineData(OrderStatuses.Accepted, "Bắt đầu chuẩn bị")]
    [InlineData(OrderStatuses.Preparing, "Sẵn sàng lấy món")]
    public async Task Typing_the_code_of_an_unready_order_names_the_missing_step(
        string status, string expectedHint)
    {
        var tokens = Tokens();
        var repository = new Mock<ICommerceRepository>(MockBehavior.Strict);
        repository.Setup(x => x.ListSellerOrdersAsync(
                VendorId, It.IsNotIn(status), It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
        repository.Setup(x => x.ListSellerOrdersAsync(
                VendorId, status, It.IsAny<CancellationToken>()))
            .ReturnsAsync([Order(status)]);
        var handler = new ConfirmPickupByCodeCommandHandler(
            VendorContext(), SellerUser(), repository.Object, tokens);

        var action = () => handler.Handle(new(tokens.CreateShortCode(OrderId, CustomerUserId)), default);

        // Strict mock: completing the order would call a member never set up.
        (await action.Should().ThrowAsync<DomainRuleException>())
            .Which.Message.Should().Contain(expectedHint);
    }

    // Whatever the camera read, the seller is told it is not a StreetBiz code -
    // never a validator sentence naming a field.
    [Theory]
    [InlineData("")]
    [InlineData("00020101021238570010A000000727012700069704220113VQRQABCDEFGH0208QRIBFTTA53037045802VN62150811DH-2026-000163049A3F00020101021238570010A000000727012700069704220113VQRQABCDEFGH0208QRIBFTTA53037045802VN6215081100020101021238570010A000000727")]
    public void Any_other_qr_is_refused_as_not_a_streetbiz_code(string scanned)
    {
        var result = new ScanOrderPickupCommandValidator().Validate(new ScanOrderPickupCommand(scanned));

        result.Errors.Should().ContainSingle()
            .Which.ErrorMessage.Should().Be(OrderPickupMessages.CodeNotRecognised);
    }

    [Theory]
    [InlineData("")]
    [InlineData("7K2M9QXP7K2M9QXP7K2M9QXP7K2M9QXP7K2M9QXP7K2M9QXP7K2M9QXP")]
    public void A_typed_code_of_the_wrong_shape_says_what_a_code_looks_like(string typed)
    {
        var result = new ConfirmPickupByCodeCommandValidator()
            .Validate(new ConfirmPickupByCodeCommand(typed));

        result.Errors.Should().ContainSingle()
            .Which.ErrorMessage.Should().Be(OrderPickupMessages.ShortCodeMalformed);
    }

    [Fact]
    public async Task An_unusable_code_never_reaches_the_database()
    {
        var repository = new Mock<ICommerceRepository>(MockBehavior.Strict);
        var handler = new ConfirmPickupByCodeCommandHandler(
            VendorContext(), SellerUser(), repository.Object, Tokens());

        var action = () => handler.Handle(new("   "), default);

        await action.Should().ThrowAsync<DomainRuleException>();
        repository.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task The_buyers_code_carries_both_ways_to_prove_it()
    {
        var repository = new Mock<ICommerceRepository>();
        repository.Setup(x => x.GetCustomerOrderAsync(
                CustomerUserId, OrderId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Order());

        var result = await new GetOrderPickupCodeQueryHandler(
            CustomerContext(), repository.Object, Tokens()).Handle(new(OrderId), default);

        result.Token.Should().NotBeNullOrWhiteSpace();
        result.ShortCode.Should().NotBeNullOrWhiteSpace();
    }

    // ---- telling the seller what is actually wrong ----

    [Theory]
    [InlineData(OrderStatuses.Placed, "Nhận đơn")]
    [InlineData(OrderStatuses.Accepted, "Bắt đầu chuẩn bị")]
    [InlineData(OrderStatuses.Preparing, "Sẵn sàng lấy món")]
    public async Task Scanning_an_order_that_is_not_ready_names_the_missing_step(
        string status, string expectedHint)
    {
        var tokens = Tokens();
        var repository = new Mock<ICommerceRepository>(MockBehavior.Strict);
        repository.Setup(x => x.GetSellerOrderAsync(VendorId, OrderId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Order(status));
        var handler = new ScanOrderPickupCommandHandler(
            VendorContext(), SellerUser(), repository.Object, tokens);

        var action = () => handler.Handle(new(tokens.Create(OrderId, CustomerUserId)), default);

        // "The order status just changed" would be both untrue and useless here.
        // The strict mock is the guard against a write: completing the order
        // would call a member that was never set up, and fail.
        (await action.Should().ThrowAsync<DomainRuleException>())
            .Which.Message.Should().Contain(expectedHint);
    }

    [Fact]
    public async Task Scanning_an_order_already_collected_says_so()
    {
        var tokens = Tokens();
        var repository = new Mock<ICommerceRepository>(MockBehavior.Strict);
        repository.Setup(x => x.GetSellerOrderAsync(VendorId, OrderId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Order(OrderStatuses.Completed));
        var handler = new ScanOrderPickupCommandHandler(
            VendorContext(), SellerUser(), repository.Object, tokens);

        var action = () => handler.Handle(new(tokens.Create(OrderId, CustomerUserId)), default);

        (await action.Should().ThrowAsync<DomainRuleException>())
            .Which.Message.Should().Contain("đã giao");
    }

    [Theory]
    [InlineData(OrderStatuses.Cancelled)]
    [InlineData(OrderStatuses.Rejected)]
    public async Task Scanning_a_closed_order_says_it_cannot_be_handed_over(string status)
    {
        var tokens = Tokens();
        var repository = new Mock<ICommerceRepository>(MockBehavior.Strict);
        repository.Setup(x => x.GetSellerOrderAsync(VendorId, OrderId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Order(status));
        var handler = new ScanOrderPickupCommandHandler(
            VendorContext(), SellerUser(), repository.Object, tokens);

        var action = () => handler.Handle(new(tokens.Create(OrderId, CustomerUserId)), default);

        (await action.Should().ThrowAsync<DomainRuleException>())
            .Which.Message.Should().Contain("huỷ");
    }

    [Fact]
    public void Every_message_the_seller_sees_is_in_vietnamese()
    {
        // The app speaks Vietnamese; an English string here reaches a real stall.
        var messages = new[]
        {
            OrderPickupMessages.NotPaidYet,
            OrderPickupMessages.CodeNotRecognised,
            OrderPickupMessages.NotYourOrder,
            OrderPickupMessages.WrongCustomer,
            OrderPickupMessages.ShortCodeMalformed,
            OrderPickupMessages.ShortCodeNoMatch,
            OrderPickupMessages.AlreadyHandedOver,
            OrderPickupMessages.OrderClosed,
            OrderPickupMessages.NotReadyYet(OrderStatuses.Accepted),
            OrderPickupMessages.ReasonRequired,
            OrderPickupMessages.ReasonTooShort,
            OrderPickupMessages.ReasonTooLong,
            OrderPickupMessages.ManualNote("hết pin"),
        };

        messages.Should().OnlyContain(m => m.Any(c => c > 127), "each message carries Vietnamese diacritics");
    }

    // ---- handing over with no code, on a written reason ----

    private static ConfirmHandoverWithoutCodeCommandHandler ManualHandler(
        ICommerceRepository repository) =>
        new(VendorContext(), SellerUser(), repository);

    private static bool IsValid(string reason) =>
        new ConfirmHandoverWithoutCodeCommandValidator()
            .Validate(new ConfirmHandoverWithoutCodeCommand(OrderId, reason)).IsValid;

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("x")]
    [InlineData("  a  ")]
    public void A_handover_with_no_real_reason_is_refused(string reason) =>
        IsValid(reason).Should().BeFalse();

    [Theory]
    [InlineData("hết pin")]
    [InlineData("Khách mất điện thoại, đã đối chiếu tên và số bàn.")]
    public void A_written_reason_is_accepted(string reason) =>
        IsValid(reason).Should().BeTrue();

    [Fact]
    public void A_reason_longer_than_the_history_column_is_refused() =>
        new ConfirmHandoverWithoutCodeCommandValidator()
            .Validate(new ConfirmHandoverWithoutCodeCommand(OrderId, new string('a', 501)))
            .Errors.Should().ContainSingle()
            .Which.ErrorMessage.Should().Be(OrderPickupMessages.ReasonTooLong);

    [Fact]
    public async Task The_reason_is_written_into_the_orders_history()
    {
        var repository = new Mock<ICommerceRepository>(MockBehavior.Strict);
        repository.Setup(x => x.GetSellerOrderAsync(VendorId, OrderId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Order());
        string? recordedNote = null;
        repository.Setup(x => x.ConfirmSellerHandoverAsync(
                VendorId, VendorUserId, OrderId, OrderStatuses.ReadyForPickup,
                It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback((long _, long _, long _, string _, string note, CancellationToken _) =>
                recordedNote = note)
            .ReturnsAsync(new OrderMutationResult(
                OrderMutationOutcome.Updated, Order(OrderStatuses.Completed)));

        var result = await ManualHandler(repository.Object)
            .Handle(new(OrderId, "  Khách hết pin điện thoại  "), default);

        result.OrderStatus.Should().Be(OrderStatuses.Completed);
        // Without the reason in the history this route is indistinguishable
        // from the seller simply closing orders at will.
        recordedNote.Should().Contain("Khách hết pin điện thoại");
        recordedNote.Should().NotContain("  Khách");
    }

    [Fact]
    public async Task A_blank_reason_never_reaches_the_database()
    {
        var repository = new Mock<ICommerceRepository>(MockBehavior.Strict);

        var action = () => ManualHandler(repository.Object).Handle(new(OrderId, "   "), default);

        await action.Should().ThrowAsync<DomainRuleException>();
        repository.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Another_storefronts_order_cannot_be_handed_over_by_reason()
    {
        var repository = new Mock<ICommerceRepository>(MockBehavior.Strict);
        repository.Setup(x => x.GetSellerOrderAsync(VendorId, OrderId, It.IsAny<CancellationToken>()))
            .ReturnsAsync((CommerceOrderRow?)null);

        var action = () => ManualHandler(repository.Object)
            .Handle(new(OrderId, "Khách quên điện thoại"), default);

        await action.Should().ThrowAsync<DomainRuleException>();
    }

    [Theory]
    [InlineData(OrderStatuses.Placed, "Nhận đơn")]
    [InlineData(OrderStatuses.Accepted, "Bắt đầu chuẩn bị")]
    [InlineData(OrderStatuses.Preparing, "Sẵn sàng lấy món")]
    [InlineData(OrderStatuses.Completed, "đã giao")]
    [InlineData(OrderStatuses.Cancelled, "huỷ")]
    public async Task A_reason_does_not_let_an_unready_order_be_handed_over(
        string status, string expectedHint)
    {
        // A written reason buys the seller past the code, not past the workflow.
        var repository = new Mock<ICommerceRepository>(MockBehavior.Strict);
        repository.Setup(x => x.GetSellerOrderAsync(VendorId, OrderId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Order(status));

        var action = () => ManualHandler(repository.Object)
            .Handle(new(OrderId, "Khách quên điện thoại"), default);

        (await action.Should().ThrowAsync<DomainRuleException>())
            .Which.Message.Should().Contain(expectedHint);
    }
}
