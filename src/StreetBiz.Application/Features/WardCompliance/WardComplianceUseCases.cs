using FluentValidation;
using MediatR;
using StreetBiz.Application.Features.WardSlots;

namespace StreetBiz.Application.Features.WardCompliance;

#region Enrollment / Registration Use Cases
public sealed record ListWardEnrollmentsQuery(string? Status, int Page = 1) : IRequest<IReadOnlyList<WardEnrollmentListItemDto>>;

public sealed class ListWardEnrollmentsQueryValidator : AbstractValidator<ListWardEnrollmentsQuery>
{
    public ListWardEnrollmentsQueryValidator()
    {
        RuleFor(x => x.Page).GreaterThan(0);
    }
}

public sealed class ListWardEnrollmentsQueryHandler(
    IWardActorContext actorContext,
    IWardComplianceService complianceService)
    : IRequestHandler<ListWardEnrollmentsQuery, IReadOnlyList<WardEnrollmentListItemDto>>
{
    public async Task<IReadOnlyList<WardEnrollmentListItemDto>> Handle(
        ListWardEnrollmentsQuery request,
        CancellationToken cancellationToken)
    {
        var actor = await actorContext.RequireAsync(cancellationToken);
        return await complianceService.ListEnrollmentsAsync(actor, request.Status, request.Page, cancellationToken);
    }
}

public sealed record GetWardEnrollmentDetailQuery(long Id) : IRequest<WardEnrollmentDetailDto>;

public sealed class GetWardEnrollmentDetailQueryValidator : AbstractValidator<GetWardEnrollmentDetailQuery>
{
    public GetWardEnrollmentDetailQueryValidator()
    {
        RuleFor(x => x.Id).GreaterThan(0);
    }
}

public sealed class GetWardEnrollmentDetailQueryHandler(
    IWardActorContext actorContext,
    IWardComplianceService complianceService)
    : IRequestHandler<GetWardEnrollmentDetailQuery, WardEnrollmentDetailDto>
{
    public async Task<WardEnrollmentDetailDto> Handle(
        GetWardEnrollmentDetailQuery request,
        CancellationToken cancellationToken)
    {
        var actor = await actorContext.RequireAsync(cancellationToken);
        return await complianceService.GetEnrollmentDetailAsync(actor, request.Id, cancellationToken);
    }
}

public sealed record DecideWardEnrollmentCommand(
    long Id,
    WardEnrollmentDecision Decision) : IRequest<WardEnrollmentDetailDto>;

public sealed class DecideWardEnrollmentCommandValidator : AbstractValidator<DecideWardEnrollmentCommand>
{
    public DecideWardEnrollmentCommandValidator()
    {
        RuleFor(x => x.Id).GreaterThan(0);
        RuleFor(x => x.Decision.Decision).Must(d => d is "APPROVE" or "REJECT" or "MORE_INFO")
            .WithMessage("Quyết định phải là APPROVE, REJECT hoặc MORE_INFO.");
        RuleFor(x => x.Decision.Reason)
            .NotEmpty().WithMessage("Lý do quyết định không được để trống.")
            .MaximumLength(500).WithMessage("Lý do quyết định không quá 500 ký tự.");
        RuleFor(x => x.Decision.ExpectedStatus).NotEmpty().WithMessage("Thiếu trạng thái hồ sơ kỳ vọng.");
    }
}

public sealed class DecideWardEnrollmentCommandHandler(
    IWardActorContext actorContext,
    IWardComplianceService complianceService)
    : IRequestHandler<DecideWardEnrollmentCommand, WardEnrollmentDetailDto>
{
    public async Task<WardEnrollmentDetailDto> Handle(
        DecideWardEnrollmentCommand request,
        CancellationToken cancellationToken)
    {
        var actor = await actorContext.RequireAsync(cancellationToken);
        return await complianceService.DecideEnrollmentAsync(actor, request.Id, request.Decision, cancellationToken);
    }
}
#endregion

#region Rental Application Use Cases
public sealed record ListWardRentalApplicationsQuery(string? Status, int Page = 1) : IRequest<IReadOnlyList<WardRentalApplicationListItemDto>>;

public sealed class ListWardRentalApplicationsQueryValidator : AbstractValidator<ListWardRentalApplicationsQuery>
{
    public ListWardRentalApplicationsQueryValidator()
    {
        RuleFor(x => x.Page).GreaterThan(0);
    }
}

public sealed class ListWardRentalApplicationsQueryHandler(
    IWardActorContext actorContext,
    IWardComplianceService complianceService)
    : IRequestHandler<ListWardRentalApplicationsQuery, IReadOnlyList<WardRentalApplicationListItemDto>>
{
    public async Task<IReadOnlyList<WardRentalApplicationListItemDto>> Handle(
        ListWardRentalApplicationsQuery request,
        CancellationToken cancellationToken)
    {
        var actor = await actorContext.RequireAsync(cancellationToken);
        return await complianceService.ListRentalApplicationsAsync(actor, request.Status, request.Page, cancellationToken);
    }
}

