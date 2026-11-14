using FluentValidation;
using MediatR;
using StreetBiz.Application.Common.Exceptions;
using StreetBiz.Application.Common.Interfaces;
using StreetBiz.Application.Common.Models;
using StreetBiz.Application.Common.Security;
using StreetBiz.Application.Features.WardFinance;
using StreetBiz.Application.Features.WardSlots;

namespace StreetBiz.Application.Features.PenaltyReceipts;

/// <summary>PenaltyPaymentReceipts.status values.</summary>
public static class PenaltyReceiptStatuses
{
    public const string Submitted = "SUBMITTED";
    public const string Accepted = "ACCEPTED";
    public const string Rejected = "REJECTED";

    public static bool IsValid(string? value) => value is Submitted or Accepted or Rejected;
}

public static class PenaltyReceiptMessages
{
    public const string PenaltyNotFound = "Không tìm thấy khoản phạt này.";
    public const string NotPayable = "Khoản phạt này không còn ở trạng thái chưa nộp.";
    public const string AlreadyWaiting = "Biên lai trước đó vẫn đang chờ phường kiểm tra.";
    public const string ReceiptNotFound = "Không tìm thấy biên lai này.";
    public const string ReceiptNotWaiting = "Biên lai này đã được xử lý.";
    public const string ReceiptNotYours = "Tệp biên lai không hợp lệ hoặc không phải của bạn.";
    public const string PaidOnRange = "Ngày nộp phải từ ngày lập khoản phạt đến hôm nay.";
    public const string NotPaid = "Chỉ ghi nhận huỷ quyết định cho khoản phạt đã được nộp.";
}

/// <summary>
/// How a fine is paid. The law has it paid into the State Treasury (the account named in the
/// decision, or online through the national public-service portal); StreetBiz neither collects nor
/// passes on the money, it only keeps the household's receipt and the ward's check of it.
/// </summary>
public static class TreasuryPaymentGuide
{
    public static IReadOnlyList<string> Instructions(string? decisionNumber) =>
    [
        decisionNumber is { Length: > 0 }
            ? $"Nộp tiền phạt vào tài khoản Kho bạc Nhà nước ghi trong quyết định xử phạt số {decisionNumber}, hoặc nộp trực tuyến qua Cổng Dịch vụ công quốc gia nếu cơ quan xử phạt đã mở."
            : "Nộp tiền phạt vào tài khoản Kho bạc Nhà nước ghi trong quyết định xử phạt, hoặc nộp trực tuyến qua Cổng Dịch vụ công quốc gia nếu cơ quan xử phạt đã mở.",
        "Chỉ nộp vào đúng tài khoản ghi trong quyết định. Không chuyển tiền phạt cho cá nhân hay tài khoản nào khác, kể cả cán bộ phường.",
        "Nộp đủ một lần trong thời hạn ghi trong quyết định; nộp chậm bị tính thêm tiền chậm nộp theo quy định.",
        "Nộp xong, chụp hoặc tải biên lai lên đây. Phường kiểm tra rồi ghi nhận là đã nộp. StreetBiz không thu hộ và không chuyển tiền phạt.",
    ];
}

public sealed record PenaltyReceiptRow(
    long ReceiptId,
    long PenaltyId,
    string Status,
    string ReceiptUrl,
    string? Reference,
    DateOnly PaidOn,
    string? Note,
    DateTime SubmittedAt,
    DateTime? ReviewedAt,
    string? ReviewNote);

/// <summary>A penalty as its household pays it: with the ward that decided it and its latest receipt.</summary>
public sealed record PenaltyTreasuryRow(
    long PenaltyId,
    decimal Amount,
    string? DecisionNumber,
    string ViolationLabel,
    string? SlotCode,
    string? WardName,
    string PenaltyStatus,
    DateTime IssuedAt,
    PenaltyReceiptRow? LatestReceipt);

public sealed record WardPenaltyReceiptRow(
    PenaltyReceiptRow Receipt,
    string? VendorName,
    decimal Amount,
    string? DecisionNumber,
    string ViolationLabel,
    string? SlotCode,
    string PenaltyStatus,
    string? ReviewedByName);

