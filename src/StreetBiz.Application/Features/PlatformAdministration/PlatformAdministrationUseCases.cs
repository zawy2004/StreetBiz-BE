using FluentValidation;
using MediatR;
using StreetBiz.Application.Common.Exceptions;
using StreetBiz.Application.Common.Interfaces;
using StreetBiz.Application.Common.Models;
using StreetBiz.Application.Common.Security;
using StreetBiz.Application.DTOs.PlatformAdministration;

namespace StreetBiz.Application.Features.PlatformAdministration;

public sealed record GetPlatformAdminProfileQuery : IRequest<PlatformAdminProfileDto>;

public sealed class GetPlatformAdminProfileQueryHandler(IPlatformAdminContext actorContext)
    : IRequestHandler<GetPlatformAdminProfileQuery, PlatformAdminProfileDto>
{
    public async Task<PlatformAdminProfileDto> Handle(
        GetPlatformAdminProfileQuery request,
        CancellationToken cancellationToken)
    {
        var actor = await actorContext.RequireAsync(cancellationToken);
        return new PlatformAdminProfileDto(actor.UserId, actor.Name);
    }
}

public sealed record ListFoodCategoriesQuery : IRequest<IReadOnlyList<FoodCategoryDto>>;

public sealed class ListFoodCategoriesQueryHandler(
    IPlatformAdminContext actorContext,
    IPlatformAdministrationRepository repository)
    : IRequestHandler<ListFoodCategoriesQuery, IReadOnlyList<FoodCategoryDto>>
{
    public async Task<IReadOnlyList<FoodCategoryDto>> Handle(
        ListFoodCategoriesQuery request,
        CancellationToken cancellationToken)
    {
        await actorContext.RequireAsync(cancellationToken);
        var rows = await repository.ListFoodCategoriesAsync(cancellationToken);
        return rows.Select(row => row.ToDto()).ToArray();
    }
}

public sealed record CreateFoodCategoryCommand(string Name) : IRequest<FoodCategoryDto>;

public sealed class CreateFoodCategoryCommandValidator : AbstractValidator<CreateFoodCategoryCommand>
{
    public CreateFoodCategoryCommandValidator() =>
        RuleFor(x => x.Name).NotEmpty().MaximumLength(100);
}

public sealed class CreateFoodCategoryCommandHandler(
    IPlatformAdminContext actorContext,
    IPlatformAdministrationRepository repository)
    : IRequestHandler<CreateFoodCategoryCommand, FoodCategoryDto>
{
    public async Task<FoodCategoryDto> Handle(
        CreateFoodCategoryCommand request,
        CancellationToken cancellationToken)
    {
        var actor = await actorContext.RequireAsync(cancellationToken);
        var name = request.Name.Trim();
        if (await repository.FoodCategoryNameExistsAsync(name, null, cancellationToken))
        {
            throw new ConflictException(PlatformAdministrationMessages.CategoryDuplicate);
        }

        var row = await repository.CreateFoodCategoryAsync(name, actor.UserId, cancellationToken)
            ?? throw new ConflictException(PlatformAdministrationMessages.CategoryDuplicate);
        return row.ToDto();
    }
}

public sealed record RenameFoodCategoryCommand(int CategoryId, string Name)
    : IRequest<FoodCategoryDto>;

public sealed class RenameFoodCategoryCommandValidator : AbstractValidator<RenameFoodCategoryCommand>
{
    public RenameFoodCategoryCommandValidator()
    {
        RuleFor(x => x.CategoryId).GreaterThan(0);
        RuleFor(x => x.Name).NotEmpty().MaximumLength(100);
    }
}

public sealed class RenameFoodCategoryCommandHandler(
    IPlatformAdminContext actorContext,
    IPlatformAdministrationRepository repository)
    : IRequestHandler<RenameFoodCategoryCommand, FoodCategoryDto>
{
    public async Task<FoodCategoryDto> Handle(
        RenameFoodCategoryCommand request,
        CancellationToken cancellationToken)
    {
        var actor = await actorContext.RequireAsync(cancellationToken);
        var name = request.Name.Trim();
        if (await repository.FoodCategoryNameExistsAsync(name, request.CategoryId, cancellationToken))
        {
            throw new ConflictException(PlatformAdministrationMessages.CategoryDuplicate);
        }

        var row = await repository.RenameFoodCategoryAsync(
            request.CategoryId, name, actor.UserId, cancellationToken)
            ?? throw new NotFoundException(PlatformAdministrationMessages.CategoryNotFound);
        return row.ToDto();
    }
}