public sealed record GetWardRentalApplicationDetailQuery(long Id) : IRequest<WardRentalApplicationDetailDto>;

public sealed class GetWardRentalApplicationDetailQueryValidator : AbstractValidator<GetWardRentalApplicationDetailQuery>
{
    public GetWardRentalApplicationDetailQueryValidator()
    {
        RuleFor(x => x.Id).GreaterThan(0);
    }
}

public sealed class GetWardRentalApplicationDetailQueryHandler(
    IWardActorContext actorContext,
    IWardComplianceService complianceService)
    : IRequestHandler<GetWardRentalApplicationDetailQuery, WardRentalApplicationDetailDto>
{
    public async Task<WardRentalApplicationDetailDto> Handle(
        GetWardRentalApplicationDetailQuery request,
        CancellationToken cancellationToken)
    {
        var actor = await actorContext.RequireAsync(cancellationToken);
        return await complianceService.GetRentalApplicationDetailAsync(actor, request.Id, cancellationToken);
    }
}

public sealed record DecideWardRentalApplicationCommand(
    long Id,
    WardRentalApplicationDecision Decision) : IRequest<WardRentalApplicationDetailDto>;

public sealed class DecideWardRentalApplicationCommandValidator : AbstractValidator<DecideWardRentalApplicationCommand>
{
    public DecideWardRentalApplicationCommandValidator()
    {
        RuleFor(x => x.Id).GreaterThan(0);
        RuleFor(x => x.Decision.Decision).Must(d => d is "APPROVE" or "REJECT")
            .WithMessage("Quyết định phải là APPROVE hoặc REJECT.");
        RuleFor(x => x.Decision.Reason)
            .NotEmpty().WithMessage("Lý do quyết định không được để trống.")
            .MaximumLength(500).WithMessage("Lý do quyết định không quá 500 ký tự.");
        RuleFor(x => x.Decision.ExpectedStatus).NotEmpty().WithMessage("Thiếu trạng thái hồ sơ kỳ vọng.");
    }
}

public sealed class DecideWardRentalApplicationCommandHandler(
    IWardActorContext actorContext,
    IWardComplianceService complianceService)
    : IRequestHandler<DecideWardRentalApplicationCommand, WardRentalApplicationDetailDto>
{
    public async Task<WardRentalApplicationDetailDto> Handle(
        DecideWardRentalApplicationCommand request,
        CancellationToken cancellationToken)
    {
        var actor = await actorContext.RequireAsync(cancellationToken);
        return await complianceService.DecideRentalApplicationAsync(actor, request.Id, request.Decision, cancellationToken);
    }
}
#endregion

#region On-site Inspection & Permit Use Cases
public sealed record InspectWardPermitQuery(InspectWardPermitRequest Request) : IRequest<InspectWardPermitResult>;

public sealed class InspectWardPermitQueryValidator : AbstractValidator<InspectWardPermitQuery>
{
    public InspectWardPermitQueryValidator()
    {
        RuleFor(x => x.Request.PermitCodeOrPayload).NotEmpty().WithMessage("Mã giấy phép hoặc payload QR không được để trống.");
        RuleFor(x => x.Request.Latitude).InclusiveBetween(-90, 90).When(x => x.Request.Latitude.HasValue);
        RuleFor(x => x.Request.Longitude).InclusiveBetween(-180, 180).When(x => x.Request.Longitude.HasValue);
    }
}

public sealed class InspectWardPermitQueryHandler(
    IWardActorContext actorContext,
    IWardComplianceService complianceService)
    : IRequestHandler<InspectWardPermitQuery, InspectWardPermitResult>
{
    public async Task<InspectWardPermitResult> Handle(
        InspectWardPermitQuery request,
        CancellationToken cancellationToken)
    {
        var actor = await actorContext.RequireAsync(cancellationToken);
        return await complianceService.InspectPermitAsync(actor, request.Request, cancellationToken);
    }
}

public sealed record ExecuteWardPermitActionCommand(
    long PermitId,
    WardPermitActionRequest Request) : IRequest<bool>;

public sealed class ExecuteWardPermitActionCommandValidator : AbstractValidator<ExecuteWardPermitActionCommand>
{
    public ExecuteWardPermitActionCommandValidator()
    {
        RuleFor(x => x.PermitId).GreaterThan(0);
        RuleFor(x => x.Request.Action).Must(a => a is "SUSPEND" or "REVOKE")
            .WithMessage("Hành động phải là SUSPEND hoặc REVOKE.");
        RuleFor(x => x.Request.Reason).NotEmpty().WithMessage("Lý do đình chỉ/thu hồi không được để trống.")
            .MaximumLength(500).WithMessage("Lý do không quá 500 ký tự.");
    }
}