/// <summary>What the ward checks before it decides on a receipt.</summary>
public sealed record PenaltyReceiptReview(
    long ReceiptId,
    long PenaltyId,
    string Status,
    string? Reference,
    string PenaltyStatus,
    decimal Amount,
    long? VendorUserId);

public sealed record SubmitPenaltyReceipt(string ReceiptUrl, string? Reference, DateOnly PaidOn, string? Note);

public interface IPenaltyReceiptRepository
{
    /// <summary>The caller's own penalty with its receipt history; null when it is not theirs.</summary>
    Task<PenaltyTreasuryRow?> GetTreasuryAsync(long vendorId, long penaltyId, CancellationToken cancellationToken);

    /// <summary>
    /// Keeps a household's receipt for one of its UNPAID penalties and tells the ward's officers.
    /// A second receipt while one still waits is a conflict.
    /// </summary>
    Task<PenaltyReceiptRow> SubmitAsync(
        long vendorId, long userId, long penaltyId, SubmitPenaltyReceipt receipt, DateTime now, CancellationToken cancellationToken);

    Task<IReadOnlyList<WardPenaltyReceiptRow>> ListForWardAsync(
        int wardId, string? status, CancellationToken cancellationToken);

    Task<PenaltyReceiptReview?> GetForReviewAsync(int wardId, long receiptId, CancellationToken cancellationToken);

    /// <summary>Whether this evidence file is the receipt of a fine of the ward, so its officers may open it.</summary>
    Task<bool> ReceiptBelongsToWardAsync(string receiptUrl, int wardId, CancellationToken cancellationToken);

    /// <summary>Records the officer's decision on a waiting receipt and tells the household.</summary>
    Task<PenaltyReceiptRow> DecideAsync(
        int wardId, long officerUserId, long receiptId, bool accepted, string? note, DateTime now, CancellationToken cancellationToken);

    /// <summary>
    /// The sanction decision was annulled after the fine was paid: the penalty is CANCELLED and its
    /// payment becomes REFUND_REQUIRED (the Treasury, not StreetBiz, returns the money). The household is told.
    /// </summary>
    Task<string> AnnulPaidPenaltyAsync(
        int wardId, long officerUserId, long penaltyId, string reason, DateTime now, CancellationToken cancellationToken);
}

#region DTOs

public sealed record PenaltyReceiptDto(
    long ReceiptId,
    long PenaltyId,
    string Status,
    string ReceiptUrl,
    string? Reference,
    DateOnly PaidOn,
    string? Note,
    DateTime SubmittedAt,
    DateTime? ReviewedAt,
    string? ReviewNote)
{
    public static PenaltyReceiptDto From(PenaltyReceiptRow row) => new(
        row.ReceiptId, row.PenaltyId, row.Status, row.ReceiptUrl, row.Reference, row.PaidOn, row.Note,
        row.SubmittedAt, row.ReviewedAt, row.ReviewNote);
}

public sealed record PenaltyTreasuryDto(
    long PenaltyId,
    decimal Amount,
    string? DecisionNumber,
    string ViolationLabel,
    string? SlotCode,
    string? WardName,
    string PenaltyStatus,
    DateTime IssuedAt,
    bool CanSubmit,
    IReadOnlyList<string> Instructions,
    PenaltyReceiptDto? LatestReceipt);

public sealed record WardPenaltyReceiptDto(
    PenaltyReceiptDto Receipt,
    string? VendorName,
    decimal Amount,
    string? DecisionNumber,
    string ViolationLabel,
    string? SlotCode,
    string PenaltyStatus,
    string? ReviewedByName);

public sealed record AcceptPenaltyReceiptRequest(string? Note);

public sealed record SubmitPenaltyReceiptRequest(string ReceiptUrl, string? Reference, DateOnly PaidOn, string? Note);

#endregion

#region Household

