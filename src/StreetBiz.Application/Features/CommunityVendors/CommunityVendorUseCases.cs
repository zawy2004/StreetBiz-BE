using FluentValidation;
using MediatR;
using StreetBiz.Application.Common.Exceptions;
using StreetBiz.Application.Common.Geo;
using StreetBiz.Application.Common.Interfaces;
using StreetBiz.Application.Common.Models;
using StreetBiz.Application.Common.Security;
using StreetBiz.Application.DTOs.Community;

namespace StreetBiz.Application.Features.CommunityVendors;

public sealed record SearchActiveVendorsQuery(
    decimal? Latitude,
    decimal? Longitude,
    double? RadiusMeters,
    int Take = 100) : IRequest<IReadOnlyList<ActiveVendorLocationDto>>;

public sealed class SearchActiveVendorsQueryValidator : AbstractValidator<SearchActiveVendorsQuery>
{
    public SearchActiveVendorsQueryValidator()
    {
        RuleFor(x => x).Must(query =>
                query.Latitude.HasValue == query.Longitude.HasValue
                && query.Latitude.HasValue == query.RadiusMeters.HasValue)
            .WithMessage("Provide latitude, longitude and radiusMeters together, or omit all three.");
        RuleFor(x => x.Latitude).InclusiveBetween(-90, 90).When(x => x.Latitude.HasValue);
        RuleFor(x => x.Longitude).InclusiveBetween(-180, 180).When(x => x.Longitude.HasValue);
        RuleFor(x => x.RadiusMeters).InclusiveBetween(1, 50_000).When(x => x.RadiusMeters.HasValue);
        RuleFor(x => x.Take).InclusiveBetween(1, 200);
    }
}

public sealed class SearchActiveVendorsQueryHandler(ICommunityVendorRepository vendors)
    : IRequestHandler<SearchActiveVendorsQuery, IReadOnlyList<ActiveVendorLocationDto>>
{
    public async Task<IReadOnlyList<ActiveVendorLocationDto>> Handle(
        SearchActiveVendorsQuery request,
        CancellationToken cancellationToken)
    {
        CommunityVendorSearchArea area;
        if (request.Latitude.HasValue)
        {
            var box = GeoMath.BoundingBox(
                (double)request.Latitude.Value,
                (double)request.Longitude!.Value,
                request.RadiusMeters!.Value);
            area = new((decimal)box.MinLat, (decimal)box.MaxLat, (decimal)box.MinLon, (decimal)box.MaxLon);
        }
        else
        {
            area = new(null, null, null, null);
        }

        // A bounding box includes its four corner areas outside the requested circle.
        // Fetch extra candidates before the exact Haversine filter so those corner rows
        // cannot crowd valid nearby vendors out of the requested result page.
        var candidateTake = request.Latitude.HasValue
            ? Math.Min(request.Take * 5, 1_000)
            : request.Take;
        var rows = await vendors.SearchActiveAsync(area, candidateTake, cancellationToken);
        return rows
            .Select(row => (Row: row, Distance: Distance(request, row)))
            .Where(item => item.Distance is null || item.Distance <= request.RadiusMeters)
            .OrderBy(item => item.Distance ?? double.MaxValue)
            .Take(request.Take)
            .Select(item => item.Row.ToDto(item.Distance))
            .ToArray();
    }

    private static double? Distance(SearchActiveVendorsQuery query, ActiveVendorLocationRow row) =>
        query.Latitude.HasValue
            ? GeoMath.DistanceMeters(
                (double)query.Latitude.Value,
                (double)query.Longitude!.Value,
                (double)row.Latitude,
                (double)row.Longitude)
            : null;
}

public sealed record GetPublicVendorProfileQuery(long VendorId)
    : IRequest<PublicVendorProfileDto>;

public sealed class GetPublicVendorProfileQueryValidator : AbstractValidator<GetPublicVendorProfileQuery>
{
    public GetPublicVendorProfileQueryValidator() => RuleFor(x => x.VendorId).GreaterThan(0);
}