public sealed class ExecuteWardPermitActionCommandHandler(
    IWardActorContext actorContext,
    IWardComplianceService complianceService)
    : IRequestHandler<ExecuteWardPermitActionCommand, bool>
{
    public async Task<bool> Handle(
        ExecuteWardPermitActionCommand request,
        CancellationToken cancellationToken)
    {
        var actor = await actorContext.RequireAsync(cancellationToken);
        return await complianceService.ExecutePermitActionAsync(actor, request.PermitId, request.Request, cancellationToken);
    }
}
#endregion

#region Violations & Sanctions Use Cases
public sealed record ListWardPenaltySchedulesQuery : IRequest<IReadOnlyList<PenaltyScheduleItemDto>>;

public sealed class ListWardPenaltySchedulesQueryHandler(
    IWardActorContext actorContext,
    IWardComplianceService complianceService)
    : IRequestHandler<ListWardPenaltySchedulesQuery, IReadOnlyList<PenaltyScheduleItemDto>>
{
    public async Task<IReadOnlyList<PenaltyScheduleItemDto>> Handle(
        ListWardPenaltySchedulesQuery request,
        CancellationToken cancellationToken)
    {
        var actor = await actorContext.RequireAsync(cancellationToken);
        return await complianceService.ListPenaltySchedulesAsync(actor, cancellationToken);
    }
}

public sealed record ListWardViolationsQuery(string? Status, int Page = 1) : IRequest<IReadOnlyList<WardViolationListItemDto>>;

public sealed class ListWardViolationsQueryValidator : AbstractValidator<ListWardViolationsQuery>
{
    public ListWardViolationsQueryValidator()
    {
        RuleFor(x => x.Page).GreaterThan(0);
    }
}

public sealed class ListWardViolationsQueryHandler(
    IWardActorContext actorContext,
    IWardComplianceService complianceService)
    : IRequestHandler<ListWardViolationsQuery, IReadOnlyList<WardViolationListItemDto>>
{
    public async Task<IReadOnlyList<WardViolationListItemDto>> Handle(
        ListWardViolationsQuery request,
        CancellationToken cancellationToken)
    {
        var actor = await actorContext.RequireAsync(cancellationToken);
        return await complianceService.ListViolationsAsync(actor, request.Status, request.Page, cancellationToken);
    }
}

public sealed record GetWardViolationDetailQuery(long Id) : IRequest<WardViolationDetailDto>;

public sealed class GetWardViolationDetailQueryValidator : AbstractValidator<GetWardViolationDetailQuery>
{
    public GetWardViolationDetailQueryValidator()
    {
        RuleFor(x => x.Id).GreaterThan(0);
    }
}

public sealed class GetWardViolationDetailQueryHandler(
    IWardActorContext actorContext,
    IWardComplianceService complianceService)
    : IRequestHandler<GetWardViolationDetailQuery, WardViolationDetailDto>
{
    public async Task<WardViolationDetailDto> Handle(
        GetWardViolationDetailQuery request,
        CancellationToken cancellationToken)
    {
        var actor = await actorContext.RequireAsync(cancellationToken);
        return await complianceService.GetViolationDetailAsync(actor, request.Id, cancellationToken);
    }
}

public sealed record RecordWardViolationCommand(RecordWardViolationRequest Request) : IRequest<WardViolationDetailDto>;

public sealed class RecordWardViolationCommandValidator : AbstractValidator<RecordWardViolationCommand>
{
    public RecordWardViolationCommandValidator()
    {
        RuleFor(x => x.Request.ViolationType).NotEmpty().WithMessage("Loại vi phạm không được để trống.");
        RuleFor(x => x.Request.Description).NotEmpty().WithMessage("Mô tả vi phạm không được để trống.")
            .MaximumLength(1000).WithMessage("Mô tả không quá 1000 ký tự.");
    }
}

public sealed class RecordWardViolationCommandHandler(
    IWardActorContext actorContext,
    IWardComplianceService complianceService)
    : IRequestHandler<RecordWardViolationCommand, WardViolationDetailDto>
{
    public async Task<WardViolationDetailDto> Handle(
        RecordWardViolationCommand request,
        CancellationToken cancellationToken)
    {
        var actor = await actorContext.RequireAsync(cancellationToken);
        return await complianceService.RecordViolationAsync(actor, request.Request, cancellationToken);
    }
}

public sealed record SanctionWardViolationCommand(
    long ViolationId,
    SanctionWardViolationRequest Request) : IRequest<WardViolationDetailDto>;

