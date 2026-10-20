using FluentValidation;
using MediatR;
using StreetBiz.Application.Features.WardSlots;

namespace StreetBiz.Application.Features.WardFinance;

// Everything a ward officer may correct by hand once the automatic pricing has run. Each write
// needs a reason, is attributed to the officer in the audit log and tells the vendor what changed.

#region DTOs
public sealed record WardFinanceContractListItemDto(
    long ContractId,
    string SlotCode,
    string ZoneName,
    string VendorName,
    string? VendorPhone,
    string ContractStatus,
    DateOnly StartDate,
    DateOnly EndDate,
    decimal Outstanding,
    int OverdueCount,
    decimal UnpaidPenalties,
    decimal DepositPaid,
    bool DepositRefunded);

public sealed record WardFeeItemDto(
    long FeeItemId,
    int Ordinal,
    DateOnly DueDate,
    decimal Amount,
    decimal DepositAmount,
    string Status,
    DateTime? PaidAt,
    decimal? OriginalAmount,
    DateTime? AdjustedAt,
    string? AdjustmentReason,
    bool CanEdit);

public sealed record WardPenaltyItemDto(
    long PenaltyId,
    long ViolationId,
    string ViolationType,
    decimal Amount,
    string Status,
    string? DecisionNumber,
    DateTime CreatedAt,
    DateTime? PaidAt,
    string? WaiverReason);

public sealed record WardDepositRefundDto(
    decimal DepositPaid,
    decimal Amount,
    string? DeductionReason,
    string Method,
    string? Reference,
    DateTime RefundedAt);

public sealed record WardFinanceContractDetailDto(
    long ContractId,
    long SlotId,
    string SlotCode,
    string ZoneName,
    string RentalMode,
    string VendorName,
    string? VendorPhone,
    string ContractStatus,
    DateOnly StartDate,
    DateOnly EndDate,
    decimal ScheduleTotal,
    decimal Outstanding,
    IReadOnlyList<WardFeeItemDto> Items,
    IReadOnlyList<WardPenaltyItemDto> Penalties,
    decimal DepositPaid,
    WardDepositRefundDto? DepositRefund,
    bool CanRefundDeposit,
    string? RefundBlockedReason,
    /// <summary>The slot's own manual price (monthly for STANDARD, daily for EVENT); null = the zone's.</summary>
    decimal? SlotPriceOverride,
    decimal ZonePrice);

public sealed record WardFinanceActionResultDto(string Message);
#endregion

#region Requests
/// <summary>Change a not-yet-paid instalment's amount and/or due date. Omitted = unchanged.</summary>
public sealed record AdjustFeeItemRequest(decimal? NewAmount, DateOnly? NewDueDate, string Reason);

public sealed record ReasonRequest(string Reason);

/// <summary><c>Target</c> is "FEE" (a fee instalment) or "PENALTY".</summary>
public sealed record RecordOfflinePaymentRequest(
    string Target,
    long TargetId,
    string Method,
    string? Reference,
    string? Note);

/// <summary>PriceOverride null puts the slot back on its zone's price.</summary>
public sealed record SetSlotPriceRequest(decimal? PriceOverride, string Reason);

public sealed record RefundDepositRequest(
    decimal Amount,
    string Method,
    string? Reference,
    string? DeductionReason);

public static class OfflinePaymentMethods
{
    public const string Cash = "CASH";
    public const string BankTransfer = "BANK_TRANSFER";

    public static bool IsValid(string? value) => value is Cash or BankTransfer;
}
#endregion

public interface IWardFinanceService
{
    Task<IReadOnlyList<WardFinanceContractListItemDto>> ListContractsAsync(WardActor actor, CancellationToken ct);
    Task<WardFinanceContractDetailDto> GetContractAsync(WardActor actor, long contractId, CancellationToken ct);
    Task<WardFinanceActionResultDto> AdjustFeeItemAsync(WardActor actor, long feeItemId, AdjustFeeItemRequest request, CancellationToken ct);
    Task<WardFinanceActionResultDto> WaiveFeeItemAsync(WardActor actor, long feeItemId, string reason, CancellationToken ct);
    Task<WardFinanceActionResultDto> WaivePenaltyAsync(WardActor actor, long penaltyId, string reason, CancellationToken ct);
    Task<WardFinanceActionResultDto> RecordOfflinePaymentAsync(WardActor actor, RecordOfflinePaymentRequest request, CancellationToken ct);
    Task<WardFinanceActionResultDto> SetSlotPriceAsync(WardActor actor, long slotId, SetSlotPriceRequest request, CancellationToken ct);
    Task<WardFinanceActionResultDto> RefundDepositAsync(WardActor actor, long contractId, RefundDepositRequest request, CancellationToken ct);
}

