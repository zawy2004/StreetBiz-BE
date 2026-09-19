using FluentValidation;
using MediatR;
using StreetBiz.Application.Common.Exceptions;
using StreetBiz.Application.Common.Interfaces;
using StreetBiz.Application.Common.Models;
using StreetBiz.Application.Common.Security;
using StreetBiz.Application.DTOs.Commerce;

namespace StreetBiz.Application.Features.Commerce;

public sealed record SearchMarketplaceMenuQuery(string? Query, int Take = 50)
    : IRequest<IReadOnlyList<MarketplaceMenuItemDto>>;

public sealed class SearchMarketplaceMenuQueryValidator : AbstractValidator<SearchMarketplaceMenuQuery>
{
    public SearchMarketplaceMenuQueryValidator()
    {
        RuleFor(x => x.Query).MaximumLength(100);
        RuleFor(x => x.Take).InclusiveBetween(1, 100);
    }
}

public sealed class SearchMarketplaceMenuQueryHandler(ICommerceRepository repository)
    : IRequestHandler<SearchMarketplaceMenuQuery, IReadOnlyList<MarketplaceMenuItemDto>>
{
    public async Task<IReadOnlyList<MarketplaceMenuItemDto>> Handle(
        SearchMarketplaceMenuQuery request,
        CancellationToken cancellationToken) =>
        (await repository.SearchMenuItemsAsync(
            string.IsNullOrWhiteSpace(request.Query) ? null : request.Query.Trim(),
            request.Take,
            cancellationToken))
        .Select(row => row.ToDto())
        .ToArray();
}

public sealed record GetMarketplaceMenuItemQuery(long MenuItemId) : IRequest<MarketplaceMenuItemDto>;

public sealed class GetMarketplaceMenuItemQueryValidator : AbstractValidator<GetMarketplaceMenuItemQuery>
{
    public GetMarketplaceMenuItemQueryValidator() => RuleFor(x => x.MenuItemId).GreaterThan(0);
}

public sealed class GetMarketplaceMenuItemQueryHandler(ICommerceRepository repository)
    : IRequestHandler<GetMarketplaceMenuItemQuery, MarketplaceMenuItemDto>
{
    public async Task<MarketplaceMenuItemDto> Handle(
        GetMarketplaceMenuItemQuery request,
        CancellationToken cancellationToken) =>
        (await repository.GetMenuItemAsync(request.MenuItemId, cancellationToken))?.ToDto()
        ?? throw new NotFoundException(CommerceMessages.MenuItemNotFound);
}

public sealed record GetCartQuery : IRequest<CartDto?>;

public sealed class GetCartQueryHandler(
    ICustomerContext customerContext,
    ICommerceRepository repository) : IRequestHandler<GetCartQuery, CartDto?>
{
    public async Task<CartDto?> Handle(GetCartQuery request, CancellationToken cancellationToken)
    {
        var customerId = await customerContext.RequireCustomerUserIdAsync(cancellationToken);
        return (await repository.GetActiveCartAsync(customerId, cancellationToken))?.ToDto();
    }
}

public sealed record AddCartItemCommand(long MenuItemId, int Quantity, string? Note)
    : IRequest<CartDto>;

public sealed class AddCartItemCommandValidator : AbstractValidator<AddCartItemCommand>
{
    public AddCartItemCommandValidator()
    {
        RuleFor(x => x.MenuItemId).GreaterThan(0);
        RuleFor(x => x.Quantity).InclusiveBetween(1, 99);
        RuleFor(x => x.Note).MaximumLength(300);
    }
}

public sealed class AddCartItemCommandHandler(
    ICustomerContext customerContext,
    ICommerceRepository repository) : IRequestHandler<AddCartItemCommand, CartDto>
{
    public async Task<CartDto> Handle(AddCartItemCommand request, CancellationToken cancellationToken)
    {
        var customerId = await customerContext.RequireCustomerUserIdAsync(cancellationToken);
        var result = await repository.AddCartItemAsync(
            customerId,
            request.MenuItemId,
            request.Quantity,
            CommerceText.Normalize(request.Note),
            cancellationToken);
        return result.RequireCart(CommerceMessages.MenuItemNotFound);
    }
}

