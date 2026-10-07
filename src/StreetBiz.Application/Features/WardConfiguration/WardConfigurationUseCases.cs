using FluentValidation;
using MediatR;
using StreetBiz.Application.Common.Security;
using StreetBiz.Application.Features.WardSlots;

namespace StreetBiz.Application.Features.WardConfiguration;

#region WARD-03 Penalty schedule
public sealed record ListWardPenaltyOverviewQuery : IRequest<IReadOnlyList<WardPenaltyTypeDto>>;

public sealed class ListWardPenaltyOverviewQueryHandler(IWardActorContext actorContext, IWardConfigurationService service)
    : IRequestHandler<ListWardPenaltyOverviewQuery, IReadOnlyList<WardPenaltyTypeDto>>
{
    public async Task<IReadOnlyList<WardPenaltyTypeDto>> Handle(ListWardPenaltyOverviewQuery request, CancellationToken ct) =>
        await service.ListPenaltyOverviewAsync(await actorContext.RequireAsync(ct), ct);
}

public sealed record ListWardPenaltyHistoryQuery(string ViolationType) : IRequest<IReadOnlyList<PenaltyRateDto>>;

public sealed class ListWardPenaltyHistoryQueryValidator : AbstractValidator<ListWardPenaltyHistoryQuery>
{
    public ListWardPenaltyHistoryQueryValidator() =>
        RuleFor(x => x.ViolationType).NotEmpty().MaximumLength(50);
}

public sealed class ListWardPenaltyHistoryQueryHandler(IWardActorContext actorContext, IWardConfigurationService service)
    : IRequestHandler<ListWardPenaltyHistoryQuery, IReadOnlyList<PenaltyRateDto>>
{
    public async Task<IReadOnlyList<PenaltyRateDto>> Handle(ListWardPenaltyHistoryQuery request, CancellationToken ct) =>
        await service.ListPenaltyHistoryAsync(await actorContext.RequireAsync(ct), request.ViolationType, ct);
}

public sealed record SetWardPenaltyRateCommand(SetPenaltyRateRequest Request) : IRequest<WardPenaltyTypeDto>;

public sealed class SetWardPenaltyRateCommandValidator : AbstractValidator<SetWardPenaltyRateCommand>
{
    // Luật XLVPHC Điều 23 khoản 1: 50.000đ - 1.000.000.000đ đối với cá nhân.
    private const long LegalMinimum = 50_000;
    private const long LegalMaximum = 1_000_000_000;

    public SetWardPenaltyRateCommandValidator()
    {
        RuleFor(x => x.Request.ViolationType).NotEmpty().MaximumLength(50);
        RuleFor(x => x.Request.DocumentRef).NotEmpty().WithMessage("Nhập số hiệu văn bản làm căn cứ.").MaximumLength(150);
        RuleFor(x => x.Request.Article).NotEmpty().WithMessage("Nhập Điều.").MaximumLength(10);
        RuleFor(x => x.Request.Clause).NotEmpty().WithMessage("Nhập Khoản.").MaximumLength(10);
        RuleFor(x => x.Request.Point).MaximumLength(10);
        RuleFor(x => x.Request.Behavior).NotEmpty().WithMessage("Nhập mô tả hành vi theo văn bản.").MaximumLength(300);
        RuleFor(x => x.Request.BracketMin)
            .InclusiveBetween(LegalMinimum, LegalMaximum)
            .WithMessage("Mức tối thiểu phải từ 50.000đ đến 1.000.000.000đ (Luật XLVPHC Điều 23 khoản 1).");
        RuleFor(x => x.Request.BracketMax)
            .InclusiveBetween(LegalMinimum, LegalMaximum)
            .WithMessage("Mức tối đa phải từ 50.000đ đến 1.000.000.000đ (Luật XLVPHC Điều 23 khoản 1).")
            .GreaterThanOrEqualTo(x => x.Request.BracketMin)
            .WithMessage("Mức tối đa không được nhỏ hơn mức tối thiểu.");
        RuleFor(x => LegalBasisText.Format(
                x.Request.DocumentRef ?? "", x.Request.Article ?? "", x.Request.Clause ?? "", x.Request.Point,
                x.Request.Behavior ?? "", x.Request.BracketMin, x.Request.BracketMax).Length)
            .LessThanOrEqualTo(LegalBasisText.MaxLength)
            .WithName("LegalBasis")
            .WithMessage("Căn cứ pháp lý sau khi ghép vượt quá 500 ký tự, hãy rút gọn mô tả hành vi.");
    }
}

