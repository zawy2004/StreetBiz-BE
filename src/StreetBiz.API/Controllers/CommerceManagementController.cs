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
    /// <summary>STORE-03: the weekly opening hours, with the hours the ward rents the pitch for.</summary>
    [HttpGet("{storefrontId:long}/business-hours")]
    public async Task<IActionResult> BusinessHours(long storefrontId, CancellationToken ct) => Ok(await service.BusinessHours(storefrontId, ct));
    /// <summary>STORE-03: replace the whole week in one transaction.</summary>
    [HttpPut("{storefrontId:long}/business-hours")]
    public async Task<IActionResult> SaveBusinessHours(long storefrontId, StorefrontHoursInput input, CancellationToken ct) => Ok(await service.SaveBusinessHours(storefrontId, input, ct));
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
