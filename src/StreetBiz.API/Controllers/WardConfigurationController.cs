using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using StreetBiz.Application.Features.AiAssistance;
using StreetBiz.Application.Features.WardConfiguration;

namespace StreetBiz.API.Controllers;

/// <summary>
/// Ward Configuration (WARD-01 slot grid, WARD-02 zone pricing/hours, WARD-03 penalty schedule).
/// Role and ward scoping happen inside each handler via IWardActorContext, same as
/// WardComplianceController.
/// </summary>
[ApiController]
[Authorize]
[Route("api/ward")]
public sealed class WardConfigurationController(ISender sender) : ControllerBase
{
    #region WARD-03 Penalty schedule
    [HttpGet("penalty-schedules/overview")]
    public async Task<ActionResult<IReadOnlyList<WardPenaltyTypeDto>>> PenaltyOverview(CancellationToken ct) =>
        Ok(await sender.Send(new ListWardPenaltyOverviewQuery(), ct));

    [HttpGet("penalty-schedules/history")]
    public async Task<ActionResult<IReadOnlyList<PenaltyRateDto>>> PenaltyHistory([FromQuery] string violationType, CancellationToken ct) =>
        Ok(await sender.Send(new ListWardPenaltyHistoryQuery(violationType), ct));

    [HttpPost("penalty-schedules")]
    public async Task<ActionResult<WardPenaltyTypeDto>> SetPenaltyRate(SetPenaltyRateRequest request, CancellationToken ct) =>
        Ok(await sender.Send(new SetWardPenaltyRateCommand(request), ct));

    [HttpDelete("penalty-schedules/{id:int}")]
    public async Task<ActionResult<WardPenaltyTypeDto>> CancelPenaltyRate(int id, CancellationToken ct) =>
        Ok(await sender.Send(new CancelWardPenaltyRateCommand(id), ct));
    #endregion

    #region WARD-02 Pricing zones
    [HttpGet("pricing-zones")]
    public async Task<ActionResult<IReadOnlyList<WardZoneDto>>> ListZones(CancellationToken ct) =>
        Ok(await sender.Send(new ListWardZonesQuery(), ct));

    [HttpGet("pricing-zones/{id:int}")]
    public async Task<ActionResult<WardZoneDto>> GetZone(int id, CancellationToken ct) =>
        Ok(await sender.Send(new GetWardZoneQuery(id), ct));

    [HttpPost("pricing-zones")]
    public async Task<ActionResult<WardZoneDto>> CreateZone(UpsertZoneRequest request, CancellationToken ct) =>
        Ok(await sender.Send(new CreateWardZoneCommand(request), ct));

    [HttpPut("pricing-zones/{id:int}")]
    public async Task<ActionResult<WardZoneDto>> UpdateZone(int id, UpsertZoneRequest request, CancellationToken ct) =>
        Ok(await sender.Send(new UpdateWardZoneCommand(id, request), ct));

    [HttpDelete("pricing-zones/{id:int}")]
    public async Task<IActionResult> DeleteZone(int id, [FromQuery] string versionToken, CancellationToken ct)
    {
        await sender.Send(new DeleteWardZoneCommand(id, versionToken), ct);
        return NoContent();
    }

    [HttpPost("pricing-zones/{id:int}/impact-preview")]
    public async Task<ActionResult<ZoneImpactPreviewDto>> PreviewZoneImpact(int id, ZoneImpactPreviewRequest request, CancellationToken ct) =>
        Ok(await sender.Send(new PreviewWardZoneImpactQuery(id, request), ct));

    [HttpGet("pricing-zones/{id:int}/history")]
    public async Task<ActionResult<IReadOnlyList<ConfigHistoryEntryDto>>> ZoneHistory(int id, CancellationToken ct) =>
        Ok(await sender.Send(new ListWardZoneHistoryQuery(id), ct));

    /// <summary>AIC-07: a suggested price from 90 days of occupancy, within the configured zone's
    /// own +-20% band. The officer still saves (or not) through UpdateZone like any other change.</summary>
    [HttpGet("pricing-zones/{id:int}/price-suggestion")]
    [EnableRateLimiting("WardAi")]
    public async Task<ActionResult<ZonePriceSuggestionDto>> PriceSuggestion(int id, CancellationToken ct) =>
        Ok(await sender.Send(new GetZonePriceSuggestionQuery(id), ct));
    #endregion

    #region WARD-01 Slot grid & street features
    [HttpGet("slot-grid")]
    public async Task<ActionResult<WardSlotGridDto>> SlotGrid([FromQuery] int? zoneId, CancellationToken ct) =>
        Ok(await sender.Send(new GetWardSlotGridQuery(zoneId), ct));

    [HttpPost("slot-grid/check")]
    public async Task<ActionResult<PlacementCheckDto>> CheckPlacement(
        SlotPlacementInput input, [FromQuery] long? ignoreSlotId, CancellationToken ct) =>
        Ok(await sender.Send(new CheckWardSlotPlacementQuery(input, ignoreSlotId), ct));

    [HttpPost("slot-grid")]
    public async Task<ActionResult<SlotMutationResultDto>> CreateSlot(CreateSlotRequest request, CancellationToken ct) =>
        Ok(await sender.Send(new CreateWardSlotCommand(request), ct));

    [HttpPut("slot-grid/{id:long}")]
    public async Task<ActionResult<SlotMutationResultDto>> UpdateSlot(long id, UpdateSlotRequest request, CancellationToken ct) =>
        Ok(await sender.Send(new UpdateWardSlotCommand(id, request), ct));

    [HttpPut("slot-grid/{id:long}/status")]
    public async Task<ActionResult<WardSlotDto>> SetSlotStatus(long id, SetSlotStatusRequest request, CancellationToken ct) =>
        Ok(await sender.Send(new SetWardSlotStatusCommand(id, request), ct));

    [HttpDelete("slot-grid/{id:long}")]
    public async Task<IActionResult> DeleteSlot(long id, [FromQuery] string versionToken, CancellationToken ct)
    {
        await sender.Send(new DeleteWardSlotCommand(id, versionToken), ct);
        return NoContent();
    }

    [HttpPost("slot-grid/batch-preview")]
    public async Task<ActionResult<BatchPreviewDto>> PreviewBatch(BatchPreviewRequest request, CancellationToken ct) =>
        Ok(await sender.Send(new PreviewWardSlotBatchQuery(request), ct));

    [HttpPost("slot-grid/batch")]
    public async Task<ActionResult<IReadOnlyList<WardSlotDto>>> CreateBatch(BatchCreateRequest request, CancellationToken ct) =>
        Ok(await sender.Send(new CreateWardSlotBatchCommand(request), ct));

    [HttpPost("street-features")]
    public async Task<ActionResult<StreetFeatureMutationResultDto>> CreateFeature(UpsertStreetFeatureRequest request, CancellationToken ct) =>
        Ok(await sender.Send(new CreateWardStreetFeatureCommand(request), ct));

    [HttpPut("street-features/{id:int}")]
    public async Task<ActionResult<StreetFeatureMutationResultDto>> UpdateFeature(int id, UpsertStreetFeatureRequest request, CancellationToken ct) =>
        Ok(await sender.Send(new UpdateWardStreetFeatureCommand(id, request), ct));

    [HttpDelete("street-features/{id:int}")]
    public async Task<IActionResult> DeleteFeature(int id, [FromQuery] string versionToken, CancellationToken ct)
    {
        await sender.Send(new DeleteWardStreetFeatureCommand(id, versionToken), ct);
        return NoContent();
    }
    #endregion
}