public sealed class SetWardPenaltyRateCommandHandler(IWardActorContext actorContext, IWardConfigurationService service)
    : IRequestHandler<SetWardPenaltyRateCommand, WardPenaltyTypeDto>
{
    public async Task<WardPenaltyTypeDto> Handle(SetWardPenaltyRateCommand request, CancellationToken ct) =>
        await service.SetPenaltyRateAsync(await actorContext.RequireAsync(ct), request.Request, ct);
}

public sealed record CancelWardPenaltyRateCommand(int ScheduleId) : IRequest<WardPenaltyTypeDto>;

public sealed class CancelWardPenaltyRateCommandValidator : AbstractValidator<CancelWardPenaltyRateCommand>
{
    public CancelWardPenaltyRateCommandValidator() => RuleFor(x => x.ScheduleId).GreaterThan(0);
}

public sealed class CancelWardPenaltyRateCommandHandler(IWardActorContext actorContext, IWardConfigurationService service)
    : IRequestHandler<CancelWardPenaltyRateCommand, WardPenaltyTypeDto>
{
    public async Task<WardPenaltyTypeDto> Handle(CancelWardPenaltyRateCommand request, CancellationToken ct) =>
        await service.CancelScheduledPenaltyRateAsync(await actorContext.RequireAsync(ct), request.ScheduleId, ct);
}
#endregion

#region WARD-02 Pricing zones
public sealed record ListWardZonesQuery : IRequest<IReadOnlyList<WardZoneDto>>;

public sealed class ListWardZonesQueryHandler(IWardActorContext actorContext, IWardConfigurationService service)
    : IRequestHandler<ListWardZonesQuery, IReadOnlyList<WardZoneDto>>
{
    public async Task<IReadOnlyList<WardZoneDto>> Handle(ListWardZonesQuery request, CancellationToken ct) =>
        await service.ListZonesAsync(await actorContext.RequireAsync(ct), ct);
}

public sealed record GetWardZoneQuery(int ZoneId) : IRequest<WardZoneDto>;

public sealed class GetWardZoneQueryHandler(IWardActorContext actorContext, IWardConfigurationService service)
    : IRequestHandler<GetWardZoneQuery, WardZoneDto>
{
    public async Task<WardZoneDto> Handle(GetWardZoneQuery request, CancellationToken ct) =>
        await service.GetZoneAsync(await actorContext.RequireAsync(ct), request.ZoneId, ct);
}

public sealed record CreateWardZoneCommand(UpsertZoneRequest Request) : IRequest<WardZoneDto>;

public sealed record UpdateWardZoneCommand(int ZoneId, UpsertZoneRequest Request) : IRequest<WardZoneDto>;

