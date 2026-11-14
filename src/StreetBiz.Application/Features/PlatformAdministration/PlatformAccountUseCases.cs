using FluentValidation;
using MediatR;
using StreetBiz.Application.Common.Exceptions;
using StreetBiz.Application.Common.Interfaces;
using StreetBiz.Application.Common.Models;
using StreetBiz.Application.Common.Security;
using StreetBiz.Application.DTOs.PlatformAdministration;

namespace StreetBiz.Application.Features.PlatformAdministration;

/// <summary>ADM-02: only Customer and Vendor accounts are the platform administrator's to manage.</summary>
public static class ManageableRoles
{
    public static bool IsValid(string value) => value is RoleCodes.Customer or RoleCodes.Vendor;
}

public sealed record ListPlatformAccountsQuery(
    string? Role,
    string? Status,
    string? Search,
    int Page = 1,
    int PageSize = 20) : IRequest<PlatformPageDto<PlatformAccountDto>>;

public sealed class ListPlatformAccountsQueryValidator : AbstractValidator<ListPlatformAccountsQuery>
{
    public ListPlatformAccountsQueryValidator()
    {
        RuleFor(x => x.Role)
            .Must(role => string.IsNullOrWhiteSpace(role) || ManageableRoles.IsValid(role.Trim().ToUpperInvariant()))
            .WithMessage("Role must be CUSTOMER or VENDOR.");
        RuleFor(x => x.Status)
            .Must(status => string.IsNullOrWhiteSpace(status)
                || status.Trim().ToUpperInvariant() is AccountStatuses.Active
                    or AccountStatuses.Suspended or AccountStatuses.Deactivated)
            .WithMessage("Unsupported account status.");
        RuleFor(x => x.Search).MaximumLength(100);
        RuleFor(x => x.Page).InclusiveBetween(1, 10_000);
        RuleFor(x => x.PageSize).InclusiveBetween(1, 100);
    }
}