public sealed record GetPenaltyTreasuryQuery(long PenaltyId) : IRequest<PenaltyTreasuryDto>;

public sealed class GetPenaltyTreasuryQueryValidator : AbstractValidator<GetPenaltyTreasuryQuery>
{
    public GetPenaltyTreasuryQueryValidator() => RuleFor(x => x.PenaltyId).GreaterThan(0);
}

public sealed class GetPenaltyTreasuryQueryHandler(
    IVendorContext vendorContext,
    IPenaltyReceiptRepository receipts) : IRequestHandler<GetPenaltyTreasuryQuery, PenaltyTreasuryDto>
{
    public async Task<PenaltyTreasuryDto> Handle(GetPenaltyTreasuryQuery request, CancellationToken cancellationToken)
    {
        var vendorId = await vendorContext.RequireVendorIdAsync(cancellationToken);
        var row = await receipts.GetTreasuryAsync(vendorId, request.PenaltyId, cancellationToken)
            ?? throw new NotFoundException(PenaltyReceiptMessages.PenaltyNotFound);
        return new PenaltyTreasuryDto(
            row.PenaltyId, row.Amount, row.DecisionNumber, row.ViolationLabel, row.SlotCode, row.WardName,
            row.PenaltyStatus, row.IssuedAt,
            // Only an unpaid fine without a receipt already waiting can take a receipt.
            PenaltyStatuses.IsPayable(row.PenaltyStatus) && row.LatestReceipt?.Status != PenaltyReceiptStatuses.Submitted,
            TreasuryPaymentGuide.Instructions(row.DecisionNumber),
            row.LatestReceipt is null ? null : PenaltyReceiptDto.From(row.LatestReceipt));
    }
}

public sealed record SubmitPenaltyReceiptCommand(long PenaltyId, SubmitPenaltyReceiptRequest Request)
    : IRequest<PenaltyReceiptDto>;

public sealed class SubmitPenaltyReceiptCommandValidator : AbstractValidator<SubmitPenaltyReceiptCommand>
{
    public SubmitPenaltyReceiptCommandValidator()
    {
        RuleFor(x => x.PenaltyId).GreaterThan(0);
        RuleFor(x => x.Request.ReceiptUrl).NotEmpty().WithMessage("Tải biên lai nộp phạt lên.").MaximumLength(500);
        RuleFor(x => x.Request.Reference).MaximumLength(100);
        RuleFor(x => x.Request.Note).MaximumLength(500);
    }
}

public sealed class SubmitPenaltyReceiptCommandHandler(
    IVendorContext vendorContext,
    ICurrentUser currentUser,
    IPenaltyReceiptRepository receipts,
    TimeProvider clock) : IRequestHandler<SubmitPenaltyReceiptCommand, PenaltyReceiptDto>
{
    public async Task<PenaltyReceiptDto> Handle(SubmitPenaltyReceiptCommand request, CancellationToken cancellationToken)
    {
        var vendorId = await vendorContext.RequireVendorIdAsync(cancellationToken);
        var userId = currentUser.UserId ?? throw new AuthenticationException("Phiên đăng nhập không hợp lệ.");
        var data = request.Request;

        // The receipt is a file this household uploaded itself, never someone else's.
        if (!EvidenceFiles.TryParseUrl(data.ReceiptUrl, out var ownerUserId, out _) || ownerUserId != userId)
        {
            throw new DomainRuleException(PenaltyReceiptMessages.ReceiptNotYours);
        }

        var penalty = await receipts.GetTreasuryAsync(vendorId, request.PenaltyId, cancellationToken)
            ?? throw new NotFoundException(PenaltyReceiptMessages.PenaltyNotFound);
        if (!PenaltyStatuses.IsPayable(penalty.PenaltyStatus))
        {
            throw new DomainRuleException(PenaltyReceiptMessages.NotPayable);
        }

        var today = BusinessCalendar.Today(clock);
        if (data.PaidOn > today || data.PaidOn < BusinessCalendar.DateOf(penalty.IssuedAt))
        {
            throw new DomainRuleException(PenaltyReceiptMessages.PaidOnRange);
        }

        var row = await receipts.SubmitAsync(
            vendorId,
            userId,
            request.PenaltyId,
            new SubmitPenaltyReceipt(data.ReceiptUrl, data.Reference?.Trim(), data.PaidOn, data.Note?.Trim()),
            clock.GetUtcNow().UtcDateTime,
            cancellationToken);
        return PenaltyReceiptDto.From(row);
    }
}