internal static class ZoneRules
{
    public static void Apply<T>(AbstractValidator<T> v, Func<T, UpsertZoneRequest> get)
    {
        v.RuleFor(x => get(x).ZoneName).NotEmpty().WithMessage("Nhập tên khu vực.").MaximumLength(150);
        v.RuleFor(x => get(x).ZoneCode).NotEmpty().WithMessage("Nhập mã khu vực.").MaximumLength(20)
            .Matches("^[A-Z0-9-]+$").WithMessage("Mã khu vực chỉ gồm chữ in hoa, số và dấu gạch ngang.");
        v.RuleFor(x => get(x).PriceDisplayUnit).Must(u => u is PriceDisplayUnits.Day or PriceDisplayUnits.Month)
            .WithMessage("Đơn vị giá phải là DAY hoặc MONTH.");
        v.RuleFor(x => get(x).RentalMode).Must(m => m is RentalModes.Standard or RentalModes.Event)
            .WithMessage("Hình thức thuê phải là STANDARD hoặc EVENT.");
        // price_per_day stays the only value the fee engine reads -- required when the officer
        // is entering it directly; when entering a monthly price, that one is required instead
        // and price_per_day is derived server-side (WardConfigurationService.ApplyZoneFields).
        v.RuleFor(x => get(x).PricePerDay).GreaterThan(0).WithMessage("Giá thuê/ngày phải lớn hơn 0.")
            .When(x => get(x).PriceDisplayUnit == PriceDisplayUnits.Day);
        v.RuleFor(x => get(x).PricePerMonth).NotNull().GreaterThan(0).WithMessage("Giá thuê/tháng phải lớn hơn 0.")
            .When(x => get(x).PriceDisplayUnit == PriceDisplayUnits.Month);
        // An event's day-by-day pricing has no monthly equivalent worth entering.
        v.RuleFor(x => get(x).PriceDisplayUnit).Equal(PriceDisplayUnits.Day)
            .WithMessage("Khu vực sự kiện chỉ tính giá theo ngày.")
            .When(x => get(x).RentalMode == RentalModes.Event);
        v.RuleFor(x => get(x))
            .Must(r => r.EventStartDate is not null && r.EventEndDate is not null && r.EventEndDate > r.EventStartDate)
            .WithName("EventStartDate")
            .WithMessage("Khu vực sự kiện cần nhập đủ ngày bắt đầu và ngày kết thúc, kết thúc phải sau bắt đầu.")
            .When(x => get(x).RentalMode == RentalModes.Event);
        v.RuleFor(x => get(x))
            .Must(r => (r.AvailableFrom is null) == (r.AvailableTo is null))
            .WithName("AvailableFrom")
            .WithMessage("Giờ bắt đầu và giờ kết thúc phải cùng được nhập hoặc cùng để trống.")
            .Must(r => r.AvailableFrom is null || r.AvailableFrom != r.AvailableTo)
            .WithName("AvailableTo")
            .WithMessage("Giờ bắt đầu và giờ kết thúc không được trùng nhau.");
        v.RuleFor(x => get(x).RegulationNumber).MaximumLength(50);
        v.RuleFor(x => get(x).RegulationIssuer).MaximumLength(80);
        v.RuleFor(x => get(x))
            .Must(r => (r.RegulationNumber is null && r.RegulationIssuedOn is null && r.RegulationIssuer is null)
                       || (!string.IsNullOrWhiteSpace(r.RegulationNumber) && r.RegulationIssuedOn is not null
                           && !string.IsNullOrWhiteSpace(r.RegulationIssuer)))
            .WithName("RegulationNumber")
            .WithMessage("Nhập đủ số hiệu, ngày ban hành và cơ quan ban hành của văn bản cho phép.");
        v.RuleFor(x => get(x))
            .Must(r => r.RegulationNumber is null || r.RegulationIssuedOn is null || r.RegulationIssuer is null
                       || ZoneRegulationText.Compose(r.RegulationNumber, r.RegulationIssuedOn!.Value, r.RegulationIssuer!).Length <= ZoneRegulationText.MaxLength)
            .WithName("RegulationNumber")
            .WithMessage("Căn cứ văn bản sau khi ghép vượt quá 120 ký tự.");
        v.RuleFor(x => get(x).SegmentFrom).MaximumLength(150);
        v.RuleFor(x => get(x).SegmentTo).MaximumLength(150);
        v.RuleFor(x => get(x).ChangeReason).MaximumLength(500);
        v.RuleFor(x => get(x).FeeComponents).NotNull().Must(c => c.Count <= 20).WithMessage("Tối đa 20 khoản phụ phí.");
        v.RuleForEach(x => get(x).FeeComponents).ChildRules(c =>
        {
            c.RuleFor(x => x.ComponentName).NotEmpty().MaximumLength(150);
            c.RuleFor(x => x.CalcBasis).Must(b => b is FeeBases.PerDay or FeeBases.PerTerm)
                .WithMessage("Cách tính phụ phí phải là PER_DAY hoặc PER_TERM.");
            c.RuleFor(x => x.UnitAmount).GreaterThanOrEqualTo(0);
        });
    }
}

public sealed class CreateWardZoneCommandValidator : AbstractValidator<CreateWardZoneCommand>
{
    public CreateWardZoneCommandValidator()
    {
        ZoneRules.Apply(this, x => x.Request);
        RuleFor(x => x.Request.RegulationNumber).NotEmpty()
            .WithMessage("Khu vực chỉ được mở khi có văn bản cho phép: nhập số hiệu văn bản.");
    }
}

public sealed class UpdateWardZoneCommandValidator : AbstractValidator<UpdateWardZoneCommand>
{
    public UpdateWardZoneCommandValidator()
    {
        RuleFor(x => x.ZoneId).GreaterThan(0);
        ZoneRules.Apply(this, x => x.Request);
        RuleFor(x => x.Request.VersionToken).NotEmpty().WithMessage("Thiếu phiên bản dữ liệu, vui lòng tải lại.");
        RuleFor(x => x.Request.ChangeReason).NotEmpty().WithMessage("Nhập lý do thay đổi.");
    }
}