public sealed record UpdateCartItemCommand(long MenuItemId, int Quantity, string? Note)
    : IRequest<CartDto>;

public sealed class UpdateCartItemCommandValidator : AbstractValidator<UpdateCartItemCommand>
{
    public UpdateCartItemCommandValidator()
    {
        RuleFor(x => x.MenuItemId).GreaterThan(0);
        RuleFor(x => x.Quantity).InclusiveBetween(1, 99);
        RuleFor(x => x.Note).MaximumLength(300);
    }
}

public sealed class UpdateCartItemCommandHandler(
    ICustomerContext customerContext,
    ICommerceRepository repository) : IRequestHandler<UpdateCartItemCommand, CartDto>
{
    public async Task<CartDto> Handle(UpdateCartItemCommand request, CancellationToken cancellationToken)
    {
        var customerId = await customerContext.RequireCustomerUserIdAsync(cancellationToken);
        var result = await repository.UpdateCartItemAsync(
            customerId,
            request.MenuItemId,
            request.Quantity,
            CommerceText.Normalize(request.Note),
            cancellationToken);
        return result.RequireCart(CommerceMessages.CartItemNotFound);
    }
}

public sealed record RemoveCartItemCommand(long MenuItemId) : IRequest<CartDto?>;

public sealed class RemoveCartItemCommandValidator : AbstractValidator<RemoveCartItemCommand>
{
    public RemoveCartItemCommandValidator() => RuleFor(x => x.MenuItemId).GreaterThan(0);
}

public sealed class RemoveCartItemCommandHandler(
    ICustomerContext customerContext,
    ICommerceRepository repository) : IRequestHandler<RemoveCartItemCommand, CartDto?>
{
    public async Task<CartDto?> Handle(RemoveCartItemCommand request, CancellationToken cancellationToken)
    {
        var customerId = await customerContext.RequireCustomerUserIdAsync(cancellationToken);
        var result = await repository.RemoveCartItemAsync(
            customerId, request.MenuItemId, cancellationToken);
        return result.Outcome switch
        {
            CartMutationOutcome.Updated => result.Cart?.ToDto(),
            CartMutationOutcome.NotFound => throw new NotFoundException(CommerceMessages.CartItemNotFound),
            _ => throw new ConflictException(CommerceMessages.CartConflict),
        };
    }
}

public sealed record ClearCartCommand : IRequest<Unit>;

public sealed class ClearCartCommandHandler(
    ICustomerContext customerContext,
    ICommerceRepository repository) : IRequestHandler<ClearCartCommand, Unit>
{
    public async Task<Unit> Handle(ClearCartCommand request, CancellationToken cancellationToken)
    {
        var customerId = await customerContext.RequireCustomerUserIdAsync(cancellationToken);
        await repository.ClearCartAsync(customerId, cancellationToken);
        return Unit.Value;
    }
}

public sealed record PlacePrepaidOrderCommand(string Provider, string IdempotencyKey)
    : IRequest<OrderDto>;

public sealed class PlacePrepaidOrderCommandValidator : AbstractValidator<PlacePrepaidOrderCommand>
{
    public PlacePrepaidOrderCommandValidator()
    {
        RuleFor(x => x.Provider)
            .Cascade(CascadeMode.Stop)
            .NotEmpty()
            .Must(value => PaymentProviders.IsValid(value.ToUpperInvariant()))
            .WithMessage("Provider must be MOMO or ZALOPAY.");
        RuleFor(x => x.IdempotencyKey).NotEmpty().MaximumLength(100);
    }
}

public sealed class PlacePrepaidOrderCommandHandler(
    ICustomerContext customerContext,
    ICommerceRepository repository) : IRequestHandler<PlacePrepaidOrderCommand, OrderDto>
{
    public async Task<OrderDto> Handle(
        PlacePrepaidOrderCommand request,
        CancellationToken cancellationToken)
    {
        var customerId = await customerContext.RequireCustomerUserIdAsync(cancellationToken);
        var result = await repository.PlacePrepaidOrderAsync(
            customerId,
            request.Provider.ToUpperInvariant(),
            request.IdempotencyKey.Trim(),
            cancellationToken);
        return result.RequireOrder();
    }
}

public sealed record ConfirmSandboxOrderPaymentCommand(long OrderId) : IRequest<OrderDto>;

