using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using StreetBiz.Application.DTOs.Commerce;
using StreetBiz.Application.Common.Exceptions;
using StreetBiz.Application.Common.Security;
using StreetBiz.Application.Features.Commerce;
using StreetBiz.API.Hubs;

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
    IConfiguration configuration,
    IOrderRealtimePublisher realtime) : ControllerBase
{
    [HttpPost("checkout")]
    public async Task<ActionResult<CheckoutDto>> Checkout(
        CheckoutOrderRequest request,
        [FromHeader(Name = "Idempotency-Key")] string idempotencyKey,
        CancellationToken cancellationToken)
    {
        var result = await sender.Send(new CheckoutOrderCommand(
            request.CartId, request.Provider, idempotencyKey), cancellationToken);
        await realtime.PublishAsync(result.OrderId, result.OrderStatus, cancellationToken);
        return Ok(result);
    }

    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<OrderDto>>> List(
        CancellationToken cancellationToken) =>
        Ok(await sender.Send(new ListCustomerOrdersQuery(), cancellationToken));

    [HttpGet("me")]
    public async Task<ActionResult<PagedResultDto<OrderDto>>> ListMine(
        [FromQuery] string? status,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        [FromQuery] DateTime? fromDate = null,
        [FromQuery] DateTime? toDate = null,
        [FromQuery] string sort = "createdAt_desc",
        CancellationToken cancellationToken = default)
    {
        var orders = await sender.Send(new ListCustomerOrdersQuery(), cancellationToken);
        return Ok(OrderApiPaging.Filter(orders, status, page, pageSize, fromDate, toDate, sort));
    }

    [HttpPost]
    public async Task<ActionResult<OrderDto>> Place(
        PlaceOrderRequest request,
        CancellationToken cancellationToken)
    {
        var result = await sender.Send(new PlacePrepaidOrderCommand(
            request.Provider, request.IdempotencyKey), cancellationToken);
        await realtime.PublishAsync(result, cancellationToken);
        return Ok(result);
    }

    [HttpGet("{orderId:long}")]
    public async Task<ActionResult<OrderDto>> Get(
        long orderId,
        CancellationToken cancellationToken) =>
        Ok(await sender.Send(new GetCustomerOrderQuery(orderId), cancellationToken));

    [HttpPost("{orderId:long}/cancel")]
    public async Task<ActionResult<OrderDto>> Cancel(
        long orderId,
        ExpectedOrderStatusRequest? request,
        CancellationToken cancellationToken)
    {
        var expected = request?.ExpectedStatus
            ?? (await sender.Send(new GetCustomerOrderQuery(orderId), cancellationToken)).OrderStatus;
        var result = await sender.Send(new CancelCustomerOrderCommand(
            orderId, expected), cancellationToken);
        await realtime.PublishAsync(result, cancellationToken);
        return Ok(result);
    }

    [HttpPost("{orderId:long}/confirm-pickup")]
    public async Task<ActionResult<OrderDto>> ConfirmPickup(
        long orderId,
        ExpectedOrderStatusRequest? request,
        CancellationToken cancellationToken)
    {
        var result = await sender.Send(new ConfirmCustomerPickupCommand(
            orderId, request?.ExpectedStatus ?? OrderStatuses.ReadyForPickup), cancellationToken);
        await realtime.PublishAsync(result, cancellationToken);
        return Ok(result);
    }

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

        var result = await sender.Send(
            new ConfirmSandboxOrderPaymentCommand(orderId), cancellationToken);
        await realtime.PublishAsync(result, cancellationToken);
        return Ok(result);
    }
}

[ApiController]
[AllowAnonymous]
[Route("api/payments")]
public sealed class PaymentCallbacksController(
    ISender sender,
    IOrderRealtimePublisher realtime) : ControllerBase
{
    [HttpPost("{provider}/callback")]
    public async Task<ActionResult<PaymentCallbackReceiptDto>> Callback(
        string provider,
        CancellationToken cancellationToken)
    {
        using var reader = new StreamReader(Request.Body);
        var rawPayload = await reader.ReadToEndAsync(cancellationToken);
        var signature = Request.Headers["X-Payment-Signature"].FirstOrDefault();
        var result = await sender.Send(
            new ProcessPaymentCallbackCommand(provider, rawPayload, signature),
            cancellationToken);
        if (result.OrderId is { } orderId && result.OrderStatus is { } orderStatus)
        {
            await realtime.PublishAsync(orderId, orderStatus, cancellationToken);
        }
        return Ok(result);
    }
}

