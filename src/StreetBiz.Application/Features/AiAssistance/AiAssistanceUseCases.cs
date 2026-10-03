using FluentValidation;
using MediatR;
using StreetBiz.Application.Features.WardSlots;

namespace StreetBiz.Application.Features.AiAssistance;

#region AIC-04: proposed-slot feasibility
public sealed record GetProposalAssessmentQuery(long SlotId) : IRequest<AiProposalAssessmentView>;

public sealed class GetProposalAssessmentQueryValidator : AbstractValidator<GetProposalAssessmentQuery>
{
    public GetProposalAssessmentQueryValidator() => RuleFor(x => x.SlotId).GreaterThan(0);
}

public sealed class GetProposalAssessmentQueryHandler(
    IWardActorContext actorContext, IWardAiInsights insights)
    : IRequestHandler<GetProposalAssessmentQuery, AiProposalAssessmentView>
{
    public async Task<AiProposalAssessmentView> Handle(GetProposalAssessmentQuery request, CancellationToken cancellationToken)
    {
        var actor = await actorContext.RequireAsync(cancellationToken);
        return await insights.GetProposalAssessmentAsync(actor, request.SlotId, cancellationToken);
    }
}

public sealed record RunProposalAssessmentCommand(long SlotId) : IRequest<AiProposalAssessment>;

public sealed class RunProposalAssessmentCommandValidator : AbstractValidator<RunProposalAssessmentCommand>
{
    public RunProposalAssessmentCommandValidator() => RuleFor(x => x.SlotId).GreaterThan(0);
}

public sealed class RunProposalAssessmentCommandHandler(
    IWardActorContext actorContext, IWardAiInsights insights)
    : IRequestHandler<RunProposalAssessmentCommand, AiProposalAssessment>
{
    public async Task<AiProposalAssessment> Handle(RunProposalAssessmentCommand request, CancellationToken cancellationToken)
    {
        var actor = await actorContext.RequireAsync(cancellationToken);
        return await insights.RunProposalAssessmentAsync(actor, request.SlotId, cancellationToken);
    }
}
#endregion

#region AIC-06: geofence drift
public sealed record GetGeofenceDriftQuery : IRequest<GeofenceDriftReportDto>;

public sealed class GetGeofenceDriftQueryHandler(
    IWardActorContext actorContext, IWardAiInsights insights)
    : IRequestHandler<GetGeofenceDriftQuery, GeofenceDriftReportDto>
{
    public async Task<GeofenceDriftReportDto> Handle(GetGeofenceDriftQuery request, CancellationToken cancellationToken)
    {
        var actor = await actorContext.RequireAsync(cancellationToken);
        return await insights.GetGeofenceDriftAsync(actor, cancellationToken);
    }
}
#endregion

#region AIC-07: zone price suggestion
public sealed record GetZonePriceSuggestionQuery(int ZoneId) : IRequest<ZonePriceSuggestionDto>;

public sealed class GetZonePriceSuggestionQueryValidator : AbstractValidator<GetZonePriceSuggestionQuery>
{
    public GetZonePriceSuggestionQueryValidator() => RuleFor(x => x.ZoneId).GreaterThan(0);
}

public sealed class GetZonePriceSuggestionQueryHandler(
    IWardActorContext actorContext, IWardAiInsights insights)
    : IRequestHandler<GetZonePriceSuggestionQuery, ZonePriceSuggestionDto>
{
    public async Task<ZonePriceSuggestionDto> Handle(GetZonePriceSuggestionQuery request, CancellationToken cancellationToken)
    {
        var actor = await actorContext.RequireAsync(cancellationToken);
        return await insights.GetZonePriceSuggestionAsync(actor, request.ZoneId, cancellationToken);
    }
}
#endregion

#region BR-41: officer feedback on any AI suggestion
public sealed record ReviewAiSuggestionCommand(long AiLogId, AiSuggestionFeedbackRequest Request) : IRequest<AiSuggestionFeedbackDto>;

public sealed class ReviewAiSuggestionCommandValidator : AbstractValidator<ReviewAiSuggestionCommand>
{
    public ReviewAiSuggestionCommandValidator()
    {
        RuleFor(x => x.AiLogId).GreaterThan(0);
        RuleFor(x => x.Request.Note).MaximumLength(500);
    }
}

public sealed class ReviewAiSuggestionCommandHandler(
    IWardActorContext actorContext, IAiAssistanceLogs aiLogs)
    : IRequestHandler<ReviewAiSuggestionCommand, AiSuggestionFeedbackDto>
{
    public async Task<AiSuggestionFeedbackDto> Handle(ReviewAiSuggestionCommand request, CancellationToken cancellationToken)
    {
        var actor = await actorContext.RequireAsync(cancellationToken);
        return await aiLogs.ReviewAsync(actor, request.AiLogId, request.Request, cancellationToken);
    }
}
#endregion
