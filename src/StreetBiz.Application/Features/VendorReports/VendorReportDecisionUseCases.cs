using FluentValidation;
using MediatR;
using StreetBiz.Application.Common.Security;
using StreetBiz.Application.Features.AiMarketplace;
using StreetBiz.Application.Features.WardSlots;

namespace StreetBiz.Application.Features.VendorReports;

/// <summary>
/// WARD-19 body. <paramref name="Outcome"/> is DISMISSED ("bác", the note is the reason the reporter
/// reads) or MEDIATED ("đã hòa giải", optionally with the amount the vendor refunded, at most what was
/// charged). Recording a violation is the third way to close a report and goes through
/// POST /api/ward/violations with SourceReportId.
/// </summary>
public sealed record ResolveVendorReportRequest(
    string ExpectedStatus,
    string Outcome,
    string Note,
    decimal? RefundAmount = null);

public interface IVendorReportDecisions
{
    /// <summary>
    /// Closes a PENDING report of the officer's ward in one serializable transaction: the outcome,
    /// reviewed_by/reviewed_at (BR-46), a VENDOR_REPORT_RESOLVED audit row and the notices to the
    /// reporter and the vendor. Another ward's report is not found (404); a report no longer in
    /// <see cref="ResolveVendorReportRequest.ExpectedStatus"/> is a conflict (409).
    /// </summary>
    Task ResolveAsync(WardActor actor, long reportId, ResolveVendorReportRequest request, CancellationToken cancellationToken);
}

public sealed record ResolveWardVendorReportCommand(long ReportId, ResolveVendorReportRequest Request)
    : IRequest<WardVendorReportDto>;

public sealed class ResolveWardVendorReportCommandValidator : AbstractValidator<ResolveWardVendorReportCommand>
{
    public ResolveWardVendorReportCommandValidator()
    {
        RuleFor(x => x.ReportId).GreaterThan(0);
        RuleFor(x => x.Request.ExpectedStatus).NotEmpty().WithMessage("Thiếu trạng thái phản ánh đang xem.");
        RuleFor(x => x.Request.Outcome).Must(VendorReportResolutions.IsResolveOutcome)
            .WithMessage("Kết quả chỉ được là DISMISSED (bác) hoặc MEDIATED (đã hòa giải). Lập biên bản qua màn biên bản.");
        RuleFor(x => x.Request.Note)
            .Must(note => !string.IsNullOrWhiteSpace(note) && note.Trim().Length <= VendorReportRules.NoteMaxLength)
            .WithMessage($"Nhập lý do hoặc ghi chú, tối đa {VendorReportRules.NoteMaxLength} ký tự.");
        RuleFor(x => x.Request.RefundAmount)
            .Null().When(x => x.Request.Outcome != VendorReportResolutions.Mediated)
            .WithMessage("Chỉ ghi số tiền hoàn khi đã hòa giải.");
        RuleFor(x => x.Request.RefundAmount)
            .Must(amount => amount is null
                || (amount.Value >= 0 && amount.Value <= VendorReportRules.MaxChargedAmount
                    && decimal.Truncate(amount.Value) == amount.Value))
            .WithMessage("Số tiền hoàn phải là số nguyên VND không âm.");
    }
}

/// <summary>
/// WARD-19. Only a ward officer closes a report (BR-41/43); a platform administrator is refused by
/// the ward actor check (BR-44). Nothing here closes a report on its own.
/// </summary>
public sealed class ResolveWardVendorReportCommandHandler(
    IWardActorContext actorContext,
    IVendorReportDecisions decisions,
    IAiMarketplaceRepository repository,
    IVendorReportAssistant assistant,
    TimeProvider? clock = null) : IRequestHandler<ResolveWardVendorReportCommand, WardVendorReportDto>
{
    public async Task<WardVendorReportDto> Handle(ResolveWardVendorReportCommand request, CancellationToken cancellationToken)
    {
        var actor = await actorContext.RequireAsync(cancellationToken);
        var trimmed = request.Request with { Note = request.Request.Note.Trim() };
        await decisions.ResolveAsync(actor, request.ReportId, trimmed, cancellationToken);
        var report = await WardVendorReportAccess.RequireAsync(actorContext, repository, request.ReportId, cancellationToken);
        return WardVendorReportAccess.ToDto(
            report, await assistant.ReadAsync(report, cancellationToken), (clock ?? TimeProvider.System).GetUtcNow().UtcDateTime);
    }
}
