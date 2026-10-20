using FluentValidation;
using MediatR;
using StreetBiz.Application.Common.Exceptions;
using StreetBiz.Application.Common.Interfaces;
using StreetBiz.Application.Common.Security;
using StreetBiz.Application.DTOs.Commerce;

namespace StreetBiz.Application.Features.Commerce;

public static class OrderPickupMessages
{
    public const string NotPaidYet = "Mã nhận hàng chỉ hiện sau khi đơn được thanh toán.";
    public const string CodeNotRecognised = "Mã này không phải mã nhận hàng của StreetBiz.";
    public const string NotYourOrder = "Đơn này không thuộc gian hàng của bạn.";
    public const string WrongCustomer = "Mã không khớp với khách hàng của đơn này.";
    public const string ShortCodeMalformed = "Mã nhận hàng gồm 8 ký tự. Kiểm tra lại mã vừa nhập.";
    // Reached only once no waiting or in-progress order matches, so it is
    // either a misheard code or an order that is already finished.
    public const string ShortCodeNoMatch =
        "Không có đơn nào đang chờ giao khớp mã này. Kiểm tra lại mã khách đọc; đơn đã giao hoặc đã huỷ thì mã không dùng được nữa.";
    public const string AlreadyHandedOver = "Đơn này đã giao cho khách rồi.";
    public const string OrderClosed = "Đơn này đã bị huỷ hoặc từ chối, không thể giao.";
    public const string ReasonRequired = "Phải ghi lý do khi giao đơn mà không có mã.";
    public const string ReasonTooShort =
        "Lý do quá ngắn. Hãy ghi rõ vì sao không đọc được mã của khách.";
    public const string ReasonTooLong = "Lý do dài tối đa 500 ký tự.";

    // These land in the order's status history, which the buyer reads too, so
    // they say how the handover was proved rather than merely that it happened.
    public const string ScannedNote = "Người bán đã quét mã nhận hàng của khách.";
    public const string TypedCodeNote = "Người bán nhập mã nhận hàng khách đọc.";

    /// <summary>The exception route: no code was read, so the reason stands in its place.</summary>
    public static string ManualNote(string reason) =>
        $"Giao thủ công, không có mã nhận hàng. Lý do người bán ghi: {reason}";

    /// <summary>Names the step the seller still has to take, instead of a bare conflict.</summary>
    public static string NotReadyYet(string status) => status switch
    {
        OrderStatuses.Placed => "Đơn chưa được nhận. Hãy bấm \"Nhận đơn\" trước khi giao.",
        // Button names as the seller sees them on the order screen.
        OrderStatuses.Accepted => "Đơn chưa chuẩn bị xong. Hãy bấm \"Bắt đầu chuẩn bị\" rồi \"Sẵn sàng lấy món\".",
        OrderStatuses.Preparing => "Đơn đang chuẩn bị. Hãy bấm \"Sẵn sàng lấy món\" trước khi giao cho khách.",
        _ => "Đơn chưa sẵn sàng để giao.",
    };
}

/// <summary>ORD-06: the code the customer shows at the stall.</summary>
public sealed record GetOrderPickupCodeQuery(long OrderId) : IRequest<OrderPickupCodeDto>;

public sealed class GetOrderPickupCodeQueryValidator : AbstractValidator<GetOrderPickupCodeQuery>
{
    public GetOrderPickupCodeQueryValidator() => RuleFor(x => x.OrderId).GreaterThan(0);
}

public sealed class GetOrderPickupCodeQueryHandler(
    ICustomerContext customerContext,
    ICommerceRepository repository,
    IOrderPickupTokenService tokens)
    : IRequestHandler<GetOrderPickupCodeQuery, OrderPickupCodeDto>
{
    public async Task<OrderPickupCodeDto> Handle(
        GetOrderPickupCodeQuery request,
        CancellationToken cancellationToken)
    {
        var customerUserId = await customerContext.RequireCustomerUserIdAsync(cancellationToken);
        var order = await repository.GetCustomerOrderAsync(
            customerUserId, request.OrderId, cancellationToken)
            ?? throw new NotFoundException(CommerceMessages.OrderNotFound);

        // An unpaid order has nothing to collect and a finished one nothing left
        // to prove, but those are different situations and the buyer is told which.
        if (!OrderStatuses.IsCollectable(order.OrderStatus))
        {
            throw new DomainRuleException(order.OrderStatus switch
            {
                OrderStatuses.Completed => OrderPickupMessages.AlreadyHandedOver,
                OrderStatuses.Cancelled or OrderStatuses.Rejected => OrderPickupMessages.OrderClosed,
                _ => OrderPickupMessages.NotPaidYet,
            });
        }

        return new OrderPickupCodeDto(
            order.OrderId,
            order.OrderCode,
            order.OrderStatus,
            order.StorefrontName,
            tokens.Create(order.OrderId, customerUserId),
            tokens.CreateShortCode(order.OrderId, customerUserId));
    }
}

