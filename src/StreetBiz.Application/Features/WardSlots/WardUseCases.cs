using FluentValidation;
using MediatR;
using StreetBiz.Application.Common.Exceptions;
using StreetBiz.Application.Common.Interfaces;
using StreetBiz.Application.Common.Security;

namespace StreetBiz.Application.Features.WardSlots;

public sealed record WardProfile(string UserId, int WardId, string Name);

public interface IWardActorContext
{
    Task<WardActor> RequireAsync(CancellationToken cancellationToken);
}

public sealed class WardActorResolver(IUserAccountRepository users) : IWardActorResolver
{
    public async Task<WardActor?> ResolveAsync(
        long userId,
        CancellationToken cancellationToken)
    {
        var user = await users.GetByIdAsync(userId, cancellationToken);
        if (user is null
            || user.RoleCode != RoleCodes.WardAuthority
            || user.AccountStatus != AccountStatuses.Active
            || user.WardUnitId is null)
        {
            return null;
        }

        return new WardActor(
            user.Id,
            user.WardUnitId.Value,
            user.FullName ?? $"Cán bộ #{user.Id}");
    }
}

public sealed class WardActorContext(
    ICurrentUser currentUser,
    IWardActorResolver actors) : IWardActorContext
{
    public async Task<WardActor> RequireAsync(CancellationToken cancellationToken)
    {
        var userId = currentUser.UserId
            ?? throw new AuthenticationException(AppMessages.SessionExpired);

        return await actors.ResolveAsync(userId, cancellationToken)
            ?? throw new ForbiddenException("Tài khoản hiện tại không phải cán bộ phường.");
    }
}

public sealed record GetWardProfileQuery : IRequest<WardProfile>;

public sealed class GetWardProfileQueryHandler(IWardActorContext actorContext)
    : IRequestHandler<GetWardProfileQuery, WardProfile>
{
    public async Task<WardProfile> Handle(
        GetWardProfileQuery request,
        CancellationToken cancellationToken)
    {
        var actor = await actorContext.RequireAsync(cancellationToken);
        return new WardProfile(actor.UserId.ToString(System.Globalization.CultureInfo.InvariantCulture),
            actor.WardId, actor.Name);
    }
}

public sealed record ListWardCasesQuery(string Kind, int Page) : IRequest<CasePage>;

public sealed class ListWardCasesQueryValidator : AbstractValidator<ListWardCasesQuery>
{
    public ListWardCasesQueryValidator()
    {
        RuleFor(x => x.Kind).Must(WardCaseKinds.IsValid)
            .WithMessage("Loại hồ sơ không hợp lệ.");
        RuleFor(x => x.Page).InclusiveBetween(1, 10_000);
    }
}

public sealed class ListWardCasesQueryHandler(
    IWardActorContext actorContext,
    IWardSlots wardSlots) : IRequestHandler<ListWardCasesQuery, CasePage>
{
    public async Task<CasePage> Handle(
        ListWardCasesQuery request,
        CancellationToken cancellationToken)
    {
        var actor = await actorContext.RequireAsync(cancellationToken);
        return await wardSlots.ListAsync(actor, request.Kind, request.Page, cancellationToken);
    }
}

public sealed record GetWardCaseQuery(string Kind, long Id) : IRequest<WardCase>;

public sealed class GetWardCaseQueryValidator : AbstractValidator<GetWardCaseQuery>
{
    public GetWardCaseQueryValidator()
    {
        RuleFor(x => x.Kind).Must(WardCaseKinds.IsValid)
            .WithMessage("Loại hồ sơ không hợp lệ.");
        RuleFor(x => x.Id).GreaterThan(0);
    }
}

public sealed class GetWardCaseQueryHandler(
    IWardActorContext actorContext,
    IWardSlots wardSlots) : IRequestHandler<GetWardCaseQuery, WardCase>
{
    public async Task<WardCase> Handle(
        GetWardCaseQuery request,
        CancellationToken cancellationToken)
    {
        var actor = await actorContext.RequireAsync(cancellationToken);
        return await wardSlots.GetAsync(actor, request.Kind, request.Id, cancellationToken);
    }
}

public sealed record DecideWardCaseCommand(
    string Kind,
    long Id,
    string Decision,
    string Reason,
    string ExpectedStatus) : IRequest<WardCase>;

public sealed class DecideWardCaseCommandValidator : AbstractValidator<DecideWardCaseCommand>
{
    public DecideWardCaseCommandValidator()
    {
        RuleFor(x => x.Kind).Must(WardCaseKinds.IsValid)
            .WithMessage("Loại hồ sơ không hợp lệ.");
        RuleFor(x => x.Id).GreaterThan(0);
        RuleFor(x => x.Reason).NotEmpty().MaximumLength(500);
        RuleFor(x => x.ExpectedStatus).NotEmpty().MaximumLength(30);
        RuleFor(x => x).Must(x => WardCaseKinds.SupportsDecision(x.Kind, x.Decision))
            .WithMessage("Quyết định không áp dụng cho loại hồ sơ này.");
    }
}

