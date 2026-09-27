using MediatR;
using Moq;
using StreetBiz.Application.Features.WardConfiguration;
using StreetBiz.Application.Features.WardSlots;

namespace StreetBiz.Application.Tests;

public sealed class WardConfigurationUseCaseTests
{
    private static readonly WardActor Actor = new(1, 10, "Cán bộ");
    private readonly Mock<IWardActorContext> actorContext = new();
    private readonly Mock<IWardConfigurationService> service = new();

    public WardConfigurationUseCaseTests() =>
        actorContext.Setup(x => x.RequireAsync(It.IsAny<CancellationToken>())).ReturnsAsync(Actor);

    [Fact]
    public async Task Every_handler_resolves_the_ward_actor_and_passes_it_to_the_service()
    {
        var ctx = actorContext.Object;
        var svc = service.Object;
        var zoneRequest = ZoneRequest();
        var slot = new CreateSlotRequest(1, null, 16, 108, 2, 2, false, false, false, null, false, null);
        var update = new UpdateSlotRequest(1, "A-1", 16, 108, 2, 2, false, false, false, null, "t", false, null);
        var feature = new UpsertStreetFeatureRequest(1, "HYDRANT", "Trụ", 16, 108, true, null, "t");
        var batch = new BatchCreateRequest(1, [new(16, 108)], 2, 2, false, false, false, null, false, null);
        var preview = new BatchPreviewRequest(1, 16, 108, 16.001m, 108, 2, 2, 1);

        await new ListWardPenaltyOverviewQueryHandler(ctx, svc).Handle(new(), default);
        await new ListWardPenaltyHistoryQueryHandler(ctx, svc).Handle(new("X"), default);
        await new SetWardPenaltyRateCommandHandler(ctx, svc).Handle(new(Penalty(2_000_000, 3_000_000)), default);
        await new CancelWardPenaltyRateCommandHandler(ctx, svc).Handle(new(1), default);
        await new ListWardZonesQueryHandler(ctx, svc).Handle(new(), default);
        await new GetWardZoneQueryHandler(ctx, svc).Handle(new(1), default);
        await new CreateWardZoneCommandHandler(ctx, svc).Handle(new(zoneRequest), default);
        await new UpdateWardZoneCommandHandler(ctx, svc).Handle(new(1, zoneRequest), default);
        await new DeleteWardZoneCommandHandler(ctx, svc).Handle(new(1, "t"), default);
        await new PreviewWardZoneImpactQueryHandler(ctx, svc).Handle(new(1, new(1, null, null)), default);
        await new ListWardZoneHistoryQueryHandler(ctx, svc).Handle(new(1), default);
        await new GetWardSlotGridQueryHandler(ctx, svc).Handle(new(null), default);
        await new CheckWardSlotPlacementQueryHandler(ctx, svc).Handle(new(new(1, 16, 108, 2, 2), null), default);
        await new CreateWardSlotCommandHandler(ctx, svc).Handle(new(slot), default);
        await new UpdateWardSlotCommandHandler(ctx, svc).Handle(new(1, update), default);
        await new SetWardSlotStatusCommandHandler(ctx, svc).Handle(new(1, new("SUSPENDED", "x", "t")), default);
        await new DeleteWardSlotCommandHandler(ctx, svc).Handle(new(1, "t"), default);
        await new PreviewWardSlotBatchQueryHandler(ctx, svc).Handle(new(preview), default);
        await new CreateWardSlotBatchCommandHandler(ctx, svc).Handle(new(batch), default);
        await new CreateWardStreetFeatureCommandHandler(ctx, svc).Handle(new(feature), default);
        await new UpdateWardStreetFeatureCommandHandler(ctx, svc).Handle(new(1, feature), default);
        var unit = await new DeleteWardStreetFeatureCommandHandler(ctx, svc).Handle(new(1, "t"), default);

        Assert.Equal(Unit.Value, unit);
        actorContext.Verify(x => x.RequireAsync(It.IsAny<CancellationToken>()), Times.Exactly(22));
        service.Verify(x => x.SetPenaltyRateAsync(Actor, It.IsAny<SetPenaltyRateRequest>(), It.IsAny<CancellationToken>()));
        service.Verify(x => x.DeleteFeatureAsync(Actor, 1, "t", It.IsAny<CancellationToken>()));
        service.Verify(x => x.CreateBatchAsync(Actor, batch, It.IsAny<CancellationToken>()));
    }