public sealed class CreateWardZoneCommandHandler(IWardActorContext actorContext, IWardConfigurationService service)
    : IRequestHandler<CreateWardZoneCommand, WardZoneDto>
{
    public async Task<WardZoneDto> Handle(CreateWardZoneCommand request, CancellationToken ct) =>
        await service.CreateZoneAsync(await actorContext.RequireAsync(ct), request.Request, ct);
}

public sealed class UpdateWardZoneCommandHandler(IWardActorContext actorContext, IWardConfigurationService service)
    : IRequestHandler<UpdateWardZoneCommand, WardZoneDto>
{
    public async Task<WardZoneDto> Handle(UpdateWardZoneCommand request, CancellationToken ct) =>
        await service.UpdateZoneAsync(await actorContext.RequireAsync(ct), request.ZoneId, request.Request, ct);
}

public sealed record DeleteWardZoneCommand(int ZoneId, string VersionToken) : IRequest<Unit>;

public sealed class DeleteWardZoneCommandValidator : AbstractValidator<DeleteWardZoneCommand>
{
    public DeleteWardZoneCommandValidator()
    {
        RuleFor(x => x.ZoneId).GreaterThan(0);
        RuleFor(x => x.VersionToken).NotEmpty();
    }
}

public sealed class DeleteWardZoneCommandHandler(IWardActorContext actorContext, IWardConfigurationService service)
    : IRequestHandler<DeleteWardZoneCommand, Unit>
{
    public async Task<Unit> Handle(DeleteWardZoneCommand request, CancellationToken ct)
    {
        await service.DeleteZoneAsync(await actorContext.RequireAsync(ct), request.ZoneId, request.VersionToken, ct);
        return Unit.Value;
    }
}

public sealed record PreviewWardZoneImpactQuery(int ZoneId, ZoneImpactPreviewRequest Request) : IRequest<ZoneImpactPreviewDto>;

public sealed class PreviewWardZoneImpactQueryValidator : AbstractValidator<PreviewWardZoneImpactQuery>
{
    public PreviewWardZoneImpactQueryValidator()
    {
        RuleFor(x => x.ZoneId).GreaterThan(0);
        RuleFor(x => x.Request.PricePerDay).GreaterThan(0);
        RuleFor(x => x.Request.FeeComponents).NotNull().Must(c => c.Count <= 20).WithMessage("Tối đa 20 khoản phụ phí.");
        RuleForEach(x => x.Request.FeeComponents).ChildRules(c =>
        {
            c.RuleFor(x => x.ComponentName).NotEmpty().MaximumLength(150);
            c.RuleFor(x => x.CalcBasis).Must(b => b is FeeBases.PerDay or FeeBases.PerTerm)
                .WithMessage("Cách tính phụ phí phải là PER_DAY hoặc PER_TERM.");
            c.RuleFor(x => x.UnitAmount).GreaterThanOrEqualTo(0);
        });
    }
}

public sealed class PreviewWardZoneImpactQueryHandler(IWardActorContext actorContext, IWardConfigurationService service)
    : IRequestHandler<PreviewWardZoneImpactQuery, ZoneImpactPreviewDto>
{
    public async Task<ZoneImpactPreviewDto> Handle(PreviewWardZoneImpactQuery request, CancellationToken ct) =>
        await service.PreviewZoneImpactAsync(await actorContext.RequireAsync(ct), request.ZoneId, request.Request, ct);
}

public sealed record ListWardZoneHistoryQuery(int ZoneId) : IRequest<IReadOnlyList<ConfigHistoryEntryDto>>;

public sealed class ListWardZoneHistoryQueryHandler(IWardActorContext actorContext, IWardConfigurationService service)
    : IRequestHandler<ListWardZoneHistoryQuery, IReadOnlyList<ConfigHistoryEntryDto>>
{
    public async Task<IReadOnlyList<ConfigHistoryEntryDto>> Handle(ListWardZoneHistoryQuery request, CancellationToken ct) =>
        await service.ListZoneHistoryAsync(await actorContext.RequireAsync(ct), request.ZoneId, ct);
}
#endregion

#region WARD-01 Slot grid & street features
internal static class SlotRules
{
    public const decimal MaxDimension = 999.99m;

    public static readonly string[] Categories =
    [
        BusinessCategories.FoodBeverage, BusinessCategories.Retail, BusinessCategories.Services,
        BusinessCategories.Crafts, BusinessCategories.General,
    ];

    public static IRuleBuilderOptions<T, decimal> Latitude<T>(this IRuleBuilder<T, decimal> rule) =>
        rule.InclusiveBetween(-90m, 90m).WithMessage("Vĩ độ không hợp lệ.");