#endregion

#region Ward

public sealed record ListPenaltyReceiptsQuery(string? Status) : IRequest<IReadOnlyList<WardPenaltyReceiptDto>>;

public sealed class ListPenaltyReceiptsQueryValidator : AbstractValidator<ListPenaltyReceiptsQuery>
{
    public ListPenaltyReceiptsQueryValidator() =>
        RuleFor(x => x.Status)
            .Must(PenaltyReceiptStatuses.IsValid)
            .When(x => !string.IsNullOrWhiteSpace(x.Status))
            .WithMessage("Trạng thái phải là SUBMITTED, ACCEPTED hoặc REJECTED.");
}

public sealed class ListPenaltyReceiptsQueryHandler(
    IWardActorContext actorContext,
    IPenaltyReceiptRepository receipts) : IRequestHandler<ListPenaltyReceiptsQuery, IReadOnlyList<WardPenaltyReceiptDto>>
{
    public async Task<IReadOnlyList<WardPenaltyReceiptDto>> Handle(
        ListPenaltyReceiptsQuery request, CancellationToken cancellationToken)
    {
        var actor = await actorContext.RequireAsync(cancellationToken);
        var rows = await receipts.ListForWardAsync(
            actor.WardId, string.IsNullOrWhiteSpace(request.Status) ? null : request.Status.Trim().ToUpperInvariant(), cancellationToken);
        return [.. rows.Select(row => new WardPenaltyReceiptDto(
            PenaltyReceiptDto.From(row.Receipt), row.VendorName, row.Amount, row.DecisionNumber, row.ViolationLabel,
            row.SlotCode, row.PenaltyStatus, row.ReviewedByName))];
    }
}

/// <summary>
/// The officer has checked the Treasury receipt: the fine is recorded as paid (a TREASURY payment,
/// with its invoice) through the same path as a payment taken in person.
/// </summary>
public sealed record AcceptPenaltyReceiptCommand(long ReceiptId, string? Note) : IRequest<WardFinanceActionResultDto>;

public sealed class AcceptPenaltyReceiptCommandValidator : AbstractValidator<AcceptPenaltyReceiptCommand>
{
    public AcceptPenaltyReceiptCommandValidator()
    {
        RuleFor(x => x.ReceiptId).GreaterThan(0);
        RuleFor(x => x.Note).MaximumLength(500);
    }
}

public sealed class AcceptPenaltyReceiptCommandHandler(
    IWardActorContext actorContext,
    IPenaltyReceiptRepository receipts,
    IFinanceRepository finance,
    TimeProvider clock) : IRequestHandler<AcceptPenaltyReceiptCommand, WardFinanceActionResultDto>
{
    public async Task<WardFinanceActionResultDto> Handle(AcceptPenaltyReceiptCommand request, CancellationToken cancellationToken)
    {
        var actor = await actorContext.RequireAsync(cancellationToken);
        var review = await receipts.GetForReviewAsync(actor.WardId, request.ReceiptId, cancellationToken)
            ?? throw new NotFoundException(PenaltyReceiptMessages.ReceiptNotFound);
        if (review.Status != PenaltyReceiptStatuses.Submitted)
        {
            throw new DomainRuleException(PenaltyReceiptMessages.ReceiptNotWaiting);
        }

        // Recorded first; if marking the receipt then fails, accepting again finds the fine already
        // paid and only finishes the receipt, so the fine is never recorded twice.
        if (PenaltyStatuses.IsPayable(review.PenaltyStatus))
        {
            await finance.RecordOfflinePaymentAsync(
                actor.WardId,
                actor.UserId,
                null,
                review.PenaltyId,
                OfflinePaymentMethods.Treasury,
                review.Reference,
                request.Note?.Trim(),
                cancellationToken);
        }
        else if (review.PenaltyStatus != PenaltyStatuses.Paid)
        {
            throw new DomainRuleException(PenaltyReceiptMessages.NotPayable);
        }

        await receipts.DecideAsync(
            actor.WardId, actor.UserId, request.ReceiptId, true, request.Note?.Trim(), clock.GetUtcNow().UtcDateTime, cancellationToken);
        return new WardFinanceActionResultDto("Đã ghi nhận khoản phạt đã nộp vào Kho bạc và phát hành hoá đơn.");
    }
}