    [Fact]
    public void Slot_validators_bound_coordinates_dimensions_category_and_require_a_reason_for_acknowledged_warnings()
    {
        var create = new CreateWardSlotCommandValidator();
        var ok = new CreateSlotRequest(1, null, 16.06m, 108.22m, 2, 2, false, false, false, "RETAIL", false, null);
        Assert.True(create.Validate(new CreateWardSlotCommand(ok)).IsValid);
        Assert.False(create.Validate(new CreateWardSlotCommand(ok with { Latitude = 91 })).IsValid);
        Assert.False(create.Validate(new CreateWardSlotCommand(ok with { WidthMeters = 0 })).IsValid);
        Assert.False(create.Validate(new CreateWardSlotCommand(ok with { LengthMeters = 1000 })).IsValid);
        Assert.False(create.Validate(new CreateWardSlotCommand(ok with { BusinessCategory = "CASINO" })).IsValid);
        Assert.False(create.Validate(new CreateWardSlotCommand(ok with { AcknowledgeWarnings = true })).IsValid);

        var update = new UpdateWardSlotCommandValidator();
        var edit = new UpdateSlotRequest(1, "A-1", 16, 108, 2, 2, false, false, false, null, "t", false, null);
        Assert.True(update.Validate(new UpdateWardSlotCommand(1, edit)).IsValid);
        Assert.False(update.Validate(new UpdateWardSlotCommand(1, edit with { VersionToken = "" })).IsValid);
        Assert.False(update.Validate(new UpdateWardSlotCommand(1, edit with { SlotCode = "" })).IsValid);

        var status = new SetWardSlotStatusCommandValidator();
        Assert.True(status.Validate(new SetWardSlotStatusCommand(1, new("SUSPENDED", "Thi công", "t"))).IsValid);
        Assert.False(status.Validate(new SetWardSlotStatusCommand(1, new("ACTIVE", "x", "t"))).IsValid);
        Assert.False(status.Validate(new SetWardSlotStatusCommand(1, new("AVAILABLE", "", "t"))).IsValid);

        var check = new CheckWardSlotPlacementQueryValidator();
        Assert.True(check.Validate(new CheckWardSlotPlacementQuery(new(1, 16, 108, 2, 2), null)).IsValid);
        Assert.False(check.Validate(new CheckWardSlotPlacementQuery(new(0, 16, 181, 2, 2), null)).IsValid);

        Assert.False(new DeleteWardSlotCommandValidator().Validate(new DeleteWardSlotCommand(1, "")).IsValid);
    }

    [Fact]
    public void Batch_validators_cap_the_batch_at_fifty_and_bound_the_gap()
    {
        var create = new CreateWardSlotBatchCommandValidator();
        var one = new BatchCreateRequest(1, [new(16, 108)], 2, 2, false, false, false, null, false, null);
        Assert.True(create.Validate(new CreateWardSlotBatchCommand(one)).IsValid);
        Assert.False(create.Validate(new CreateWardSlotBatchCommand(one with { Positions = [] })).IsValid);
        Assert.False(create.Validate(new CreateWardSlotBatchCommand(one with
        {
            Positions = Enumerable.Range(0, 51).Select(_ => new BatchSlotPosition(16, 108)).ToList(),
        })).IsValid);

        var preview = new PreviewWardSlotBatchQueryValidator();
        var p = new BatchPreviewRequest(1, 16, 108, 16.001m, 108, 2, 2, 1);
        Assert.True(preview.Validate(new PreviewWardSlotBatchQuery(p)).IsValid);
        Assert.False(preview.Validate(new PreviewWardSlotBatchQuery(p with { GapMeters = -1 })).IsValid);
    }