    public static IRuleBuilderOptions<T, decimal> Longitude<T>(this IRuleBuilder<T, decimal> rule) =>
        rule.InclusiveBetween(-180m, 180m).WithMessage("Kinh độ không hợp lệ.");

    public static IRuleBuilderOptions<T, decimal> Dimension<T>(this IRuleBuilder<T, decimal> rule) =>
        rule.GreaterThan(0m).LessThanOrEqualTo(MaxDimension).WithMessage("Kích thước ô phải lớn hơn 0 và không quá 999,99 m.");

    public static IRuleBuilderOptions<T, string?> Category<T>(this IRuleBuilder<T, string?> rule) =>
        rule.Must(c => c is null || Categories.Contains(c)).WithMessage("Ngành hàng không hợp lệ.");
}

public sealed record GetWardSlotGridQuery(int? ZoneId) : IRequest<WardSlotGridDto>;

public sealed class GetWardSlotGridQueryHandler(IWardActorContext actorContext, IWardConfigurationService service)
    : IRequestHandler<GetWardSlotGridQuery, WardSlotGridDto>
{
    public async Task<WardSlotGridDto> Handle(GetWardSlotGridQuery request, CancellationToken ct) =>
        await service.GetSlotGridAsync(await actorContext.RequireAsync(ct), request.ZoneId, ct);
}

public sealed record CheckWardSlotPlacementQuery(SlotPlacementInput Input, long? IgnoreSlotId) : IRequest<PlacementCheckDto>;

public sealed class CheckWardSlotPlacementQueryValidator : AbstractValidator<CheckWardSlotPlacementQuery>
{
    public CheckWardSlotPlacementQueryValidator()
    {
        RuleFor(x => x.Input.ZoneId).GreaterThan(0);
        RuleFor(x => x.Input.Latitude).Latitude();
        RuleFor(x => x.Input.Longitude).Longitude();
        RuleFor(x => x.Input.WidthMeters).Dimension();
        RuleFor(x => x.Input.LengthMeters).Dimension();
    }
}

public sealed class CheckWardSlotPlacementQueryHandler(IWardActorContext actorContext, IWardConfigurationService service)
    : IRequestHandler<CheckWardSlotPlacementQuery, PlacementCheckDto>
{
    public async Task<PlacementCheckDto> Handle(CheckWardSlotPlacementQuery request, CancellationToken ct) =>
        await service.CheckPlacementAsync(await actorContext.RequireAsync(ct), request.Input, request.IgnoreSlotId, ct);
}

public sealed record CreateWardSlotCommand(CreateSlotRequest Request) : IRequest<SlotMutationResultDto>;

public sealed class CreateWardSlotCommandValidator : AbstractValidator<CreateWardSlotCommand>
{
    public CreateWardSlotCommandValidator()
    {
        RuleFor(x => x.Request.ZoneId).GreaterThan(0);
        RuleFor(x => x.Request.SlotCode).MaximumLength(30);
        RuleFor(x => x.Request.Latitude).Latitude();
        RuleFor(x => x.Request.Longitude).Longitude();
        RuleFor(x => x.Request.WidthMeters).Dimension();
        RuleFor(x => x.Request.LengthMeters).Dimension();
        RuleFor(x => x.Request.BusinessCategory).Category();
        RuleFor(x => x.Request.WarningReason).MaximumLength(500);
        RuleFor(x => x.Request.WarningReason).NotEmpty().When(x => x.Request.AcknowledgeWarnings)
            .WithMessage("Nhập lý do khi xác nhận bỏ qua cảnh báo.");
    }
}

public sealed class CreateWardSlotCommandHandler(IWardActorContext actorContext, IWardConfigurationService service)
    : IRequestHandler<CreateWardSlotCommand, SlotMutationResultDto>
{
    public async Task<SlotMutationResultDto> Handle(CreateWardSlotCommand request, CancellationToken ct) =>
        await service.CreateSlotAsync(await actorContext.RequireAsync(ct), request.Request, ct);
}

public sealed record UpdateWardSlotCommand(long SlotId, UpdateSlotRequest Request) : IRequest<SlotMutationResultDto>;