/// <summary>
/// ORD-06: the seller scans the customer's code to hand the order over.
///
/// The scan does not get its own completion path: once the code is shown to
/// belong to this seller's order and to the customer on it, the order is
/// completed through the same transaction as the typed-code and no-code
/// routes, so all three share one set of state checks and one audit trail.
/// </summary>
public sealed record ScanOrderPickupCommand(string Token) : IRequest<OrderDto>;

public sealed class ScanOrderPickupCommandValidator : AbstractValidator<ScanOrderPickupCommand>
{
    // Whatever the camera read - an empty frame, a bank transfer QR, a URL - the
    // seller is told it is not a StreetBiz code, never which field failed.
    public ScanOrderPickupCommandValidator() =>
        RuleFor(x => x.Token)
            .Cascade(CascadeMode.Stop)
            .NotEmpty().WithMessage(OrderPickupMessages.CodeNotRecognised)
            .MaximumLength(200).WithMessage(OrderPickupMessages.CodeNotRecognised);
}

public sealed class ScanOrderPickupCommandHandler(
    IVendorContext vendorContext,
    ICurrentUser currentUser,
    ICommerceRepository repository,
    IOrderPickupTokenService tokens) : IRequestHandler<ScanOrderPickupCommand, OrderDto>
{
    public async Task<OrderDto> Handle(
        ScanOrderPickupCommand request,
        CancellationToken cancellationToken)
    {
        if (!tokens.TryParse(request.Token, out var claims))
        {
            throw new DomainRuleException(OrderPickupMessages.CodeNotRecognised);
        }

        var vendorId = await vendorContext.RequireVendorIdAsync(cancellationToken);
        var actorUserId = currentUser.UserId
            ?? throw new AuthenticationException(AppMessages.SessionExpired);

        // The signature only proves StreetBiz issued the code. Everything that
        // matters - whose order it is, who is collecting - is re-read from the
        // database rather than believed from the payload.
        var order = await repository.GetSellerOrderAsync(
            vendorId, claims.OrderId, cancellationToken)
            ?? throw new DomainRuleException(OrderPickupMessages.NotYourOrder);

        if (order.CustomerUserId != claims.CustomerUserId)
        {
            throw new DomainRuleException(OrderPickupMessages.WrongCustomer);
        }

        // Checked here so the seller is told which step is still missing. Letting
        // the handover refuse it instead surfaces "the order status just changed",
        // which is both untrue and no help to somebody holding a queue.
        if (order.OrderStatus != OrderStatuses.ReadyForPickup)
        {
            throw new DomainRuleException(order.OrderStatus switch
            {
                OrderStatuses.Completed => OrderPickupMessages.AlreadyHandedOver,
                OrderStatuses.Cancelled or OrderStatuses.Rejected => OrderPickupMessages.OrderClosed,
                _ => OrderPickupMessages.NotReadyYet(order.OrderStatus),
            });
        }

        return (await repository.ConfirmSellerHandoverAsync(
            vendorId,
            actorUserId,
            order.OrderId,
            OrderStatuses.ReadyForPickup,
            OrderPickupMessages.ScannedNote,
            cancellationToken)).RequireOrder();
    }
}

/// <summary>
/// ORD-06 fallback: the seller types the customer's 8-character code when the
/// camera is broken or the browser cannot decode a QR.
///
/// The code carries no order id, so the order is found by computing each
/// waiting order's own code and comparing. That set is the seller's orders in
/// READY_FOR_PICKUP - a handful in practice - and it means the fallback needs no
/// stored code and no extra column to go stale.
/// </summary>
public sealed record ConfirmPickupByCodeCommand(string Code) : IRequest<OrderDto>;

public sealed class ConfirmPickupByCodeCommandValidator
    : AbstractValidator<ConfirmPickupByCodeCommand>
{
    public ConfirmPickupByCodeCommandValidator() =>
        RuleFor(x => x.Code)
            .Cascade(CascadeMode.Stop)
            .NotEmpty().WithMessage(OrderPickupMessages.ShortCodeMalformed)
            .MaximumLength(50).WithMessage(OrderPickupMessages.ShortCodeMalformed);
}