public sealed record RejectPenaltyReceiptCommand(long ReceiptId, string Reason) : IRequest<WardFinanceActionResultDto>;

public sealed class RejectPenaltyReceiptCommandValidator : AbstractValidator<RejectPenaltyReceiptCommand>
{
    public RejectPenaltyReceiptCommandValidator()
    {
        RuleFor(x => x.ReceiptId).GreaterThan(0);
        RuleFor(x => x.Reason).NotEmpty().WithMessage("Nhập lý do từ chối biên lai.").MaximumLength(500);
    }
}

public sealed class RejectPenaltyReceiptCommandHandler(
    IWardActorContext actorContext,
    IPenaltyReceiptRepository receipts,
    TimeProvider clock) : IRequestHandler<RejectPenaltyReceiptCommand, WardFinanceActionResultDto>
{
    public async Task<WardFinanceActionResultDto> Handle(RejectPenaltyReceiptCommand request, CancellationToken cancellationToken)
    {
        var actor = await actorContext.RequireAsync(cancellationToken);
        var review = await receipts.GetForReviewAsync(actor.WardId, request.ReceiptId, cancellationToken)
            ?? throw new NotFoundException(PenaltyReceiptMessages.ReceiptNotFound);
        if (review.Status != PenaltyReceiptStatuses.Submitted)
        {
            throw new DomainRuleException(PenaltyReceiptMessages.ReceiptNotWaiting);
        }

        await receipts.DecideAsync(
            actor.WardId, actor.UserId, request.ReceiptId, false, request.Reason.Trim(), clock.GetUtcNow().UtcDateTime, cancellationToken);
        return new WardFinanceActionResultDto("Đã từ chối biên lai và báo cho hộ kinh doanh.");
    }
}

/// <summary>
/// The sanction decision was annulled or amended after the fine was paid. The money is the State
/// Treasury's to return, so the payment is only marked as owed back; the officer then records when
/// the Treasury has refunded it, as for any other refund.
/// </summary>
public sealed record AnnulPaidPenaltyCommand(long PenaltyId, string Reason) : IRequest<WardFinanceActionResultDto>;

public sealed class AnnulPaidPenaltyCommandValidator : AbstractValidator<AnnulPaidPenaltyCommand>
{
    public AnnulPaidPenaltyCommandValidator()
    {
        RuleFor(x => x.PenaltyId).GreaterThan(0);
        RuleFor(x => x.Reason).NotEmpty().WithMessage("Nhập lý do huỷ quyết định xử phạt.").MaximumLength(500);
    }
}

public sealed class AnnulPaidPenaltyCommandHandler(
    IWardActorContext actorContext,
    IPenaltyReceiptRepository receipts,
    TimeProvider clock) : IRequestHandler<AnnulPaidPenaltyCommand, WardFinanceActionResultDto>
{
    public async Task<WardFinanceActionResultDto> Handle(AnnulPaidPenaltyCommand request, CancellationToken cancellationToken)
    {
        var actor = await actorContext.RequireAsync(cancellationToken);
        var message = await receipts.AnnulPaidPenaltyAsync(
            actor.WardId, actor.UserId, request.PenaltyId, request.Reason.Trim(), clock.GetUtcNow().UtcDateTime, cancellationToken);
        return new WardFinanceActionResultDto(message);
    }
}

#endregion