#region Use cases
public sealed record ListWardFinanceContractsQuery : IRequest<IReadOnlyList<WardFinanceContractListItemDto>>;

public sealed class ListWardFinanceContractsQueryHandler(IWardActorContext actor, IWardFinanceService service)
    : IRequestHandler<ListWardFinanceContractsQuery, IReadOnlyList<WardFinanceContractListItemDto>>
{
    public async Task<IReadOnlyList<WardFinanceContractListItemDto>> Handle(ListWardFinanceContractsQuery request, CancellationToken ct) =>
        await service.ListContractsAsync(await actor.RequireAsync(ct), ct);
}

public sealed record GetWardFinanceContractQuery(long ContractId) : IRequest<WardFinanceContractDetailDto>;

public sealed class GetWardFinanceContractQueryHandler(IWardActorContext actor, IWardFinanceService service)
    : IRequestHandler<GetWardFinanceContractQuery, WardFinanceContractDetailDto>
{
    public async Task<WardFinanceContractDetailDto> Handle(GetWardFinanceContractQuery request, CancellationToken ct) =>
        await service.GetContractAsync(await actor.RequireAsync(ct), request.ContractId, ct);
}

public sealed record AdjustFeeItemCommand(long FeeItemId, AdjustFeeItemRequest Request) : IRequest<WardFinanceActionResultDto>;

public sealed class AdjustFeeItemCommandValidator : AbstractValidator<AdjustFeeItemCommand>
{
    public AdjustFeeItemCommandValidator()
    {
        RuleFor(x => x.Request.Reason).NotEmpty().WithMessage("Nhập lý do điều chỉnh.").MaximumLength(500);
        RuleFor(x => x.Request.NewAmount).GreaterThan(0)
            .WithMessage("Số tiền phải lớn hơn 0. Muốn không thu kỳ này, hãy dùng thao tác miễn.")
            .LessThanOrEqualTo(1_000_000_000m)
            .When(x => x.Request.NewAmount is not null);
        RuleFor(x => x.Request)
            .Must(r => r.NewAmount is not null || r.NewDueDate is not null)
            .WithName("NewAmount")
            .WithMessage("Nhập số tiền mới hoặc hạn nộp mới.");
    }
}

public sealed class AdjustFeeItemCommandHandler(IWardActorContext actor, IWardFinanceService service)
    : IRequestHandler<AdjustFeeItemCommand, WardFinanceActionResultDto>
{
    public async Task<WardFinanceActionResultDto> Handle(AdjustFeeItemCommand request, CancellationToken ct) =>
        await service.AdjustFeeItemAsync(await actor.RequireAsync(ct), request.FeeItemId, request.Request, ct);
}

public sealed record WaiveFeeItemCommand(long FeeItemId, string Reason) : IRequest<WardFinanceActionResultDto>;

public sealed class WaiveFeeItemCommandValidator : AbstractValidator<WaiveFeeItemCommand>
{
    public WaiveFeeItemCommandValidator() =>
        RuleFor(x => x.Reason).NotEmpty().WithMessage("Nhập lý do miễn.").MaximumLength(500);
}

public sealed class WaiveFeeItemCommandHandler(IWardActorContext actor, IWardFinanceService service)
    : IRequestHandler<WaiveFeeItemCommand, WardFinanceActionResultDto>
{
    public async Task<WardFinanceActionResultDto> Handle(WaiveFeeItemCommand request, CancellationToken ct) =>
        await service.WaiveFeeItemAsync(await actor.RequireAsync(ct), request.FeeItemId, request.Reason, ct);
}

public sealed record WaivePenaltyCommand(long PenaltyId, string Reason) : IRequest<WardFinanceActionResultDto>;

public sealed class WaivePenaltyCommandValidator : AbstractValidator<WaivePenaltyCommand>
{
    public WaivePenaltyCommandValidator() =>
        RuleFor(x => x.Reason).NotEmpty().WithMessage("Nhập lý do miễn/huỷ khoản phạt.").MaximumLength(500);
}