public sealed class ConfirmSandboxOrderPaymentCommandValidator
    : AbstractValidator<ConfirmSandboxOrderPaymentCommand>
{
    public ConfirmSandboxOrderPaymentCommandValidator() => RuleFor(x => x.OrderId).GreaterThan(0);
}

public sealed class ConfirmSandboxOrderPaymentCommandHandler(
    ICustomerContext customerContext,
    ICommerceRepository repository) : IRequestHandler<ConfirmSandboxOrderPaymentCommand, OrderDto>
{
    public async Task<OrderDto> Handle(
        ConfirmSandboxOrderPaymentCommand request,
        CancellationToken cancellationToken)
    {
        var customerId = await customerContext.RequireCustomerUserIdAsync(cancellationToken);
        return (await repository.ConfirmSandboxPaymentAsync(
            customerId, request.OrderId, cancellationToken)).RequireOrder();
    }
}

public sealed record ListCustomerOrdersQuery : IRequest<IReadOnlyList<OrderDto>>;

public sealed class ListCustomerOrdersQueryHandler(
    ICustomerContext customerContext,
    ICommerceRepository repository) : IRequestHandler<ListCustomerOrdersQuery, IReadOnlyList<OrderDto>>
{
    public async Task<IReadOnlyList<OrderDto>> Handle(
        ListCustomerOrdersQuery request,
        CancellationToken cancellationToken)
    {
        var customerId = await customerContext.RequireCustomerUserIdAsync(cancellationToken);
        return (await repository.ListCustomerOrdersAsync(customerId, cancellationToken))
            .Select(row => row.ToDto()).ToArray();
    }
}

public sealed record GetCustomerOrderQuery(long OrderId) : IRequest<OrderDto>;

public sealed class GetCustomerOrderQueryValidator : AbstractValidator<GetCustomerOrderQuery>
{
    public GetCustomerOrderQueryValidator() => RuleFor(x => x.OrderId).GreaterThan(0);
}

public sealed class GetCustomerOrderQueryHandler(
    ICustomerContext customerContext,
    ICommerceRepository repository) : IRequestHandler<GetCustomerOrderQuery, OrderDto>
{
    public async Task<OrderDto> Handle(GetCustomerOrderQuery request, CancellationToken cancellationToken)
    {
        var customerId = await customerContext.RequireCustomerUserIdAsync(cancellationToken);
        return (await repository.GetCustomerOrderAsync(
            customerId, request.OrderId, cancellationToken))?.ToDto()
            ?? throw new NotFoundException(CommerceMessages.OrderNotFound);
    }
}

public sealed record CancelCustomerOrderCommand(long OrderId, string ExpectedStatus)
    : IRequest<OrderDto>;

public sealed class CancelCustomerOrderCommandValidator : AbstractValidator<CancelCustomerOrderCommand>
{
    public CancelCustomerOrderCommandValidator()
    {
        RuleFor(x => x.OrderId).GreaterThan(0);
        RuleFor(x => x.ExpectedStatus)
            .Must(value => value is OrderStatuses.PendingPayment or OrderStatuses.Placed)
            .WithMessage("Only a pending payment or placed order can be cancelled.");
    }
}

public sealed class CancelCustomerOrderCommandHandler(
    ICustomerContext customerContext,
    ICommerceRepository repository) : IRequestHandler<CancelCustomerOrderCommand, OrderDto>
{
    public async Task<OrderDto> Handle(
        CancelCustomerOrderCommand request,
        CancellationToken cancellationToken)
    {
        var customerId = await customerContext.RequireCustomerUserIdAsync(cancellationToken);
        return (await repository.CancelCustomerOrderAsync(
            customerId,
            request.OrderId,
            request.ExpectedStatus,
            cancellationToken)).RequireOrder();
    }
}

public sealed record ConfirmCustomerPickupCommand(long OrderId, string ExpectedStatus)
    : IRequest<OrderDto>;

public sealed class ConfirmCustomerPickupCommandValidator
    : AbstractValidator<ConfirmCustomerPickupCommand>
{
    public ConfirmCustomerPickupCommandValidator()
    {
        RuleFor(x => x.OrderId).GreaterThan(0);
        RuleFor(x => x.ExpectedStatus).Equal(OrderStatuses.ReadyForPickup);
    }
}

