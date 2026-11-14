using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using StreetBiz.Application.Features.PayoutAccounts;

namespace StreetBiz.API.Controllers;

/// <summary>
/// A household's receiving account: the payOS channel its orders are paid into. StreetBiz holds no
/// money; customers pay straight into this account. Keys are accepted here and never returned.
/// </summary>
[ApiController]
[Authorize]
[Route("api/vendor/payout-account")]
public sealed class VendorPayoutAccountController(ISender sender) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<PayoutAccountDto>> Get(CancellationToken cancellationToken) =>
        Ok(await sender.Send(new GetVendorPayoutAccountQuery(), cancellationToken));

    /// <summary>Connects the channel (or replaces its keys): payOS must accept them first.</summary>
    [HttpPut]
    public async Task<ActionResult<PayoutAccountDto>> Connect(
        ConnectPayoutAccountRequest request, CancellationToken cancellationToken) =>
        Ok(await sender.Send(new ConnectVendorPayoutAccountCommand(request), cancellationToken));

    /// <summary>Asks payOS again with the stored keys; refused keys put the account in ERROR.</summary>
    [HttpPost("check")]
    public async Task<ActionResult<PayoutAccountDto>> Check(CancellationToken cancellationToken) =>
        Ok(await sender.Send(new CheckVendorPayoutAccountCommand(), cancellationToken));

    /// <summary>Stops new payments to the account. Its keys stay, so late webhooks of earlier payments still verify.</summary>
    [HttpDelete]
    public async Task<ActionResult<PayoutAccountDto>> Disconnect(CancellationToken cancellationToken) =>
        Ok(await sender.Send(new DisconnectVendorPayoutAccountCommand(), cancellationToken));
}

/// <summary>
/// A ward's receiving account: the payOS channel the rental fees of its slots are paid into. Any
/// officer of the ward manages it; the keys are accepted here and never returned.
/// </summary>
[ApiController]
[Authorize]
[Route("api/ward/payout-account")]
public sealed class WardPayoutAccountController(ISender sender) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<PayoutAccountDto>> Get(CancellationToken cancellationToken) =>
        Ok(await sender.Send(new GetWardPayoutAccountQuery(), cancellationToken));

    [HttpPut]
    public async Task<ActionResult<PayoutAccountDto>> Connect(
        ConnectPayoutAccountRequest request, CancellationToken cancellationToken) =>
        Ok(await sender.Send(new ConnectWardPayoutAccountCommand(request), cancellationToken));

    [HttpPost("check")]
    public async Task<ActionResult<PayoutAccountDto>> Check(CancellationToken cancellationToken) =>
        Ok(await sender.Send(new CheckWardPayoutAccountCommand(), cancellationToken));

    [HttpDelete]
    public async Task<ActionResult<PayoutAccountDto>> Disconnect(CancellationToken cancellationToken) =>
        Ok(await sender.Send(new DisconnectWardPayoutAccountCommand(), cancellationToken));
}