public sealed class WaivePenaltyCommandHandler(IWardActorContext actor, IWardFinanceService service)
    : IRequestHandler<WaivePenaltyCommand, WardFinanceActionResultDto>
{
    public async Task<WardFinanceActionResultDto> Handle(WaivePenaltyCommand request, CancellationToken ct) =>
        await service.WaivePenaltyAsync(await actor.RequireAsync(ct), request.PenaltyId, request.Reason, ct);
}

public sealed record RecordOfflinePaymentCommand(RecordOfflinePaymentRequest Request) : IRequest<WardFinanceActionResultDto>;

public sealed class RecordOfflinePaymentCommandValidator : AbstractValidator<RecordOfflinePaymentCommand>
{
    public RecordOfflinePaymentCommandValidator()
    {
        RuleFor(x => x.Request.Target).Must(t => t is "FEE" or "PENALTY")
            .WithMessage("Đối tượng thu phải là FEE hoặc PENALTY.");
        RuleFor(x => x.Request.TargetId).GreaterThan(0);
        RuleFor(x => x.Request.Method).Must(OfflinePaymentMethods.IsValid)
            .WithMessage("Hình thức thu phải là CASH hoặc BANK_TRANSFER.");
        RuleFor(x => x.Request.Reference).NotEmpty()
            .WithMessage("Nhập số biên lai hoặc mã giao dịch ngân hàng.").MaximumLength(100);
        RuleFor(x => x.Request.Note).MaximumLength(500);
    }
}

public sealed class RecordOfflinePaymentCommandHandler(IWardActorContext actor, IWardFinanceService service)
    : IRequestHandler<RecordOfflinePaymentCommand, WardFinanceActionResultDto>
{
    public async Task<WardFinanceActionResultDto> Handle(RecordOfflinePaymentCommand request, CancellationToken ct) =>
        await service.RecordOfflinePaymentAsync(await actor.RequireAsync(ct), request.Request, ct);
}

public sealed record SetSlotPriceCommand(long SlotId, SetSlotPriceRequest Request) : IRequest<WardFinanceActionResultDto>;

public sealed class SetSlotPriceCommandValidator : AbstractValidator<SetSlotPriceCommand>
{
    public SetSlotPriceCommandValidator()
    {
        RuleFor(x => x.Request.Reason).NotEmpty().WithMessage("Nhập lý do đặt giá riêng cho ô.").MaximumLength(500);
        RuleFor(x => x.Request.PriceOverride).GreaterThan(0).LessThanOrEqualTo(1_000_000_000m)
            .When(x => x.Request.PriceOverride is not null);
    }
}

public sealed class SetSlotPriceCommandHandler(IWardActorContext actor, IWardFinanceService service)
    : IRequestHandler<SetSlotPriceCommand, WardFinanceActionResultDto>
{
    public async Task<WardFinanceActionResultDto> Handle(SetSlotPriceCommand request, CancellationToken ct) =>
        await service.SetSlotPriceAsync(await actor.RequireAsync(ct), request.SlotId, request.Request, ct);
}

public sealed record RefundDepositCommand(long ContractId, RefundDepositRequest Request) : IRequest<WardFinanceActionResultDto>;

public sealed class RefundDepositCommandValidator : AbstractValidator<RefundDepositCommand>
{
    public RefundDepositCommandValidator()
    {
        RuleFor(x => x.Request.Amount).GreaterThanOrEqualTo(0);
        RuleFor(x => x.Request.Method).Must(OfflinePaymentMethods.IsValid)
            .WithMessage("Hình thức hoàn phải là CASH hoặc BANK_TRANSFER.");
        RuleFor(x => x.Request.Reference).MaximumLength(100);
        RuleFor(x => x.Request.DeductionReason).MaximumLength(500);
    }
}

public sealed class RefundDepositCommandHandler(IWardActorContext actor, IWardFinanceService service)
    : IRequestHandler<RefundDepositCommand, WardFinanceActionResultDto>
{
    public async Task<WardFinanceActionResultDto> Handle(RefundDepositCommand request, CancellationToken ct) =>
        await service.RefundDepositAsync(await actor.RequireAsync(ct), request.ContractId, request.Request, ct);
}
#endregion