public sealed class ConfirmCustomerPickupCommandHandler(
    ICustomerContext customerContext,
    ICommerceRepository repository) : IRequestHandler<ConfirmCustomerPickupCommand, OrderDto>
{
    public async Task<OrderDto> Handle(
        ConfirmCustomerPickupCommand request,
        CancellationToken cancellationToken)
    {
        var customerId = await customerContext.RequireCustomerUserIdAsync(cancellationToken);
        return (await repository.ConfirmCustomerPickupAsync(
            customerId,
            request.OrderId,
            request.ExpectedStatus,
            cancellationToken)).RequireOrder();
    }
}

public sealed record ListSellerOrdersQuery(string? Status) : IRequest<IReadOnlyList<OrderDto>>;

public sealed class ListSellerOrdersQueryValidator : AbstractValidator<ListSellerOrdersQuery>
{
    public ListSellerOrdersQueryValidator() => RuleFor(x => x.Status)
        .Must(value => value is null || OrderStatuses.IsSellerVisible(value.ToUpperInvariant()))
        .WithMessage("Unsupported seller order status.");
}

public sealed class ListSellerOrdersQueryHandler(
    IVendorContext vendorContext,
    ICommerceRepository repository) : IRequestHandler<ListSellerOrdersQuery, IReadOnlyList<OrderDto>>
{
    public async Task<IReadOnlyList<OrderDto>> Handle(
        ListSellerOrdersQuery request,
        CancellationToken cancellationToken)
    {
        var vendorId = await vendorContext.RequireVendorIdAsync(cancellationToken);
        var status = string.IsNullOrWhiteSpace(request.Status)
            ? null
            : request.Status.Trim().ToUpperInvariant();
        return (await repository.ListSellerOrdersAsync(vendorId, status, cancellationToken))
            .Select(row => row.ToDto()).ToArray();
    }
}

public sealed record GetSellerOrderQuery(long OrderId) : IRequest<OrderDto>;

public sealed class GetSellerOrderQueryValidator : AbstractValidator<GetSellerOrderQuery>
{
    public GetSellerOrderQueryValidator() => RuleFor(x => x.OrderId).GreaterThan(0);
}

public sealed class GetSellerOrderQueryHandler(
    IVendorContext vendorContext,
    ICommerceRepository repository) : IRequestHandler<GetSellerOrderQuery, OrderDto>
{
    public async Task<OrderDto> Handle(GetSellerOrderQuery request, CancellationToken cancellationToken)
    {
        var vendorId = await vendorContext.RequireVendorIdAsync(cancellationToken);
        return (await repository.GetSellerOrderAsync(
            vendorId, request.OrderId, cancellationToken))?.ToDto()
            ?? throw new NotFoundException(CommerceMessages.OrderNotFound);
    }
}

public sealed record DecideSellerOrderCommand(
    long OrderId,
    string Decision,
    string? Reason,
    string ExpectedStatus) : IRequest<OrderDto>;

public sealed class DecideSellerOrderCommandValidator : AbstractValidator<DecideSellerOrderCommand>
{
    public DecideSellerOrderCommandValidator()
    {
        RuleFor(x => x.OrderId).GreaterThan(0);
        RuleFor(x => x.Decision)
            .Cascade(CascadeMode.Stop)
            .NotEmpty()
            .Must(value => value.ToUpperInvariant() is SellerOrderDecisions.Accept or SellerOrderDecisions.Reject)
            .WithMessage("Decision must be ACCEPT or REJECT.");
        RuleFor(x => x.ExpectedStatus).Equal(OrderStatuses.Placed);
        RuleFor(x => x.Reason).MaximumLength(500);
        RuleFor(x => x.Reason)
            .NotEmpty()
            .When(x => string.Equals(x.Decision, SellerOrderDecisions.Reject, StringComparison.OrdinalIgnoreCase))
            .WithMessage("A rejection reason is required.");
    }
}

