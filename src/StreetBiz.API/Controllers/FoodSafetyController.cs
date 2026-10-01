using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using StreetBiz.Application.Features.FoodSafety;

namespace StreetBiz.API.Controllers;

/// <summary>Vendor side of the ATTP certificate workflow.</summary>
[ApiController]
[Authorize]
[Route("api/vendor/food-safety")]
public sealed class VendorFoodSafetyController(IFoodSafetyService service) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<FoodSafetyApplicationDto>>> List(CancellationToken ct) =>
        Ok(await service.VendorList(ct));

    [HttpGet("{id:long}")]
    public async Task<ActionResult<FoodSafetyApplicationDto>> Get(long id, CancellationToken ct) =>
        Ok(await service.VendorGet(id, ct));

    [HttpPost]
    public async Task<ActionResult<FoodSafetyApplicationDto>> Submit(FoodSafetySubmitInput input, CancellationToken ct) =>
        Ok(await service.Submit(input, ct));

    [HttpPut("{id:long}")]
    public async Task<ActionResult<FoodSafetyApplicationDto>> Resubmit(long id, FoodSafetySubmitInput input, CancellationToken ct) =>
        Ok(await service.Resubmit(id, input, ct));

    [HttpPost("{id:long}/withdraw")]
    public async Task<ActionResult<FoodSafetyApplicationDto>> Withdraw(long id, CancellationToken ct) =>
        Ok(await service.Withdraw(id, ct));
}

/// <summary>Ward side: review, forward to the department, record the department's result.</summary>
[ApiController]
[Authorize]
[Route("api/ward/food-safety")]
public sealed class WardFoodSafetyController(IFoodSafetyService service) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<FoodSafetyApplicationDto>>> List(
        [FromQuery] string? status, CancellationToken ct) =>
        Ok(await service.WardList(status, ct));

    [HttpGet("{id:long}")]
    public async Task<ActionResult<FoodSafetyApplicationDto>> Get(long id, CancellationToken ct) =>
        Ok(await service.WardGet(id, ct));

    [HttpPost("{id:long}/decision")]
    public async Task<ActionResult<FoodSafetyApplicationDto>> Decide(
        long id, FoodSafetyDecisionInput input, CancellationToken ct) =>
        Ok(await service.Decide(id, input, ct));
}