public sealed class SanctionWardViolationCommandValidator : AbstractValidator<SanctionWardViolationCommand>
{
    public SanctionWardViolationCommandValidator()
    {
        RuleFor(x => x.ViolationId).GreaterThan(0);
        RuleFor(x => x.Request.PenaltyScheduleId).GreaterThan(0).WithMessage("Phải chọn biểu khung phạt hợp lệ.");
        RuleFor(x => x.Request.DecisionNumber).NotEmpty().WithMessage("Số quyết định xử phạt không được để trống.");
        RuleFor(x => x.Request.SignerName).NotEmpty().WithMessage("Thiếu tên người ký quyết định xử phạt.");
        RuleFor(x => x.Request.SignerTitle).NotEmpty().WithMessage("Thiếu chức danh người ký (VD: Chủ tịch UBND Phường).");
    }
}

public sealed class SanctionWardViolationCommandHandler(
    IWardActorContext actorContext,
    IWardComplianceService complianceService)
    : IRequestHandler<SanctionWardViolationCommand, WardViolationDetailDto>
{
    public async Task<WardViolationDetailDto> Handle(
        SanctionWardViolationCommand request,
        CancellationToken cancellationToken)
    {
        var actor = await actorContext.RequireAsync(cancellationToken);
        return await complianceService.SanctionViolationAsync(actor, request.ViolationId, request.Request, cancellationToken);
    }
}
#endregion

#region AI Assistance Queries
/// <summary>
/// Takes only a registration id -- never a client-supplied evidence list or declared
/// name/address (an earlier draft did, and its FE/BE request shapes drifted out of sync as a
/// result). The handler loads the registration's own stored evidence and checks biometric
/// consent server-side, the same way GetEnrollmentDetailAsync's first-view backfill does.
/// </summary>
public sealed record GetAiDocumentExtractQuery(long RegistrationId) : IRequest<AiDocumentCheckResult>;

public sealed class GetAiDocumentExtractQueryValidator : AbstractValidator<GetAiDocumentExtractQuery>
{
    public GetAiDocumentExtractQueryValidator()
    {
        RuleFor(x => x.RegistrationId).GreaterThan(0);
    }
}

public sealed class GetAiDocumentExtractQueryHandler(
    IWardActorContext actorContext,
    IWardComplianceService complianceService)
    : IRequestHandler<GetAiDocumentExtractQuery, AiDocumentCheckResult>
{
    public async Task<AiDocumentCheckResult> Handle(GetAiDocumentExtractQuery request, CancellationToken cancellationToken)
    {
        var actor = await actorContext.RequireAsync(cancellationToken);
        return await complianceService.ReRunDocumentCheckAsync(actor, request.RegistrationId, cancellationToken);
    }
}

public sealed record GetAiEncroachmentCheckQuery(
    string PhotoUrl,
    double? SlotWidth,
    double? SlotLength) : IRequest<AiEncroachmentResult>;

public sealed class GetAiEncroachmentCheckQueryHandler(
    IAiComplianceService aiService) : IRequestHandler<GetAiEncroachmentCheckQuery, AiEncroachmentResult>
{
    public Task<AiEncroachmentResult> Handle(GetAiEncroachmentCheckQuery request, CancellationToken cancellationToken) =>
        aiService.AnalyzeInspectionPhotoAsync(request.PhotoUrl, request.SlotWidth, request.SlotLength, cancellationToken);
}
#endregion

#region Insights Use Cases (rule-based, no LLM)
public sealed record GetWardRiskQueueQuery : IRequest<IReadOnlyList<WardRiskQueueItemDto>>;

public sealed class GetWardRiskQueueQueryHandler(
    IWardActorContext actorContext,
    IWardComplianceService complianceService)
    : IRequestHandler<GetWardRiskQueueQuery, IReadOnlyList<WardRiskQueueItemDto>>
{
    public async Task<IReadOnlyList<WardRiskQueueItemDto>> Handle(
        GetWardRiskQueueQuery request,
        CancellationToken cancellationToken)
    {
        var actor = await actorContext.RequireAsync(cancellationToken);
        return await complianceService.GetRiskQueueAsync(actor, cancellationToken);
    }
}

public sealed record GetWardPatrolHeatmapQuery : IRequest<IReadOnlyList<WardPatrolHeatmapPointDto>>;

public sealed class GetWardPatrolHeatmapQueryHandler(
    IWardActorContext actorContext,
    IWardComplianceService complianceService)
    : IRequestHandler<GetWardPatrolHeatmapQuery, IReadOnlyList<WardPatrolHeatmapPointDto>>
{
    public async Task<IReadOnlyList<WardPatrolHeatmapPointDto>> Handle(
        GetWardPatrolHeatmapQuery request,
        CancellationToken cancellationToken)
    {
        var actor = await actorContext.RequireAsync(cancellationToken);
        return await complianceService.GetPatrolHeatmapAsync(actor, cancellationToken);
    }
}
#endregion