[ApiController]
[Authorize]
[Route("api/seller/orders")]
public sealed class SellerOrdersController(
    ISender sender,
    IOrderRealtimePublisher realtime) : ControllerBase
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
        CancellationToken cancellationToken)
    {
        var result = await sender.Send(new DecideSellerOrderCommand(
            orderId, request.Decision, request.Reason, request.ExpectedStatus), cancellationToken);
        await realtime.PublishAsync(result, cancellationToken);
        return Ok(result);
    }

    [HttpPost("{orderId:long}/status")]
    public async Task<ActionResult<OrderDto>> UpdateStatus(
        long orderId,
        SellerOrderStatusRequest request,
        CancellationToken cancellationToken)
    {
        var result = await sender.Send(new UpdateSellerOrderStatusCommand(
            orderId, request.TargetStatus, request.ExpectedStatus), cancellationToken);
        await realtime.PublishAsync(result, cancellationToken);
        return Ok(result);
    }

    [HttpPost("{orderId:long}/handover")]
    public async Task<ActionResult<OrderDto>> ConfirmHandover(
        long orderId,
        ExpectedOrderStatusRequest request,
        CancellationToken cancellationToken)
    {
        var result = await sender.Send(new ConfirmSellerHandoverCommand(
            orderId, request.ExpectedStatus), cancellationToken);
        await realtime.PublishAsync(result, cancellationToken);
        return Ok(result);
    }

    [HttpGet("sales-summary")]
    public async Task<ActionResult<SalesSummaryDto>> SalesSummary(
        [FromQuery] string period = "DAY",
        CancellationToken cancellationToken = default) =>
        Ok(await sender.Send(new GetSalesSummaryQuery(period), cancellationToken));
}

[ApiController]
[Authorize]
[Route("api/vendor/orders")]
public sealed class VendorOrdersController(
    ISender sender,
    IOrderRealtimePublisher realtime) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<PagedResultDto<OrderDto>>> List(
        [FromQuery] string? status,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        [FromQuery] DateTime? fromDate = null,
        [FromQuery] DateTime? toDate = null,
        [FromQuery] string sort = "createdAt_desc",
        CancellationToken cancellationToken = default)
    {
        var orders = await sender.Send(new ListSellerOrdersQuery(status), cancellationToken);
        return Ok(OrderApiPaging.Filter(
            orders, null, page, pageSize, fromDate, toDate, sort));
    }

    [HttpGet("{orderId:long}")]
    public async Task<ActionResult<OrderDto>> Get(
        long orderId,
        CancellationToken cancellationToken) =>
        Ok(await sender.Send(new GetSellerOrderQuery(orderId), cancellationToken));

    [HttpPost("{orderId:long}/accept")]
    public async Task<ActionResult<OrderDto>> Accept(
        long orderId,
        CancellationToken cancellationToken)
    {
        var result = await sender.Send(new DecideSellerOrderCommand(
            orderId, SellerOrderDecisions.Accept, null, OrderStatuses.Placed), cancellationToken);
        await realtime.PublishAsync(result, cancellationToken);
        return Ok(result);
    }

    [HttpPost("{orderId:long}/reject")]
    public async Task<ActionResult<OrderDto>> Reject(
        long orderId,
        VendorRejectOrderRequest request,
        CancellationToken cancellationToken)
    {
        var result = await sender.Send(new DecideSellerOrderCommand(
            orderId, SellerOrderDecisions.Reject, request.Reason, OrderStatuses.Placed), cancellationToken);
        await realtime.PublishAsync(result, cancellationToken);
        return Ok(result);
    }

    [HttpPost("{orderId:long}/preparing")]
    public async Task<ActionResult<OrderDto>> Preparing(
        long orderId,
        CancellationToken cancellationToken)
    {
        var result = await sender.Send(new UpdateSellerOrderStatusCommand(
            orderId, OrderStatuses.Preparing, OrderStatuses.Accepted), cancellationToken);
        await realtime.PublishAsync(result, cancellationToken);
        return Ok(result);
    }

    [HttpPost("{orderId:long}/ready-for-pickup")]
    public async Task<ActionResult<OrderDto>> ReadyForPickup(
        long orderId,
        CancellationToken cancellationToken)
    {
        var result = await sender.Send(new UpdateSellerOrderStatusCommand(
            orderId, OrderStatuses.ReadyForPickup, OrderStatuses.Preparing), cancellationToken);
        await realtime.PublishAsync(result, cancellationToken);
        return Ok(result);
    }

    [HttpPost("{orderId:long}/confirm-handover")]
    public async Task<ActionResult<OrderDto>> ConfirmHandover(
        long orderId,
        CancellationToken cancellationToken)
    {
        var result = await sender.Send(new ConfirmSellerHandoverCommand(
            orderId, OrderStatuses.ReadyForPickup), cancellationToken);
        await realtime.PublishAsync(result, cancellationToken);
        return Ok(result);
    }

    [HttpGet("sales-summary")]
    public async Task<ActionResult<DetailedSalesSummaryDto>> SalesSummary(
        [FromQuery] DateTime? fromDate,
        [FromQuery] DateTime? toDate,
        [FromQuery] string groupBy = "day",
        CancellationToken cancellationToken = default)
    {
        var to = DateTime.SpecifyKind(toDate ?? DateTime.UtcNow, DateTimeKind.Utc);
        var from = DateTime.SpecifyKind(fromDate ?? to.AddDays(-30), DateTimeKind.Utc);
        if (from > to || groupBy.ToLowerInvariant() is not ("day" or "week" or "month"))
        {
            throw new ValidationAppException(new Dictionary<string, string[]>
            {
                ["dateRange"] = ["fromDate must not exceed toDate; groupBy must be day, week or month."]
            });
        }

        var orders = (await sender.Send(
                new ListSellerOrdersQuery(OrderStatuses.Completed), cancellationToken))
            .Where(order => order.CompletedAt >= from && order.CompletedAt <= to)
            .ToArray();
        var groups = orders.GroupBy(order => GroupKey(
                order.CompletedAt!.Value, groupBy.ToLowerInvariant()))
            .OrderBy(group => group.Key)
            .Select(group =>
            {
                var gross = group.Sum(order => order.TotalAmount);
                var refunded = group.Where(order => order.RefundStatus == "SUCCESS")
                    .Sum(order => order.RefundAmount ?? 0);
                return new SalesBucketDto(
                    group.Key, group.Count(), gross, refunded, gross - refunded);
            }).ToArray();
        var gross = orders.Sum(order => order.TotalAmount);
        var refunded = orders.Where(order => order.RefundStatus == "SUCCESS")
            .Sum(order => order.RefundAmount ?? 0);
        return Ok(new DetailedSalesSummaryDto(
            from, to, groupBy.ToLowerInvariant(), orders.Length,
            gross, refunded, gross - refunded, groups));
    }

    private static string GroupKey(DateTime value, string groupBy)
    {
        var utc = DateTime.SpecifyKind(value, DateTimeKind.Utc);
        if (groupBy == "month")
        {
            return utc.ToString("yyyy-MM");
        }
        if (groupBy == "week")
        {
            var monday = utc.Date.AddDays(-(((int)utc.DayOfWeek + 6) % 7));
            return monday.ToString("yyyy-MM-dd");
        }
        return utc.ToString("yyyy-MM-dd");
    }
}