public sealed record DeleteFoodCategoryCommand(int CategoryId) : IRequest<Unit>;

public sealed class DeleteFoodCategoryCommandValidator : AbstractValidator<DeleteFoodCategoryCommand>
{
    public DeleteFoodCategoryCommandValidator() => RuleFor(x => x.CategoryId).GreaterThan(0);
}

public sealed class DeleteFoodCategoryCommandHandler(
    IPlatformAdminContext actorContext,
    IPlatformAdministrationRepository repository)
    : IRequestHandler<DeleteFoodCategoryCommand, Unit>
{
    public async Task<Unit> Handle(
        DeleteFoodCategoryCommand request,
        CancellationToken cancellationToken)
    {
        var actor = await actorContext.RequireAsync(cancellationToken);
        var outcome = await repository.DeleteFoodCategoryAsync(
            request.CategoryId, actor.UserId, cancellationToken);
        switch (outcome)
        {
            case CategoryDeleteOutcome.NotFound:
                throw new NotFoundException(PlatformAdministrationMessages.CategoryNotFound);
            case CategoryDeleteOutcome.InUse:
                throw new DomainRuleException(PlatformAdministrationMessages.CategoryInUse);
        }

        return Unit.Value;
    }
}

public sealed record ListReportedContentQuery(string? Status, int Page = 1, int PageSize = 50)
    : IRequest<PlatformPageDto<ReportedContentDto>>;

public sealed class ListReportedContentQueryValidator : AbstractValidator<ListReportedContentQuery>
{
    public ListReportedContentQueryValidator()
    {
        RuleFor(x => x.Status)
            .Must(status => status is null || ReportedContentStatuses.IsValid(status.ToUpperInvariant()))
            .WithMessage("Unsupported reported-content status.");
        RuleFor(x => x.Page).InclusiveBetween(1, 10_000);
        RuleFor(x => x.PageSize).InclusiveBetween(1, 100);
    }
}

public sealed class ListReportedContentQueryHandler(
    IPlatformAdminContext actorContext,
    IPlatformAdministrationRepository repository)
    : IRequestHandler<ListReportedContentQuery, PlatformPageDto<ReportedContentDto>>
{
    public async Task<PlatformPageDto<ReportedContentDto>> Handle(
        ListReportedContentQuery request,
        CancellationToken cancellationToken)
    {
        await actorContext.RequireAsync(cancellationToken);
        var status = string.IsNullOrWhiteSpace(request.Status)
            ? null
            : request.Status.Trim().ToUpperInvariant();
        var page = await repository.ListReportedContentAsync(
            status, request.Page, request.PageSize, cancellationToken);
        return page.ToDto(row => row.ToDto());
    }
}

public sealed record GetReportedContentQuery(long ReportId) : IRequest<ReportedContentDto>;

public sealed class GetReportedContentQueryValidator : AbstractValidator<GetReportedContentQuery>
{
    public GetReportedContentQueryValidator() => RuleFor(x => x.ReportId).GreaterThan(0);
}

public sealed class GetReportedContentQueryHandler(
    IPlatformAdminContext actorContext,
    IPlatformAdministrationRepository repository)
    : IRequestHandler<GetReportedContentQuery, ReportedContentDto>
{
    public async Task<ReportedContentDto> Handle(
        GetReportedContentQuery request,
        CancellationToken cancellationToken)
    {
        await actorContext.RequireAsync(cancellationToken);
        return (await repository.GetReportedContentAsync(request.ReportId, cancellationToken))?.ToDto()
            ?? throw new NotFoundException(PlatformAdministrationMessages.ReportNotFound);
    }
}

public sealed record DecideReportedContentCommand(
    long ReportId,
    string Decision,
    string ExpectedStatus) : IRequest<ReportedContentDto>;