public sealed class UpdateWardSlotCommandValidator : AbstractValidator<UpdateWardSlotCommand>
{
    public UpdateWardSlotCommandValidator()
    {
        RuleFor(x => x.SlotId).GreaterThan(0);
        RuleFor(x => x.Request.ZoneId).GreaterThan(0);
        RuleFor(x => x.Request.SlotCode).NotEmpty().MaximumLength(30);
        RuleFor(x => x.Request.Latitude).Latitude();
        RuleFor(x => x.Request.Longitude).Longitude();
        RuleFor(x => x.Request.WidthMeters).Dimension();
        RuleFor(x => x.Request.LengthMeters).Dimension();
        RuleFor(x => x.Request.BusinessCategory).Category();
        RuleFor(x => x.Request.VersionToken).NotEmpty();
        RuleFor(x => x.Request.WarningReason).MaximumLength(500);
        RuleFor(x => x.Request.WarningReason).NotEmpty().When(x => x.Request.AcknowledgeWarnings)
            .WithMessage("Nhập lý do khi xác nhận bỏ qua cảnh báo.");
    }
}

public sealed class UpdateWardSlotCommandHandler(IWardActorContext actorContext, IWardConfigurationService service)
    : IRequestHandler<UpdateWardSlotCommand, SlotMutationResultDto>
{
    public async Task<SlotMutationResultDto> Handle(UpdateWardSlotCommand request, CancellationToken ct) =>
        await service.UpdateSlotAsync(await actorContext.RequireAsync(ct), request.SlotId, request.Request, ct);
}

public sealed record SetWardSlotStatusCommand(long SlotId, SetSlotStatusRequest Request) : IRequest<WardSlotDto>;

public sealed class SetWardSlotStatusCommandValidator : AbstractValidator<SetWardSlotStatusCommand>
{
    public SetWardSlotStatusCommandValidator()
    {
        RuleFor(x => x.SlotId).GreaterThan(0);
        RuleFor(x => x.Request.Status).Must(s => s is SlotStatuses.Available or SlotStatuses.Suspended)
            .WithMessage("Chỉ được chuyển ô sang AVAILABLE hoặc SUSPENDED.");
        RuleFor(x => x.Request.Reason).NotEmpty().WithMessage("Nhập lý do.").MaximumLength(500);
        RuleFor(x => x.Request.VersionToken).NotEmpty();
    }
}

public sealed class SetWardSlotStatusCommandHandler(IWardActorContext actorContext, IWardConfigurationService service)
    : IRequestHandler<SetWardSlotStatusCommand, WardSlotDto>
{
    public async Task<WardSlotDto> Handle(SetWardSlotStatusCommand request, CancellationToken ct) =>
        await service.SetSlotStatusAsync(await actorContext.RequireAsync(ct), request.SlotId, request.Request, ct);
}

public sealed record DeleteWardSlotCommand(long SlotId, string VersionToken) : IRequest<Unit>;

public sealed class DeleteWardSlotCommandValidator : AbstractValidator<DeleteWardSlotCommand>
{
    public DeleteWardSlotCommandValidator()
    {
        RuleFor(x => x.SlotId).GreaterThan(0);
        RuleFor(x => x.VersionToken).NotEmpty();
    }
}

public sealed class DeleteWardSlotCommandHandler(IWardActorContext actorContext, IWardConfigurationService service)
    : IRequestHandler<DeleteWardSlotCommand, Unit>
{
    public async Task<Unit> Handle(DeleteWardSlotCommand request, CancellationToken ct)
    {
        await service.DeleteSlotAsync(await actorContext.RequireAsync(ct), request.SlotId, request.VersionToken, ct);
        return Unit.Value;
    }
}

public sealed record PreviewWardSlotBatchQuery(BatchPreviewRequest Request) : IRequest<BatchPreviewDto>;

public sealed class PreviewWardSlotBatchQueryValidator : AbstractValidator<PreviewWardSlotBatchQuery>
{
    public PreviewWardSlotBatchQueryValidator()
    {
        RuleFor(x => x.Request.ZoneId).GreaterThan(0);
        RuleFor(x => x.Request.StartLatitude).Latitude();
        RuleFor(x => x.Request.StartLongitude).Longitude();
        RuleFor(x => x.Request.EndLatitude).Latitude();
        RuleFor(x => x.Request.EndLongitude).Longitude();
        RuleFor(x => x.Request.WidthMeters).Dimension();
        RuleFor(x => x.Request.LengthMeters).Dimension();
        RuleFor(x => x.Request.GapMeters).InclusiveBetween(0m, 100m).WithMessage("Khoảng cách giữa các ô từ 0 đến 100 m.");
    }
}