internal static class OrderApiPaging
{
    public static PagedResultDto<OrderDto> Filter(
        IReadOnlyList<OrderDto> source,
        string? status,
        int page,
        int pageSize,
        DateTime? fromDate,
        DateTime? toDate,
        string sort)
    {
        if (page < 1 || pageSize is < 1 or > 100
            || (fromDate.HasValue && toDate.HasValue && fromDate > toDate))
        {
            throw new ValidationAppException(new Dictionary<string, string[]>
            {
                ["pagination"] = ["page >= 1, pageSize between 1 and 100, and fromDate <= toDate are required."]
            });
        }

        IEnumerable<OrderDto> query = source;
        if (!string.IsNullOrWhiteSpace(status))
        {
            var normalized = status.Trim().ToUpperInvariant();
            if (!OrderStatuses.IsValid(normalized))
            {
                throw new ValidationAppException(new Dictionary<string, string[]>
                {
                    ["status"] = ["Unsupported order status."]
                });
            }
            query = query.Where(order => order.OrderStatus == normalized);
        }
        if (fromDate.HasValue)
        {
            query = query.Where(order => order.CreatedAt >= fromDate.Value);
        }
        if (toDate.HasValue)
        {
            query = query.Where(order => order.CreatedAt <= toDate.Value);
        }

        query = sort.ToLowerInvariant() switch
        {
            "createdat_asc" => query.OrderBy(order => order.CreatedAt),
            "createdat_desc" => query.OrderByDescending(order => order.CreatedAt),
            "ordercode_asc" => query.OrderBy(order => order.OrderCode),
            "ordercode_desc" => query.OrderByDescending(order => order.OrderCode),
            _ => throw new ValidationAppException(new Dictionary<string, string[]>
            {
                ["sort"] = ["sort must be createdAt_asc, createdAt_desc, orderCode_asc or orderCode_desc."]
            }),
        };
        var rows = query.ToArray();
        return new PagedResultDto<OrderDto>(
            rows.Skip((page - 1) * pageSize).Take(pageSize).ToArray(),
            page,
            pageSize,
            rows.Length,
            (int)Math.Ceiling(rows.Length / (double)pageSize));
    }
}

public sealed record AddCartItemRequest(long MenuItemId, int Quantity, string? Note);
public sealed record UpdateCartItemRequest(int Quantity, string? Note);
public sealed record PlaceOrderRequest(string Provider, string IdempotencyKey);
public sealed record CheckoutOrderRequest(long CartId, string Provider);
public sealed record ExpectedOrderStatusRequest(string ExpectedStatus);
public sealed record SellerOrderDecisionRequest(string Decision, string? Reason, string ExpectedStatus);
public sealed record SellerOrderStatusRequest(string TargetStatus, string ExpectedStatus);
public sealed record VendorRejectOrderRequest(string Reason);