public sealed class DecideSellerOrderCommandHandler(
    IVendorContext vendorContext,
    ICurrentUser currentUser,
    ICommerceRepository repository) : IRequestHandler<DecideSellerOrderCommand, OrderDto>
{
    public async Task<OrderDto> Handle(
        DecideSellerOrderCommand request,
        CancellationToken cancellationToken)
    {
        var vendorId = await vendorContext.RequireVendorIdAsync(cancellationToken);
        var actorUserId = currentUser.UserId ?? throw new AuthenticationException(AppMessages.SessionExpired);
        return (await repository.DecideSellerOrderAsync(
            vendorId,
            actorUserId,
            request.OrderId,
            request.Decision.ToUpperInvariant(),
            CommerceText.Normalize(request.Reason),
            request.ExpectedStatus,
            cancellationToken)).RequireOrder();
    }
}

public sealed record UpdateSellerOrderStatusCommand(
    long OrderId,
    string TargetStatus,
    string ExpectedStatus) : IRequest<OrderDto>;

public sealed class UpdateSellerOrderStatusCommandValidator
    : AbstractValidator<UpdateSellerOrderStatusCommand>
{
    public UpdateSellerOrderStatusCommandValidator()
    {
        RuleFor(x => x.OrderId).GreaterThan(0);
        RuleFor(x => x.TargetStatus)
            .Must(value => value is OrderStatuses.Preparing or OrderStatuses.ReadyForPickup)
            .WithMessage("Target status must be PREPARING or READY_FOR_PICKUP.");
        RuleFor(x => x.ExpectedStatus)
            .Must(value => value is OrderStatuses.Accepted or OrderStatuses.Preparing)
            .WithMessage("Expected status must be ACCEPTED or PREPARING.");
    }
}

public sealed class UpdateSellerOrderStatusCommandHandler(
    IVendorContext vendorContext,
    ICurrentUser currentUser,
    ICommerceRepository repository) : IRequestHandler<UpdateSellerOrderStatusCommand, OrderDto>
{
    public async Task<OrderDto> Handle(
        UpdateSellerOrderStatusCommand request,
        CancellationToken cancellationToken)
    {
        var vendorId = await vendorContext.RequireVendorIdAsync(cancellationToken);
        var actorUserId = currentUser.UserId ?? throw new AuthenticationException(AppMessages.SessionExpired);
        return (await repository.UpdateSellerOrderStatusAsync(
            vendorId,
            actorUserId,
            request.OrderId,
            request.TargetStatus,
            request.ExpectedStatus,
            cancellationToken)).RequireOrder();
    }
}

public sealed record ConfirmSellerHandoverCommand(long OrderId, string ExpectedStatus)
    : IRequest<OrderDto>;

public sealed class ConfirmSellerHandoverCommandValidator
    : AbstractValidator<ConfirmSellerHandoverCommand>
{
    public ConfirmSellerHandoverCommandValidator()
    {
        RuleFor(x => x.OrderId).GreaterThan(0);
        RuleFor(x => x.ExpectedStatus).Equal(OrderStatuses.ReadyForPickup);
    }
}

public sealed class ConfirmSellerHandoverCommandHandler(
    IVendorContext vendorContext,
    ICurrentUser currentUser,
    ICommerceRepository repository) : IRequestHandler<ConfirmSellerHandoverCommand, OrderDto>
{
    public async Task<OrderDto> Handle(
        ConfirmSellerHandoverCommand request,
        CancellationToken cancellationToken)
    {
        var vendorId = await vendorContext.RequireVendorIdAsync(cancellationToken);
        var actorUserId = currentUser.UserId ?? throw new AuthenticationException(AppMessages.SessionExpired);
        return (await repository.ConfirmSellerHandoverAsync(
            vendorId,
            actorUserId,
            request.OrderId,
            request.ExpectedStatus,
            cancellationToken)).RequireOrder();
    }
}

public sealed record GetSalesSummaryQuery(string Period) : IRequest<SalesSummaryDto>;

public sealed class GetSalesSummaryQueryValidator : AbstractValidator<GetSalesSummaryQuery>
{
    public GetSalesSummaryQueryValidator() => RuleFor(x => x.Period)
        .Cascade(CascadeMode.Stop)
        .NotEmpty()
        .Must(value => SalesPeriods.IsValid(value.ToUpperInvariant()))
        .WithMessage("Period must be DAY, WEEK or MONTH.");
}