public sealed class PreviewWardSlotBatchQueryHandler(IWardActorContext actorContext, IWardConfigurationService service)
    : IRequestHandler<PreviewWardSlotBatchQuery, BatchPreviewDto>
{
    public async Task<BatchPreviewDto> Handle(PreviewWardSlotBatchQuery request, CancellationToken ct) =>
        await service.PreviewBatchAsync(await actorContext.RequireAsync(ct), request.Request, ct);
}

public sealed record CreateWardSlotBatchCommand(BatchCreateRequest Request) : IRequest<IReadOnlyList<WardSlotDto>>;

public sealed class CreateWardSlotBatchCommandValidator : AbstractValidator<CreateWardSlotBatchCommand>
{
    public const int MaxBatchSize = 50;

    public CreateWardSlotBatchCommandValidator()
    {
        RuleFor(x => x.Request.ZoneId).GreaterThan(0);
        RuleFor(x => x.Request.Positions).NotEmpty()
            .Must(p => p.Count <= MaxBatchSize).WithMessage("Tối đa 50 ô mỗi lần rải.");
        RuleForEach(x => x.Request.Positions).ChildRules(p =>
        {
            p.RuleFor(x => x.Latitude).Latitude();
            p.RuleFor(x => x.Longitude).Longitude();
        });
        RuleFor(x => x.Request.WidthMeters).Dimension();
        RuleFor(x => x.Request.LengthMeters).Dimension();
        RuleFor(x => x.Request.BusinessCategory).Category();
        RuleFor(x => x.Request.WarningReason).MaximumLength(500);
        RuleFor(x => x.Request.WarningReason).NotEmpty().When(x => x.Request.AcknowledgeWarnings)
            .WithMessage("Nhập lý do khi xác nhận bỏ qua cảnh báo.");
    }
}

public sealed class CreateWardSlotBatchCommandHandler(IWardActorContext actorContext, IWardConfigurationService service)
    : IRequestHandler<CreateWardSlotBatchCommand, IReadOnlyList<WardSlotDto>>
{
    public async Task<IReadOnlyList<WardSlotDto>> Handle(CreateWardSlotBatchCommand request, CancellationToken ct) =>
        await service.CreateBatchAsync(await actorContext.RequireAsync(ct), request.Request, ct);
}

public sealed record ListWardSlotHistoryQuery(long SlotId) : IRequest<IReadOnlyList<ConfigHistoryEntryDto>>;

public sealed class ListWardSlotHistoryQueryHandler(IWardActorContext actorContext, IWardConfigurationService service)
    : IRequestHandler<ListWardSlotHistoryQuery, IReadOnlyList<ConfigHistoryEntryDto>>
{
    public async Task<IReadOnlyList<ConfigHistoryEntryDto>> Handle(ListWardSlotHistoryQuery request, CancellationToken ct) =>
        await service.ListSlotHistoryAsync(await actorContext.RequireAsync(ct), request.SlotId, ct);
}

internal static class FeatureRules
{
    public static void Apply<T>(AbstractValidator<T> v, Func<T, UpsertStreetFeatureRequest> get)
    {
        v.RuleFor(x => get(x).ZoneId).GreaterThan(0);
        v.RuleFor(x => get(x).FeatureType).Must(t => StreetFeatureTypes.All.Contains(t))
            .WithMessage("Loại chướng ngại vật không hợp lệ.");
        v.RuleFor(x => get(x).Label).NotEmpty().WithMessage("Nhập tên chướng ngại vật.").MaximumLength(150);
        v.RuleFor(x => get(x).Note).MaximumLength(200);
        v.RuleFor(x => get(x).Latitude).Latitude();
        v.RuleFor(x => get(x).Longitude).Longitude();
    }
}

public sealed record CreateWardStreetFeatureCommand(UpsertStreetFeatureRequest Request) : IRequest<StreetFeatureMutationResultDto>;

public sealed class CreateWardStreetFeatureCommandValidator : AbstractValidator<CreateWardStreetFeatureCommand>
{
    public CreateWardStreetFeatureCommandValidator() => FeatureRules.Apply(this, x => x.Request);
}

public sealed class CreateWardStreetFeatureCommandHandler(IWardActorContext actorContext, IWardConfigurationService service)
    : IRequestHandler<CreateWardStreetFeatureCommand, StreetFeatureMutationResultDto>
{
    public async Task<StreetFeatureMutationResultDto> Handle(CreateWardStreetFeatureCommand request, CancellationToken ct) =>
        await service.CreateFeatureAsync(await actorContext.RequireAsync(ct), request.Request, ct);
}