public sealed class GetPublicVendorProfileQueryHandler(ICommunityVendorRepository vendors)
    : IRequestHandler<GetPublicVendorProfileQuery, PublicVendorProfileDto>
{
    public async Task<PublicVendorProfileDto> Handle(
        GetPublicVendorProfileQuery request,
        CancellationToken cancellationToken)
    {
        var profile = await vendors.GetPublicProfileAsync(request.VendorId, cancellationToken)
            ?? throw new NotFoundException(CommunityMessages.VendorNotFound);
        var comments = await vendors.ListCommentsAsync(request.VendorId, 50, cancellationToken);
        return profile.ToDto(comments.Select(comment => comment.ToDto()).ToArray());
    }
}

public sealed record VerifyPublicPermitCommand(
    string QrPayload,
    decimal? Latitude,
    decimal? Longitude,
    string? PhotoUrl) : IRequest<PermitVerificationDto>;

public sealed class VerifyPublicPermitCommandValidator : AbstractValidator<VerifyPublicPermitCommand>
{
    public VerifyPublicPermitCommandValidator()
    {
        RuleFor(x => x.QrPayload).NotEmpty().MaximumLength(500);
        RuleFor(x => x).Must(command => command.Latitude.HasValue == command.Longitude.HasValue)
            .WithMessage("Latitude and longitude must be supplied together.");
        RuleFor(x => x.Latitude).InclusiveBetween(-90, 90).When(x => x.Latitude.HasValue);
        RuleFor(x => x.Longitude).InclusiveBetween(-180, 180).When(x => x.Longitude.HasValue);
        RuleFor(x => x.PhotoUrl).MaximumLength(500);
    }
}

public sealed class VerifyPublicPermitCommandHandler(
    ICommunityVendorRepository vendors,
    IPermitTokenService tokens,
    ICurrentUser currentUser)
    : IRequestHandler<VerifyPublicPermitCommand, PermitVerificationDto>
{
    public async Task<PermitVerificationDto> Handle(
        VerifyPublicPermitCommand request,
        CancellationToken cancellationToken)
    {
        PublicPermitVerificationRow? permit = null;
        if (tokens.TryParse(request.QrPayload, out var claims))
        {
            permit = await vendors.GetPermitVerificationAsync(
                claims.ContractId,
                request.QrPayload,
                cancellationToken);
        }

        var result = permit?.EffectiveStatus ?? PermitScanResults.NotFound;
        await vendors.AddPermitScanAsync(new PublicPermitScan(
            permit?.PermitId,
            request.QrPayload,
            currentUser.UserId,
            result,
            request.Latitude,
            request.Longitude,
            request.PhotoUrl), cancellationToken);

        return permit is null
            ? new(false, result, null, null, null, null, null, null, null, null, null)
            : new(
                permit.EffectiveStatus == PermitEffectiveStatuses.Valid,
                permit.EffectiveStatus,
                permit.PermitId,
                permit.VendorId,
                permit.DisplayName,
                permit.SlotId,
                permit.SlotCode,
                permit.Latitude,
                permit.Longitude,
                permit.StartDate,
                permit.EndDate);
    }
}

public sealed record UpsertVendorCommentCommand(
    long VendorId,
    short Rating,
    string CommentText) : IRequest<VendorCommentDto>;

public sealed class UpsertVendorCommentCommandValidator : AbstractValidator<UpsertVendorCommentCommand>
{
    public UpsertVendorCommentCommandValidator()
    {
        RuleFor(x => x.VendorId).GreaterThan(0);
        RuleFor(x => x.Rating).InclusiveBetween((short)1, (short)5);
        RuleFor(x => x.CommentText).NotEmpty().MaximumLength(1000);
    }
}