public sealed class ListPlatformAccountsQueryHandler(
    IPlatformAdminContext actorContext,
    IPlatformAdministrationRepository repository)
    : IRequestHandler<ListPlatformAccountsQuery, PlatformPageDto<PlatformAccountDto>>
{
    public async Task<PlatformPageDto<PlatformAccountDto>> Handle(
        ListPlatformAccountsQuery request,
        CancellationToken cancellationToken)
    {
        await actorContext.RequireAsync(cancellationToken);
        var page = await repository.ListAccountsAsync(
            Normalize(request.Role),
            Normalize(request.Status),
            string.IsNullOrWhiteSpace(request.Search) ? null : request.Search.Trim(),
            request.Page,
            request.PageSize,
            cancellationToken);
        return page.ToDto(row => row.ToDto());
    }

    private static string? Normalize(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim().ToUpperInvariant();
}

public sealed record GetPlatformAccountQuery(long UserId) : IRequest<PlatformAccountDetailDto>;

public sealed class GetPlatformAccountQueryValidator : AbstractValidator<GetPlatformAccountQuery>
{
    public GetPlatformAccountQueryValidator() => RuleFor(x => x.UserId).GreaterThan(0);
}

public sealed class GetPlatformAccountQueryHandler(
    IPlatformAdminContext actorContext,
    IPlatformAdministrationRepository repository)
    : IRequestHandler<GetPlatformAccountQuery, PlatformAccountDetailDto>
{
    public async Task<PlatformAccountDetailDto> Handle(
        GetPlatformAccountQuery request,
        CancellationToken cancellationToken)
    {
        await actorContext.RequireAsync(cancellationToken);
        var detail = await repository.GetAccountAsync(request.UserId, cancellationToken)
            ?? throw new NotFoundException(PlatformAdministrationMessages.AccountNotFound);
        return detail.ToDto();
    }
}

public sealed record SuspendPlatformAccountCommand(long UserId, string Reason, string ExpectedStatus)
    : IRequest<PlatformAccountDetailDto>;

public sealed class SuspendPlatformAccountCommandValidator
    : AbstractValidator<SuspendPlatformAccountCommand>
{
    public SuspendPlatformAccountCommandValidator()
    {
        RuleFor(x => x.UserId).GreaterThan(0);
        RuleFor(x => x.Reason)
            .Cascade(CascadeMode.Stop)
            .NotEmpty()
            .Must(reason => reason.Trim().Length is >= 3 and <= 500)
            .WithMessage("Reason must be 3 to 500 characters.");
        RuleFor(x => x.ExpectedStatus)
            .Equal(AccountStatuses.Active)
            .WithMessage("Only an ACTIVE account can be suspended.");
    }
}

public sealed class SuspendPlatformAccountCommandHandler(
    IPlatformAdminContext actorContext,
    IPlatformAdministrationRepository repository)
    : IRequestHandler<SuspendPlatformAccountCommand, PlatformAccountDetailDto>
{
    public async Task<PlatformAccountDetailDto> Handle(
        SuspendPlatformAccountCommand request,
        CancellationToken cancellationToken)
    {
        var actor = await actorContext.RequireAsync(cancellationToken);
        if (request.UserId == actor.UserId)
        {
            throw new DomainRuleException(PlatformAdministrationMessages.AccountSelfAction);
        }

        var result = await repository.ChangeAccountStatusAsync(
            request.UserId,
            AccountStatuses.Suspended,
            request.ExpectedStatus,
            request.Reason.Trim(),
            actor.UserId,
            cancellationToken);
        return result.ToDtoOrThrow();
    }
}

public sealed record ReactivatePlatformAccountCommand(long UserId, string? Note, string ExpectedStatus)
    : IRequest<PlatformAccountDetailDto>;

public sealed class ReactivatePlatformAccountCommandValidator
    : AbstractValidator<ReactivatePlatformAccountCommand>
{
    public ReactivatePlatformAccountCommandValidator()
    {
        RuleFor(x => x.UserId).GreaterThan(0);
        RuleFor(x => x.Note).MaximumLength(500);
        RuleFor(x => x.ExpectedStatus)
            .Equal(AccountStatuses.Suspended)
            .WithMessage("Only a SUSPENDED account can be reactivated.");
    }
}

public sealed class ReactivatePlatformAccountCommandHandler(
    IPlatformAdminContext actorContext,
    IPlatformAdministrationRepository repository)
    : IRequestHandler<ReactivatePlatformAccountCommand, PlatformAccountDetailDto>
{
    public async Task<PlatformAccountDetailDto> Handle(
        ReactivatePlatformAccountCommand request,
        CancellationToken cancellationToken)
    {
        var actor = await actorContext.RequireAsync(cancellationToken);
        var result = await repository.ChangeAccountStatusAsync(
            request.UserId,
            AccountStatuses.Active,
            request.ExpectedStatus,
            string.IsNullOrWhiteSpace(request.Note) ? null : request.Note.Trim(),
            actor.UserId,
            cancellationToken);
        return result.ToDtoOrThrow();
    }
}

internal static class PlatformAccountMapping
{
    public static PlatformAccountDto ToDto(this PlatformAccountRow row) => new(
        row.UserId, row.FullName, PhoneMask.Mask(row.PhoneNumber), row.RoleCode, row.AccountStatus,
        row.CreatedAt, row.LastLoginAt, row.ActiveSessions, row.StorefrontCount);

    public static PlatformAccountDetailDto ToDto(this PlatformAccountDetailRow row) => new(
        row.Account.ToDto(),
        row.History.Select(e => new PlatformAccountEventDto(e.Action, e.ActorName, e.Reason, e.CreatedAt))
            .ToArray());

    public static PlatformAccountDetailDto ToDtoOrThrow(this AccountStatusChangeResult result) =>
        result.Outcome switch
        {
            AccountStatusChangeOutcome.Updated => result.Account!.ToDto(),
            AccountStatusChangeOutcome.NotFound => throw new NotFoundException(
                PlatformAdministrationMessages.AccountNotFound),
            AccountStatusChangeOutcome.RoleNotManageable => throw new ForbiddenException(
                PlatformAdministrationMessages.AccountRoleNotManageable),
            _ => throw new ConflictException(PlatformAdministrationMessages.AccountConflict),
        };
}