public sealed record UpdateWardStreetFeatureCommand(int FeatureId, UpsertStreetFeatureRequest Request) : IRequest<StreetFeatureMutationResultDto>;

public sealed class UpdateWardStreetFeatureCommandValidator : AbstractValidator<UpdateWardStreetFeatureCommand>
{
    public UpdateWardStreetFeatureCommandValidator()
    {
        RuleFor(x => x.FeatureId).GreaterThan(0);
        FeatureRules.Apply(this, x => x.Request);
        RuleFor(x => x.Request.VersionToken).NotEmpty();
    }
}

public sealed class UpdateWardStreetFeatureCommandHandler(IWardActorContext actorContext, IWardConfigurationService service)
    : IRequestHandler<UpdateWardStreetFeatureCommand, StreetFeatureMutationResultDto>
{
    public async Task<StreetFeatureMutationResultDto> Handle(UpdateWardStreetFeatureCommand request, CancellationToken ct) =>
        await service.UpdateFeatureAsync(await actorContext.RequireAsync(ct), request.FeatureId, request.Request, ct);
}

public sealed record DeleteWardStreetFeatureCommand(int FeatureId, string VersionToken) : IRequest<Unit>;

public sealed class DeleteWardStreetFeatureCommandValidator : AbstractValidator<DeleteWardStreetFeatureCommand>
{
    public DeleteWardStreetFeatureCommandValidator()
    {
        RuleFor(x => x.FeatureId).GreaterThan(0);
        RuleFor(x => x.VersionToken).NotEmpty();
    }
}

public sealed class DeleteWardStreetFeatureCommandHandler(IWardActorContext actorContext, IWardConfigurationService service)
    : IRequestHandler<DeleteWardStreetFeatureCommand, Unit>
{
    public async Task<Unit> Handle(DeleteWardStreetFeatureCommand request, CancellationToken ct)
    {
        await service.DeleteFeatureAsync(await actorContext.RequireAsync(ct), request.FeatureId, request.VersionToken, ct);
        return Unit.Value;
    }
}
#endregion

#region WardCompliancePolicy (Phase A)
public sealed record GetWardCompliancePolicyQuery : IRequest<WardCompliancePolicyDto>;

public sealed class GetWardCompliancePolicyQueryHandler(IWardActorContext actorContext, IWardConfigurationService service)
    : IRequestHandler<GetWardCompliancePolicyQuery, WardCompliancePolicyDto>
{
    public async Task<WardCompliancePolicyDto> Handle(GetWardCompliancePolicyQuery request, CancellationToken ct) =>
        await service.GetCompliancePolicyAsync(await actorContext.RequireAsync(ct), ct);
}

public sealed record UpsertWardCompliancePolicyCommand(UpsertWardCompliancePolicyRequest Request) : IRequest<WardCompliancePolicyDto>;

public sealed class UpsertWardCompliancePolicyCommandValidator : AbstractValidator<UpsertWardCompliancePolicyCommand>
{
    public UpsertWardCompliancePolicyCommandValidator()
    {
        RuleFor(x => x.Request.ViolationThresholdCount).GreaterThan(0)
            .When(x => x.Request.ViolationThresholdCount is not null)
            .WithMessage("Ngưỡng số lần vi phạm phải lớn hơn 0.");
        RuleFor(x => x.Request.ViolationWindowDays).GreaterThan(0)
            .When(x => x.Request.ViolationWindowDays is not null)
            .WithMessage("Cửa sổ thời gian phải lớn hơn 0 ngày.");
        RuleFor(x => x.Request.UnpaidPenaltyGraceDays).GreaterThan(0)
            .When(x => x.Request.UnpaidPenaltyGraceDays is not null)
            .WithMessage("Số ngày ân hạn phải lớn hơn 0.");
        RuleFor(x => x.Request)
            .Must(r => r.ViolationThresholdCount is null || r.ViolationWindowDays is not null)
            .WithName("ViolationWindowDays")
            .WithMessage("Đã nhập ngưỡng số lần vi phạm thì phải nhập cả cửa sổ thời gian.");
    }
}

public sealed class UpsertWardCompliancePolicyCommandHandler(IWardActorContext actorContext, IWardConfigurationService service)
    : IRequestHandler<UpsertWardCompliancePolicyCommand, WardCompliancePolicyDto>
{
    public async Task<WardCompliancePolicyDto> Handle(UpsertWardCompliancePolicyCommand request, CancellationToken ct) =>
        await service.UpsertCompliancePolicyAsync(await actorContext.RequireAsync(ct), request.Request, ct);
}
#endregion
