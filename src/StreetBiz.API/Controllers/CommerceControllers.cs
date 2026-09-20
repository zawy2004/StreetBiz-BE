using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using StreetBiz.Application.DTOs.Commerce;
using StreetBiz.Application.Features.Commerce;

namespace StreetBiz.API.Controllers;

[ApiController]
[Route("api/marketplace")]
public sealed class MarketplaceController(ISender sender) : ControllerBase
{
    [HttpGet("menu-items")]
    public async Task<ActionResult<IReadOnlyList<MarketplaceMenuItemDto>>> Search(
        [FromQuery] string? query,
        [FromQuery] int? wardId,
        [FromQuery] int? categoryId,
        [FromQuery] decimal? minPrice,
        [FromQuery] decimal? maxPrice,
        [FromQuery] bool? openNow,
        [FromQuery] string? sort,
        [FromQuery] int take = 50,
        CancellationToken cancellationToken = default) =>
        Ok(await sender.Send(
            new SearchMarketplaceMenuQuery(query, take, wardId, categoryId, minPrice, maxPrice, openNow, sort),
            cancellationToken));

    [HttpGet("menu-items/{menuItemId:long}")]
    public async Task<ActionResult<MarketplaceMenuItemDto>> Get(
        long menuItemId,
        CancellationToken cancellationToken) =>
        Ok(await sender.Send(new GetMarketplaceMenuItemQuery(menuItemId), cancellationToken));

    [HttpGet("service-areas")]
    public async Task<ActionResult<IReadOnlyList<ServiceAreaDto>>> ServiceAreas(
        [FromQuery] decimal? latitude,
        [FromQuery] decimal? longitude,
        CancellationToken cancellationToken) =>
        Ok(await sender.Send(new ListServiceAreasQuery(latitude, longitude), cancellationToken));

    [HttpGet("categories")]
    public async Task<ActionResult<IReadOnlyList<MarketplaceCategoryDto>>> Categories(
        CancellationToken cancellationToken) =>
        Ok(await sender.Send(new ListMarketplaceCategoriesQuery(), cancellationToken));

    [HttpGet("storefronts")]
    public async Task<ActionResult<IReadOnlyList<StorefrontSummaryDto>>> Storefronts(
        [FromQuery] string? query,
        [FromQuery] int? wardId,
        [FromQuery] int? categoryId,
        [FromQuery] bool? openNow,
        [FromQuery] decimal? latitude,
        [FromQuery] decimal? longitude,
        [FromQuery] double? radiusMeters,
        [FromQuery] string? sort,
        [FromQuery] int take = 50,
        CancellationToken cancellationToken = default) =>
        Ok(await sender.Send(
            new ListStorefrontsQuery(query, wardId, categoryId, openNow, latitude, longitude, radiusMeters, sort, take),
            cancellationToken));

    [HttpGet("storefronts/{storefrontId:long}")]
    public async Task<ActionResult<StorefrontDetailDto>> Storefront(
        long storefrontId,
        [FromQuery] decimal? latitude,
        [FromQuery] decimal? longitude,
        CancellationToken cancellationToken) =>
        Ok(await sender.Send(new GetStorefrontQuery(storefrontId, latitude, longitude), cancellationToken));
}

[ApiController]
[Authorize]
[Route("api/cart")]
public sealed class CartController(ISender sender) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<CartDto?>> Get(CancellationToken cancellationToken) =>
        Ok(await sender.Send(new GetCartQuery(), cancellationToken));

    [HttpPost("items")]
    public async Task<ActionResult<CartDto>> AddItem(
        AddCartItemRequest request,
        CancellationToken cancellationToken) =>
        Ok(await sender.Send(new AddCartItemCommand(
            request.MenuItemId, request.Quantity, request.Note), cancellationToken));

    [HttpPut("items/{menuItemId:long}")]
    public async Task<ActionResult<CartDto>> UpdateItem(
        long menuItemId,
        UpdateCartItemRequest request,
        CancellationToken cancellationToken) =>
        Ok(await sender.Send(new UpdateCartItemCommand(
            menuItemId, request.Quantity, request.Note), cancellationToken));

    [HttpDelete("items/{menuItemId:long}")]
    public async Task<ActionResult<CartDto?>> RemoveItem(
        long menuItemId,
        CancellationToken cancellationToken) =>
        Ok(await sender.Send(new RemoveCartItemCommand(menuItemId), cancellationToken));

    [HttpDelete]
    public async Task<IActionResult> Clear(CancellationToken cancellationToken)
    {
        await sender.Send(new ClearCartCommand(), cancellationToken);
        return NoContent();
    }
}