public sealed class GetSalesSummaryQueryHandler(
    IVendorContext vendorContext,
    ICommerceRepository repository) : IRequestHandler<GetSalesSummaryQuery, SalesSummaryDto>
{
    public async Task<SalesSummaryDto> Handle(
        GetSalesSummaryQuery request,
        CancellationToken cancellationToken)
    {
        var vendorId = await vendorContext.RequireVendorIdAsync(cancellationToken);
        return (await repository.GetSalesSummaryAsync(
            vendorId,
            request.Period.ToUpperInvariant(),
            cancellationToken)).ToDto();
    }
}

internal static class CommerceMapping
{
    public static MarketplaceMenuItemDto ToDto(this MarketplaceMenuItemRow row) => new(
        row.MenuItemId, row.StorefrontId, row.StorefrontName, row.ItemName,
        row.Description, row.ImageUrl, row.UnitPrice, row.AvailabilityStatus,
        row.CategoryId, row.CategoryName);

    public static CartDto ToDto(this CommerceCartRow row) => new(
        row.CartId,
        row.StorefrontId,
        row.StorefrontName,
        row.StorefrontStatus,
        row.Items.Select(item => new CartItemDto(
            item.CartItemId, item.MenuItemId, item.ItemName, item.ImageUrl,
            item.UnitPrice, item.AvailabilityStatus, item.Quantity, item.Note)).ToArray(),
        row.Subtotal);

    public static OrderDto ToDto(this CommerceOrderRow row) => new(
        row.OrderId, row.OrderCode, row.CustomerUserId, row.CustomerName,
        row.StorefrontId, row.StorefrontName, row.OrderStatus, row.SubtotalAmount,
        row.TotalAmount, row.RejectionReason, row.PaymentProvider, row.PaymentStatus,
        row.RefundAmount, row.RefundReason, row.RefundStatus,
        row.RefundRequestedAt, row.RefundCompletedAt,
        row.PlacedAt, row.CompletedAt, row.CreatedAt,
        row.Items.Select(item => new OrderItemDto(
            item.OrderItemId, item.MenuItemId, item.ItemName, item.UnitPrice,
            item.Quantity, item.Note)).ToArray(),
        row.History.Select(item => new OrderHistoryDto(
            item.HistoryId, item.FromStatus, item.ToStatus, item.Note, item.ChangedAt)).ToArray());

    public static SalesSummaryDto ToDto(this CommerceSalesSummaryRow row) => new(
        row.Period, row.FromUtc, row.ToUtc, row.CompletedOrderCount, row.GrossSales,
        row.RefundedAmount, row.NetSales,
        row.Orders.Select(order => order.ToDto()).ToArray());

    public static CartDto RequireCart(this CartMutationResult result, string notFoundMessage) =>
        result.Outcome switch
        {
            CartMutationOutcome.Updated => result.Cart!.ToDto(),
            CartMutationOutcome.NotFound => throw new NotFoundException(notFoundMessage),
            CartMutationOutcome.MenuItemUnavailable => throw new DomainRuleException(
                CommerceMessages.MenuItemUnavailable),
            CartMutationOutcome.StorefrontUnavailable => throw new DomainRuleException(
                CommerceMessages.StorefrontUnavailable),
            _ => throw new ConflictException(CommerceMessages.CartConflict),
        };

    public static OrderDto RequireOrder(this OrderMutationResult result) =>
        result.Outcome switch
        {
            OrderMutationOutcome.Updated => result.Order!.ToDto(),
            OrderMutationOutcome.NotFound => throw new NotFoundException(CommerceMessages.OrderNotFound),
            OrderMutationOutcome.EmptyCart => throw new DomainRuleException(CommerceMessages.CartEmpty),
            OrderMutationOutcome.MenuItemUnavailable => throw new DomainRuleException(
                CommerceMessages.MenuItemUnavailable),
            OrderMutationOutcome.StorefrontUnavailable => throw new DomainRuleException(
                CommerceMessages.StorefrontUnavailable),
            OrderMutationOutcome.PaymentNotFound => throw new DomainRuleException(
                CommerceMessages.PaymentNotFound),
            _ => throw new ConflictException(CommerceMessages.OrderConflict),
        };
}

internal static class CommerceText
{
    public static string? Normalize(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