public sealed class DecideReportedContentCommandValidator
    : AbstractValidator<DecideReportedContentCommand>
{
    public DecideReportedContentCommandValidator()
    {
        RuleFor(x => x.ReportId).GreaterThan(0);
        RuleFor(x => x.Decision)
            .Cascade(CascadeMode.Stop)
            .NotEmpty()
            .Must(decision => ContentModerationDecisions.IsValid(decision.ToUpperInvariant()))
            .WithMessage("Decision must be DISMISS or HIDE.");
        RuleFor(x => x.ExpectedStatus)
            .Equal(ReportedContentStatuses.Pending)
            .WithMessage("Only a PENDING report can be reviewed.");
    }
}

public sealed class DecideReportedContentCommandHandler(
    IPlatformAdminContext actorContext,
    IPlatformAdministrationRepository repository)
    : IRequestHandler<DecideReportedContentCommand, ReportedContentDto>
{
    public async Task<ReportedContentDto> Handle(
        DecideReportedContentCommand request,
        CancellationToken cancellationToken)
    {
        var actor = await actorContext.RequireAsync(cancellationToken);
        var result = await repository.DecideReportedContentAsync(
            request.ReportId,
            request.ExpectedStatus,
            request.Decision.ToUpperInvariant(),
            actor.UserId,
            cancellationToken);

        return result.Outcome switch
        {
            ContentDecisionOutcome.Updated => result.Report!.ToDto(),
            ContentDecisionOutcome.NotFound => throw new NotFoundException(
                PlatformAdministrationMessages.ReportNotFound),
            ContentDecisionOutcome.ContentNotFound => throw new DomainRuleException(
                PlatformAdministrationMessages.ReportedContentNotFound),
            _ => throw new ConflictException(PlatformAdministrationMessages.ReportConflict),
        };
    }
}

public sealed record ListOrderComplaintsQuery(string? Status, int Page = 1, int PageSize = 50)
    : IRequest<PlatformPageDto<OrderComplaintDto>>;

public sealed class ListOrderComplaintsQueryValidator : AbstractValidator<ListOrderComplaintsQuery>
{
    public ListOrderComplaintsQueryValidator()
    {
        RuleFor(x => x.Status)
            .Must(status => status is null || ComplaintStatuses.IsValid(status.ToUpperInvariant()))
            .WithMessage("Unsupported complaint status.");
        RuleFor(x => x.Page).InclusiveBetween(1, 10_000);
        RuleFor(x => x.PageSize).InclusiveBetween(1, 100);
    }
}

public sealed class ListOrderComplaintsQueryHandler(
    IPlatformAdminContext actorContext,
    IPlatformAdministrationRepository repository)
    : IRequestHandler<ListOrderComplaintsQuery, PlatformPageDto<OrderComplaintDto>>
{
    public async Task<PlatformPageDto<OrderComplaintDto>> Handle(
        ListOrderComplaintsQuery request,
        CancellationToken cancellationToken)
    {
        await actorContext.RequireAsync(cancellationToken);
        var status = string.IsNullOrWhiteSpace(request.Status)
            ? null
            : request.Status.Trim().ToUpperInvariant();
        var page = await repository.ListOrderComplaintsAsync(
            status, request.Page, request.PageSize, cancellationToken);
        return page.ToDto(row => row.ToDto());
    }
}

public sealed record GetOrderComplaintQuery(long ComplaintId) : IRequest<OrderComplaintDto>;

public sealed class GetOrderComplaintQueryValidator : AbstractValidator<GetOrderComplaintQuery>
{
    public GetOrderComplaintQueryValidator() => RuleFor(x => x.ComplaintId).GreaterThan(0);
}

public sealed class GetOrderComplaintQueryHandler(
    IPlatformAdminContext actorContext,
    IPlatformAdministrationRepository repository)
    : IRequestHandler<GetOrderComplaintQuery, OrderComplaintDto>
{
    public async Task<OrderComplaintDto> Handle(
        GetOrderComplaintQuery request,
        CancellationToken cancellationToken)
    {
        await actorContext.RequireAsync(cancellationToken);
        return (await repository.GetOrderComplaintAsync(request.ComplaintId, cancellationToken))?.ToDto()
            ?? throw new NotFoundException(PlatformAdministrationMessages.ComplaintNotFound);
    }
}

public sealed record DecideOrderComplaintCommand(
    long ComplaintId,
    string Decision,
    string Notes,
    string ExpectedStatus,
    decimal? ApprovedRefundAmount) : IRequest<OrderComplaintDto>;

