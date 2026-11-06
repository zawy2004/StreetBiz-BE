using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using StreetBiz.Application.Features.PenaltyReceipts;
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

    /// <summary>Treasury receipts households sent for fines of this ward, waiting ones first.</summary>
    [HttpGet("penalty-receipts")]
    public async Task<ActionResult<IReadOnlyList<WardPenaltyReceiptDto>>> ListPenaltyReceipts(
        [FromQuery] string? status, CancellationToken ct) =>
        Ok(await sender.Send(new ListPenaltyReceiptsQuery(status), ct));

    /// <summary>The officer has checked the receipt: the fine is recorded as paid to the Treasury, with its invoice.</summary>
    [HttpPost("penalty-receipts/{receiptId:long}/accept")]
    public async Task<ActionResult<WardFinanceActionResultDto>> AcceptPenaltyReceipt(
        long receiptId, AcceptPenaltyReceiptRequest request, CancellationToken ct) =>
        Ok(await sender.Send(new AcceptPenaltyReceiptCommand(receiptId, request.Note), ct));

    [HttpPost("penalty-receipts/{receiptId:long}/reject")]
    public async Task<ActionResult<WardFinanceActionResultDto>> RejectPenaltyReceipt(
        long receiptId, ReasonRequest request, CancellationToken ct) =>
        Ok(await sender.Send(new RejectPenaltyReceiptCommand(receiptId, request.Reason), ct));

    /// <summary>The sanction decision of a paid fine was annulled: the payment becomes owed back (the Treasury refunds it).</summary>
    [HttpPost("penalties/{penaltyId:long}/annul")]
    public async Task<ActionResult<WardFinanceActionResultDto>> AnnulPaidPenalty(
        long penaltyId, ReasonRequest request, CancellationToken ct) =>
        Ok(await sender.Send(new AnnulPaidPenaltyCommand(penaltyId, request.Reason), ct));

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
