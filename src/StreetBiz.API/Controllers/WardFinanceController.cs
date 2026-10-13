using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using StreetBiz.Application.Features.WardFinance;

namespace StreetBiz.API.Controllers;

/// <summary>
/// The ward's manual money handling: correct or waive an instalment, waive a penalty, record a
/// payment taken in person, price one slot by hand, refund a deposit. Role and ward scoping happen
/// inside each handler via IWardActorContext, same as WardConfigurationController.
/// </summary>
[ApiController]
[Authorize]
[Route("api/ward/finance")]
public sealed class WardFinanceController(ISender sender) : ControllerBase
{
    [HttpGet("contracts")]
    public async Task<ActionResult<IReadOnlyList<WardFinanceContractListItemDto>>> ListContracts(CancellationToken ct) =>
        Ok(await sender.Send(new ListWardFinanceContractsQuery(), ct));

    [HttpGet("contracts/{contractId:long}")]
    public async Task<ActionResult<WardFinanceContractDetailDto>> GetContract(long contractId, CancellationToken ct) =>
        Ok(await sender.Send(new GetWardFinanceContractQuery(contractId), ct));

    [HttpPost("fee-items/{feeItemId:long}/adjust")]
    public async Task<ActionResult<WardFinanceActionResultDto>> AdjustFeeItem(
        long feeItemId, AdjustFeeItemRequest request, CancellationToken ct) =>
        Ok(await sender.Send(new AdjustFeeItemCommand(feeItemId, request), ct));

    [HttpPost("fee-items/{feeItemId:long}/waive")]
    public async Task<ActionResult<WardFinanceActionResultDto>> WaiveFeeItem(
        long feeItemId, ReasonRequest request, CancellationToken ct) =>
        Ok(await sender.Send(new WaiveFeeItemCommand(feeItemId, request.Reason), ct));

    [HttpPost("penalties/{penaltyId:long}/waive")]
    public async Task<ActionResult<WardFinanceActionResultDto>> WaivePenalty(
        long penaltyId, ReasonRequest request, CancellationToken ct) =>
        Ok(await sender.Send(new WaivePenaltyCommand(penaltyId, request.Reason), ct));

    [HttpPost("offline-payments")]
    public async Task<ActionResult<WardFinanceActionResultDto>> RecordOfflinePayment(
        RecordOfflinePaymentRequest request, CancellationToken ct) =>
        Ok(await sender.Send(new RecordOfflinePaymentCommand(request), ct));

    [HttpPut("slots/{slotId:long}/price")]
    public async Task<ActionResult<WardFinanceActionResultDto>> SetSlotPrice(
        long slotId, SetSlotPriceRequest request, CancellationToken ct) =>
        Ok(await sender.Send(new SetSlotPriceCommand(slotId, request), ct));

    [HttpPost("contracts/{contractId:long}/deposit-refund")]
    public async Task<ActionResult<WardFinanceActionResultDto>> RefundDeposit(
        long contractId, RefundDepositRequest request, CancellationToken ct) =>
        Ok(await sender.Send(new RefundDepositCommand(contractId, request), ct));
}
