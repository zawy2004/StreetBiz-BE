using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using StreetBiz.Application.Features.Commerce;

namespace StreetBiz.API.Controllers;

[ApiController]
[Authorize]
[Route("api/orders/{orderId:long}/review")]
public sealed class OrderReviewsController(ICommerceManagement service) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> Get(long orderId, CancellationToken ct) => new JsonResult(await service.Review(orderId, ct));
    [HttpPut]
    public async Task<IActionResult> Save(long orderId, OrderReviewInput input, CancellationToken ct) => Ok(await service.SaveReview(orderId, input, ct));
}
