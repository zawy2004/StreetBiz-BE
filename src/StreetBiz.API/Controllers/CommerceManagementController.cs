using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using StreetBiz.Application.Features.Commerce;

namespace StreetBiz.API.Controllers;

[ApiController]
[Authorize]
[Route("api/seller/storefronts")]
public sealed class SellerStorefrontsController(ICommerceManagement service) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> List(CancellationToken ct) => Ok(await service.Stores(ct));
    [HttpPost]
    public async Task<IActionResult> Create(StorefrontInput input, CancellationToken ct) => Ok(await service.SaveStore(null, input, ct));
    [HttpPut("{storefrontId:long}")]
    public async Task<IActionResult> Update(long storefrontId, StorefrontInput input, CancellationToken ct) => Ok(await service.SaveStore(storefrontId, input, ct));
    [HttpGet("food-categories")]
    public async Task<IActionResult> Categories(CancellationToken ct) => Ok(await service.Categories(ct));
    [HttpGet("{storefrontId:long}/menu-items")]
    public async Task<IActionResult> Menu(long storefrontId, CancellationToken ct) => Ok(await service.Menu(storefrontId, ct));
    [HttpPost("{storefrontId:long}/menu-items")]
    public async Task<IActionResult> AddItem(long storefrontId, SellerMenuInput input, CancellationToken ct) => Ok(await service.SaveMenu(storefrontId, null, input, ct));
    [HttpPut("{storefrontId:long}/menu-items/{itemId:long}")]
    public async Task<IActionResult> UpdateItem(long storefrontId, long itemId, SellerMenuInput input, CancellationToken ct) => Ok(await service.SaveMenu(storefrontId, itemId, input, ct));
    [HttpDelete("{storefrontId:long}/menu-items/{itemId:long}")]
    public async Task<IActionResult> Archive(long storefrontId, long itemId, CancellationToken ct)
    {
        await service.ArchiveMenu(storefrontId, itemId, ct);
        return NoContent();
    }
}

[ApiController]
[Authorize]
[Route("api/orders/{orderId:long}/complaints")]
public sealed class CustomerComplaintsController(ICommerceManagement service) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> List(long orderId, CancellationToken ct) => Ok(await service.Complaints(orderId, ct));
    [HttpPost]
    public async Task<IActionResult> Create(long orderId, CustomerComplaintInput input, CancellationToken ct) => Ok(await service.Complain(orderId, input, ct));
}