[ApiController]
[Authorize]
[Route("api/orders")]
public sealed class OrdersController(
    ISender sender,
    IHostEnvironment environment,
    IConfiguration configuration) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<OrderDto>>> List(
        CancellationToken cancellationToken) =>
        Ok(await sender.Send(new ListCustomerOrdersQuery(), cancellationToken));

    [HttpPost]
    public async Task<ActionResult<OrderDto>> Place(
        PlaceOrderRequest request,
        CancellationToken cancellationToken) =>
        Ok(await sender.Send(new PlacePrepaidOrderCommand(
            request.Provider, request.IdempotencyKey), cancellationToken));

    [HttpGet("{orderId:long}")]
    public async Task<ActionResult<OrderDto>> Get(
        long orderId,
        CancellationToken cancellationToken) =>
        Ok(await sender.Send(new GetCustomerOrderQuery(orderId), cancellationToken));

    [HttpPost("{orderId:long}/cancel")]
    public async Task<ActionResult<OrderDto>> Cancel(
        long orderId,
        ExpectedOrderStatusRequest request,
        CancellationToken cancellationToken) =>
        Ok(await sender.Send(new CancelCustomerOrderCommand(
            orderId, request.ExpectedStatus), cancellationToken));

    [HttpPost("{orderId:long}/confirm-pickup")]
    public async Task<ActionResult<OrderDto>> ConfirmPickup(
        long orderId,
        ExpectedOrderStatusRequest request,
        CancellationToken cancellationToken) =>
        Ok(await sender.Send(new ConfirmCustomerPickupCommand(
            orderId, request.ExpectedStatus), cancellationToken));

    [HttpPost("{orderId:long}/payment/sandbox-confirm")]
    [ApiExplorerSettings(IgnoreApi = true)]
    public async Task<ActionResult<OrderDto>> ConfirmSandboxPayment(
        long orderId,
        CancellationToken cancellationToken)
    {
        if (!environment.IsDevelopment()
            || !configuration.GetValue<bool>("Payments:SandboxEnabled"))
        {
            return NotFound();
        }

        return Ok(await sender.Send(
            new ConfirmSandboxOrderPaymentCommand(orderId), cancellationToken));
    }
}

[ApiController]
[Authorize]
[Route("api/seller/orders")]
public sealed class SellerOrdersController(ISender sender) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<OrderDto>>> List(
        [FromQuery] string? status,
        CancellationToken cancellationToken) =>
        Ok(await sender.Send(new ListSellerOrdersQuery(status), cancellationToken));

    [HttpGet("{orderId:long}")]
    public async Task<ActionResult<OrderDto>> Get(
        long orderId,
        CancellationToken cancellationToken) =>
        Ok(await sender.Send(new GetSellerOrderQuery(orderId), cancellationToken));

    [HttpPost("{orderId:long}/decision")]
    public async Task<ActionResult<OrderDto>> Decide(
        long orderId,
        SellerOrderDecisionRequest request,
        CancellationToken cancellationToken) =>
        Ok(await sender.Send(new DecideSellerOrderCommand(
            orderId,
            request.Decision,
            request.Reason,
            request.ExpectedStatus), cancellationToken));

    [HttpPost("{orderId:long}/status")]
    public async Task<ActionResult<OrderDto>> UpdateStatus(
        long orderId,
        SellerOrderStatusRequest request,
        CancellationToken cancellationToken) =>
        Ok(await sender.Send(new UpdateSellerOrderStatusCommand(
            orderId,
            request.TargetStatus,
            request.ExpectedStatus), cancellationToken));

    [HttpPost("{orderId:long}/handover")]
    public async Task<ActionResult<OrderDto>> ConfirmHandover(
        long orderId,
        ExpectedOrderStatusRequest request,
        CancellationToken cancellationToken) =>
        Ok(await sender.Send(new ConfirmSellerHandoverCommand(
            orderId, request.ExpectedStatus), cancellationToken));

    [HttpGet("sales-summary")]
    public async Task<ActionResult<SalesSummaryDto>> SalesSummary(
        [FromQuery] string period = "DAY",
        CancellationToken cancellationToken = default) =>
        Ok(await sender.Send(new GetSalesSummaryQuery(period), cancellationToken));
}

public sealed record AddCartItemRequest(long MenuItemId, int Quantity, string? Note);
public sealed record UpdateCartItemRequest(int Quantity, string? Note);
public sealed record PlaceOrderRequest(string Provider, string IdempotencyKey);
public sealed record ExpectedOrderStatusRequest(string ExpectedStatus);
public sealed record SellerOrderDecisionRequest(string Decision, string? Reason, string ExpectedStatus);
public sealed record SellerOrderStatusRequest(string TargetStatus, string ExpectedStatus);