public sealed class UpsertVendorCommentCommandHandler(
    ICustomerContext customer,
    ICommunityVendorRepository vendors)
    : IRequestHandler<UpsertVendorCommentCommand, VendorCommentDto>
{
    public async Task<VendorCommentDto> Handle(
        UpsertVendorCommentCommand request,
        CancellationToken cancellationToken)
    {
        var customerUserId = await customer.RequireCustomerUserIdAsync(cancellationToken);
        _ = await vendors.GetPublicProfileAsync(request.VendorId, cancellationToken)
            ?? throw new NotFoundException(CommunityMessages.VendorNotFound);
        var comment = await vendors.UpsertCommentAsync(
            request.VendorId,
            customerUserId,
            new CustomerVendorComment(request.Rating, request.CommentText.Trim()),
            cancellationToken);
        return comment.ToDto();
    }
}

public sealed record ReportSuspiciousVendorCommand(
    long VendorId,
    string Reason,
    string? EvidenceUrl,
    long? SlotId,
    long? ScannedPermitId) : IRequest<VendorReportReceiptDto>;

public sealed class ReportSuspiciousVendorCommandValidator : AbstractValidator<ReportSuspiciousVendorCommand>
{
    public ReportSuspiciousVendorCommandValidator()
    {
        RuleFor(x => x.VendorId).GreaterThan(0);
        RuleFor(x => x.Reason).NotEmpty().MaximumLength(500);
        RuleFor(x => x.EvidenceUrl).MaximumLength(500);
        RuleFor(x => x.SlotId).GreaterThan(0).When(x => x.SlotId.HasValue);
        RuleFor(x => x.ScannedPermitId).GreaterThan(0).When(x => x.ScannedPermitId.HasValue);
    }
}

public sealed class ReportSuspiciousVendorCommandHandler(
    ICustomerContext customer,
    ICommunityVendorRepository vendors)
    : IRequestHandler<ReportSuspiciousVendorCommand, VendorReportReceiptDto>
{
    public async Task<VendorReportReceiptDto> Handle(
        ReportSuspiciousVendorCommand request,
        CancellationToken cancellationToken)
    {
        var customerUserId = await customer.RequireCustomerUserIdAsync(cancellationToken);
        _ = await vendors.GetPublicProfileAsync(request.VendorId, cancellationToken)
            ?? throw new NotFoundException(CommunityMessages.VendorNotFound);

        if (!await vendors.ReportReferencesBelongToVendorAsync(
                request.VendorId,
                request.SlotId,
                request.ScannedPermitId,
                cancellationToken))
        {
            throw new DomainRuleException(CommunityMessages.ReportReferenceMismatch);
        }

        var reportId = await vendors.AddReportAsync(
            request.VendorId,
            customerUserId,
            new CustomerVendorReport(
                request.Reason.Trim(),
                string.IsNullOrWhiteSpace(request.EvidenceUrl) ? null : request.EvidenceUrl.Trim(),
                request.SlotId,
                request.ScannedPermitId),
            cancellationToken);
        return new(reportId, VendorReportStatuses.Pending);
    }
}

internal static class CommunityVendorMapping
{
    public static ActiveVendorLocationDto ToDto(this ActiveVendorLocationRow row, double? distance) => new(
        row.VendorId,
        row.DisplayName,
        row.VendorType,
        row.DeclaredAddress,
        row.PermitId,
        row.PermitEndDate,
        row.SlotId,
        row.SlotCode,
        row.ZoneName,
        row.Latitude,
        row.Longitude,
        distance,
        row.CommunityRating,
        row.CommunityCount,
        row.VerifiedRating,
        row.VerifiedCount);

    public static VendorCommentDto ToDto(this PublicVendorCommentRow row) => new(
        row.CommentId,
        row.AuthorName,
        row.Rating,
        row.CommentText,
        row.CreatedAt);

    public static PublicVendorProfileDto ToDto(
        this PublicVendorProfileRow row,
        IReadOnlyList<VendorCommentDto> comments) => new(
            row.VendorId,
            row.DisplayName,
            row.VendorType,
            row.DeclaredAddress,
            row.WardId,
            row.WardName,
            row.PermitId,
            row.PermitStatus,
            row.PermitEndDate,
            row.SlotId,
            row.SlotCode,
            row.ZoneName,
            row.Latitude,
            row.Longitude,
            row.CommunityRating,
            row.CommunityCount,
            row.VerifiedRating,
            row.VerifiedCount,
            comments);
}