public sealed class DecideOrderComplaintCommandValidator
    : AbstractValidator<DecideOrderComplaintCommand>
{
    public DecideOrderComplaintCommandValidator()
    {
        RuleFor(x => x.ComplaintId).GreaterThan(0);
        RuleFor(x => x.Decision)
            .Cascade(CascadeMode.Stop)
            .NotEmpty()
            .Must(decision => ComplaintDecisions.IsValid(decision.ToUpperInvariant()))
            .WithMessage("Decision must be RESOLVE or REJECT.");
        RuleFor(x => x.Notes).NotEmpty().MaximumLength(1000);
        RuleFor(x => x.ExpectedStatus)
            .Must(status => status is ComplaintStatuses.Open or ComplaintStatuses.UnderReview)
            .WithMessage("Only an OPEN or UNDER_REVIEW complaint can be decided.");
        RuleFor(x => x.ApprovedRefundAmount)
            .GreaterThan(0)
            .PrecisionScale(18, 0, false)
            .When(x => x.ApprovedRefundAmount.HasValue);
        RuleFor(x => x.ApprovedRefundAmount)
            .Null()
            .When(x => string.Equals(
                x.Decision,
                ComplaintDecisions.Reject,
                StringComparison.OrdinalIgnoreCase))
            .WithMessage("A rejected complaint cannot create a refund.");
    }
}

public sealed class DecideOrderComplaintCommandHandler(
    IPlatformAdminContext actorContext,
    IPlatformAdministrationRepository repository)
    : IRequestHandler<DecideOrderComplaintCommand, OrderComplaintDto>
{
    public async Task<OrderComplaintDto> Handle(
        DecideOrderComplaintCommand request,
        CancellationToken cancellationToken)
    {
        var actor = await actorContext.RequireAsync(cancellationToken);
        var result = await repository.DecideOrderComplaintAsync(
            request.ComplaintId,
            new ComplaintResolution(
                request.Decision.ToUpperInvariant(),
                request.Notes.Trim(),
                request.ExpectedStatus,
                request.ApprovedRefundAmount),
            actor.UserId,
            cancellationToken);

        return result.Outcome switch
        {
            ComplaintDecisionOutcome.Updated => result.Complaint!.ToDto(),
            ComplaintDecisionOutcome.NotFound => throw new NotFoundException(
                PlatformAdministrationMessages.ComplaintNotFound),
            ComplaintDecisionOutcome.Conflict => throw new ConflictException(
                PlatformAdministrationMessages.ComplaintConflict),
            ComplaintDecisionOutcome.RefundNotAllowed => throw new DomainRuleException(
                PlatformAdministrationMessages.RefundNotAllowed),
            ComplaintDecisionOutcome.PaymentNotFound => throw new DomainRuleException(
                PlatformAdministrationMessages.PaymentNotFound),
            _ => throw new DomainRuleException(PlatformAdministrationMessages.RefundExceedsLimit),
        };
    }
}

internal static class PlatformAdministrationMapping
{
    public static FoodCategoryDto ToDto(this FoodCategoryRow row) => new(
        row.CategoryId, row.CategoryName, row.ItemCount, row.CreatedByName);

    public static ReportedContentDto ToDto(this ReportedContentRow row) => new(
        row.ReportId, row.ContentType, row.ContentId, row.ContentTitle, row.ContentBody,
        row.ContentStatus, row.ContentExists, row.ReporterName, row.Reason, row.Status,
        row.ReviewedByName, row.CreatedAt, row.ReviewedAt);

    public static OrderComplaintDto ToDto(this OrderComplaintRow row) => new(
        row.ComplaintId, row.OrderId, row.OrderCode, row.OrderStatus, row.CustomerName,
        row.StorefrontName, row.ComplaintType, row.Description, row.RequestedRefundAmount,
        row.Status, row.ResolutionNotes, row.ResolvedByName, row.CreatedAt, row.ResolvedAt,
        row.PaymentAmount, row.PaymentProvider, row.RefundedAmount, row.LatestRefundId,
        row.LatestRefundStatus);

    public static PlatformPageDto<TDto> ToDto<TRow, TDto>(
        this PlatformPage<TRow> page,
        Func<TRow, TDto> map) => new(
        page.Items.Select(map).ToArray(), page.Page, page.PageSize, page.TotalCount);
}