public sealed class ConfirmPickupByCodeCommandHandler(
    IVendorContext vendorContext,
    ICurrentUser currentUser,
    ICommerceRepository repository,
    IOrderPickupTokenService tokens) : IRequestHandler<ConfirmPickupByCodeCommand, OrderDto>
{
    public async Task<OrderDto> Handle(
        ConfirmPickupByCodeCommand request,
        CancellationToken cancellationToken)
    {
        var normalised = tokens.NormaliseShortCode(request.Code)
            ?? throw new DomainRuleException(OrderPickupMessages.ShortCodeMalformed);

        var vendorId = await vendorContext.RequireVendorIdAsync(cancellationToken);
        var actorUserId = currentUser.UserId
            ?? throw new AuthenticationException(AppMessages.SessionExpired);

        var waiting = await repository.ListSellerOrdersAsync(
            vendorId, OrderStatuses.ReadyForPickup, cancellationToken);
        var match = waiting.FirstOrDefault(order =>
            tokens.ShortCodeMatches(normalised, order.OrderId, order.CustomerUserId))
            ?? throw new DomainRuleException(
                await ExplainMissAsync(vendorId, normalised, cancellationToken));

        return (await repository.ConfirmSellerHandoverAsync(
            vendorId,
            actorUserId,
            match.OrderId,
            OrderStatuses.ReadyForPickup,
            OrderPickupMessages.TypedCodeNote,
            cancellationToken)).RequireOrder();
    }

    /// <summary>
    /// The usual miss is a code read out before the seller pressed "ready", and
    /// the scan route names the missing step in that case, so this one does too.
    /// Only the in-progress statuses are searched: they are a handful of orders,
    /// where finished ones grow without bound.
    /// </summary>
    private async Task<string> ExplainMissAsync(
        long vendorId, string normalised, CancellationToken cancellationToken)
    {
        foreach (var status in new[]
            { OrderStatuses.Preparing, OrderStatuses.Accepted, OrderStatuses.Placed })
        {
            var orders = await repository.ListSellerOrdersAsync(vendorId, status, cancellationToken);
            if (orders.Any(order =>
                tokens.ShortCodeMatches(normalised, order.OrderId, order.CustomerUserId)))
            {
                return OrderPickupMessages.NotReadyYet(status);
            }
        }

        return OrderPickupMessages.ShortCodeNoMatch;
    }
}

/// <summary>
/// ORD-06 last resort: the buyer has no code at all - a flat phone, a lost
/// order, an app that will not open - and the seller hands the food over on
/// their own judgement.
///
/// This path exists because the alternative was worse: with only the code, such
/// an order was stuck in READY_FOR_PICKUP for good and the stall had a customer
/// in front of it and nothing to press. What it does not do is make the code
/// optional. The seller must write down why, and that sentence is stored in the
/// order's status history where the buyer, and anyone auditing the stall, reads
/// it next to the completion. A handover with no code is allowed; a handover
/// with no explanation is not.
/// </summary>
public sealed record ConfirmHandoverWithoutCodeCommand(long OrderId, string Reason)
    : IRequest<OrderDto>;

public sealed class ConfirmHandoverWithoutCodeCommandValidator
    : AbstractValidator<ConfirmHandoverWithoutCodeCommand>
{
    /// <summary>
    /// Long enough to rule out "a" or "..", short enough to let "hết pin" through.
    /// It cannot tell a real reason from a typed-out one - what makes this path
    /// costly is that whatever is written is shown to the buyer, not its length.
    /// </summary>
    public const int MinimumReasonLength = 6;

    public ConfirmHandoverWithoutCodeCommandValidator()
    {
        RuleFor(x => x.OrderId).GreaterThan(0);
        RuleFor(x => x.Reason)
            .Cascade(CascadeMode.Stop)
            .NotEmpty().WithMessage(OrderPickupMessages.ReasonRequired)
            .Must(reason => reason.Trim().Length >= MinimumReasonLength)
                .WithMessage(OrderPickupMessages.ReasonTooShort)
            .MaximumLength(500).WithMessage(OrderPickupMessages.ReasonTooLong);
    }
}

public sealed class ConfirmHandoverWithoutCodeCommandHandler(
    IVendorContext vendorContext,
    ICurrentUser currentUser,
    ICommerceRepository repository)
    : IRequestHandler<ConfirmHandoverWithoutCodeCommand, OrderDto>
{
    public async Task<OrderDto> Handle(
        ConfirmHandoverWithoutCodeCommand request,
        CancellationToken cancellationToken)
    {
        var reason = CommerceText.Normalize(request.Reason)
            ?? throw new DomainRuleException(OrderPickupMessages.ReasonRequired);

        var vendorId = await vendorContext.RequireVendorIdAsync(cancellationToken);
        var actorUserId = currentUser.UserId
            ?? throw new AuthenticationException(AppMessages.SessionExpired);

        // Skipping the code does not skip the rest: the order still has to be
        // this seller's, and still has to be ready to hand over.
        var order = await repository.GetSellerOrderAsync(
            vendorId, request.OrderId, cancellationToken)
            ?? throw new DomainRuleException(OrderPickupMessages.NotYourOrder);

        if (order.OrderStatus != OrderStatuses.ReadyForPickup)
        {
            throw new DomainRuleException(order.OrderStatus switch
            {
                OrderStatuses.Completed => OrderPickupMessages.AlreadyHandedOver,
                OrderStatuses.Cancelled or OrderStatuses.Rejected => OrderPickupMessages.OrderClosed,
                _ => OrderPickupMessages.NotReadyYet(order.OrderStatus),
            });
        }

        return (await repository.ConfirmSellerHandoverAsync(
            vendorId,
            actorUserId,
            order.OrderId,
            OrderStatuses.ReadyForPickup,
            OrderPickupMessages.ManualNote(reason),
            cancellationToken)).RequireOrder();
    }
}