    [Fact]
    public void Street_feature_validators_only_accept_the_database_feature_types()
    {
        var create = new CreateWardStreetFeatureCommandValidator();
        var f = new UpsertStreetFeatureRequest(1, "HYDRANT", "Trụ nước", 16, 108, true, null, null);
        Assert.True(create.Validate(new CreateWardStreetFeatureCommand(f)).IsValid);
        Assert.False(create.Validate(new CreateWardStreetFeatureCommand(f with { FeatureType = "MANHOLE" })).IsValid);
        Assert.False(create.Validate(new CreateWardStreetFeatureCommand(f with { Label = "" })).IsValid);

        var update = new UpdateWardStreetFeatureCommandValidator();
        Assert.False(update.Validate(new UpdateWardStreetFeatureCommand(1, f)).IsValid);
        Assert.True(update.Validate(new UpdateWardStreetFeatureCommand(1, f with { VersionToken = "t" })).IsValid);
        Assert.False(new DeleteWardStreetFeatureCommandValidator().Validate(new DeleteWardStreetFeatureCommand(1, "")).IsValid);
    }

    [Fact]
    public void Zone_and_penalty_side_validators_reject_bad_input()
    {
        var zone = new CreateWardZoneCommandValidator();
        Assert.False(zone.Validate(new CreateWardZoneCommand(ZoneRequest() with { ZoneCode = "hc1 nvl" })).IsValid);
        Assert.False(zone.Validate(new CreateWardZoneCommand(ZoneRequest() with { PricePerDay = 0 })).IsValid);
        Assert.False(zone.Validate(new CreateWardZoneCommand(ZoneRequest() with
        {
            FeeComponents = [new ZoneFeeComponentInput("Phí", "PER_MONTH", 1)],
        })).IsValid);
        Assert.False(zone.Validate(new CreateWardZoneCommand(ZoneRequest() with { RegulationIssuer = new string('x', 80) + "x" })).IsValid);

        Assert.False(new DeleteWardZoneCommandValidator().Validate(new DeleteWardZoneCommand(1, "")).IsValid);
        Assert.False(new PreviewWardZoneImpactQueryValidator().Validate(new PreviewWardZoneImpactQuery(1, new(0, null, null))).IsValid);
        Assert.False(new CancelWardPenaltyRateCommandValidator().Validate(new CancelWardPenaltyRateCommand(0)).IsValid);
        Assert.False(new ListWardPenaltyHistoryQueryValidator().Validate(new ListWardPenaltyHistoryQuery("")).IsValid);

        var penalty = new SetWardPenaltyRateCommandValidator();
        Assert.False(penalty.Validate(new SetWardPenaltyRateCommand(Penalty(2_000_000, 3_000_000) with { Behavior = new string('x', 301) })).IsValid);
        Assert.False(penalty.Validate(new SetWardPenaltyRateCommand(Penalty(2_000_000, 3_000_000) with { Article = "" })).IsValid);
    }

    private static SetPenaltyRateRequest Penalty(long min, long max) =>
        new("UNAUTHORIZED_BUSINESS_USE", "Nghị định 168/2024/NĐ-CP", "12", "5", null, "kinh doanh trái phép", min, max,
            new DateOnly(2026, 10, 1), null);

    private static UpsertZoneRequest ZoneRequest() =>
        new("Đường Bạch Đằng", "HC1-BD", 40_000, null, null, "QĐ 15/QĐ-UBND", new DateOnly(2026, 9, 1),
            "UBND phường Hải Châu", null, null, null, [], "Lý do", "t");
}