public sealed class DecideWardCaseCommandHandler(
    IWardActorContext actorContext,
    IWardSlots wardSlots) : IRequestHandler<DecideWardCaseCommand, WardCase>
{
    public async Task<WardCase> Handle(
        DecideWardCaseCommand request,
        CancellationToken cancellationToken)
    {
        var actor = await actorContext.RequireAsync(cancellationToken);
        var decision = new ReviewDecision(
            request.Decision.ToUpperInvariant(),
            request.Reason.Trim(),
            request.ExpectedStatus);
        return await wardSlots.DecideAsync(actor, request.Kind, request.Id, decision, cancellationToken);
    }
}

public sealed record PinWardProposalCommand(long Id, double Latitude, double Longitude)
    : IRequest<WardCase>;

public sealed class PinWardProposalCommandValidator : AbstractValidator<PinWardProposalCommand>
{
    public PinWardProposalCommandValidator()
    {
        RuleFor(x => x.Id).GreaterThan(0);
        RuleFor(x => x.Latitude).Must(double.IsFinite).InclusiveBetween(-90, 90);
        RuleFor(x => x.Longitude).Must(double.IsFinite).InclusiveBetween(-180, 180);
    }
}

public sealed class PinWardProposalCommandHandler(
    IWardActorContext actorContext,
    IWardSlots wardSlots) : IRequestHandler<PinWardProposalCommand, WardCase>
{
    public async Task<WardCase> Handle(
        PinWardProposalCommand request,
        CancellationToken cancellationToken)
    {
        var actor = await actorContext.RequireAsync(cancellationToken);
        return await wardSlots.PinAsync(
            actor,
            request.Id,
            new GeoPoint(request.Latitude, request.Longitude),
            cancellationToken);
    }
}

public sealed record SearchWardLocationsQuery(string Address)
    : IRequest<IReadOnlyList<GeocodeResult>>;

public sealed class SearchWardLocationsQueryValidator : AbstractValidator<SearchWardLocationsQuery>
{
    public SearchWardLocationsQueryValidator() =>
        RuleFor(x => x.Address).NotEmpty().MaximumLength(500);
}

public sealed class SearchWardLocationsQueryHandler(
    IWardActorContext actorContext,
    IGeocodingService geocoding)
    : IRequestHandler<SearchWardLocationsQuery, IReadOnlyList<GeocodeResult>>
{
    public async Task<IReadOnlyList<GeocodeResult>> Handle(
        SearchWardLocationsQuery request,
        CancellationToken cancellationToken)
    {
        await actorContext.RequireAsync(cancellationToken);
        var candidates = await geocoding.SearchAsync(request.Address.Trim(), 5, cancellationToken);
        return candidates.Select(point => new GeocodeResult(
            point.DisplayName ?? $"{point.Latitude}, {point.Longitude}",
            new GeoPoint((double)point.Latitude, (double)point.Longitude))).ToArray();
    }
}

public sealed record VerifyWardLocationQuery(double Latitude, double Longitude)
    : IRequest<GeofenceResult>;

public sealed class VerifyWardLocationQueryValidator : AbstractValidator<VerifyWardLocationQuery>
{
    public VerifyWardLocationQueryValidator()
    {
        RuleFor(x => x.Latitude).Must(double.IsFinite).InclusiveBetween(-90, 90);
        RuleFor(x => x.Longitude).Must(double.IsFinite).InclusiveBetween(-180, 180);
    }
}

public sealed class VerifyWardLocationQueryHandler(
    IWardActorContext actorContext,
    IGeolocation geolocation) : IRequestHandler<VerifyWardLocationQuery, GeofenceResult>
{
    public async Task<GeofenceResult> Handle(
        VerifyWardLocationQuery request,
        CancellationToken cancellationToken)
    {
        var actor = await actorContext.RequireAsync(cancellationToken);
        return geolocation.Verify(actor.WardId, new GeoPoint(request.Latitude, request.Longitude));
    }
}

public static class WardCaseKinds
{
    public const string Proposals = "proposals";
    public const string Conflicts = "conflicts";
    public const string Transfers = "transfers";

    // Business-registration review deliberately does NOT live here: it goes through
    // WardComplianceController/WardComplianceService's dedicated /ward/enrollments
    // endpoints instead, which enforce the BR-41 identity-verification gate
    // (ConfirmIdentityAsync must run before APPROVE). A registrations case-kind was
    // briefly added here in parallel and has been removed to avoid a second,
    // gate-less path to approve a BusinessRegistration.

    public static bool IsValid(string kind) =>
        kind is Proposals or Conflicts or Transfers;

    public static bool SupportsDecision(string kind, string decision)
    {
        var normalized = decision?.ToUpperInvariant();
        return kind switch
        {
            Proposals or Transfers => normalized is "APPROVE" or "REJECT",
            Conflicts => normalized is "QUEUE" or "REJECT",
            _ => false,
        };
    }
}
