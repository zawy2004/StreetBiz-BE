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

/// <param name="QrPayload">The scanned text: the bare signed token or a <c>/q/{token}</c> link.</param>
/// <param name="PrintedVersion">The version printed on the paper board (<c>?v=</c>); read from the
/// link itself when omitted.</param>
public sealed record VerifyPublicPermitCommand(
    string QrPayload,
    decimal? Latitude,
    decimal? Longitude,
    string? PhotoUrl,
    int? PrintedVersion = null) : IRequest<PermitVerificationDto>;

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
        RuleFor(x => x.PrintedVersion).GreaterThan(0).When(x => x.PrintedVersion.HasValue);
    }
}

/// <summary>
/// BUY-02 / BUY-06. Every answer is a live lookup (BR-39): the signature first, then
/// vw_PermitValidity, then the price board, each read now; nothing is cached and every scan is
/// logged. The price board is shown only for a VALID permit, whatever the storefront's own
/// open/paused/closed state: posting prices is a legal duty, not a marketplace feature.
/// </summary>
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
        var token = PermitQrText.ExtractToken(request.QrPayload);
        PublicPermitVerificationRow? permit = null;
        if (tokens.TryParse(token, out var claims))
        {
            permit = await vendors.GetPermitVerificationAsync(
                claims.ContractId,
                token,
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

        if (permit is null)
        {
            return new(false, result, null, null, null, null, null, null, null, null, null);
        }

        var isValid = permit.EffectiveStatus == PermitEffectiveStatuses.Valid;
        PublicPriceBoardDto? board = null;
        if (isValid && await vendors.GetPriceBoardByContractAsync(permit.ContractId, cancellationToken) is { } row)
        {
            var printed = request.PrintedVersion ?? PermitQrText.PrintedVersion(request.QrPayload);
            board = new PublicPriceBoardDto(
                row.Version,
                row.UpdatedAt is { } updated ? DateTime.SpecifyKind(updated, DateTimeKind.Utc) : null,
                printed is null ? null : printed == row.Version,
                row.Items.Select(item => item.ToDto()).ToArray());
        }

        return new(
            isValid,
            permit.EffectiveStatus,
            permit.PermitId,
            permit.VendorId,
            permit.DisplayName,
            permit.SlotId,
            permit.SlotCode,
            permit.Latitude,
            permit.Longitude,
            permit.StartDate,
            permit.EndDate,
            board);
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

/// <param name="ReportType">GENERAL, OVERCHARGE ("bị tính cao hơn giá niêm yết") or
/// PRICE_NOT_POSTED. Only an OVERCHARGE names a dish, a quantity, the amount charged and when.</param>
/// <param name="PurchasedAt">When the customer paid; must carry its UTC offset.</param>
public sealed record ReportSuspiciousVendorCommand(
    long VendorId,
    string? Reason,
    string? EvidenceUrl,
    long? SlotId,
    long? ScannedPermitId,
    string ReportType = VendorReportTypes.General,
    long? MenuItemId = null,
    int? Quantity = null,
    decimal? ChargedAmount = null,
    DateTimeOffset? PurchasedAt = null) : IRequest<VendorReportReceiptDto>;

public sealed class ReportSuspiciousVendorCommandValidator : AbstractValidator<ReportSuspiciousVendorCommand>
{
    public ReportSuspiciousVendorCommandValidator()
    {
        RuleFor(x => x.VendorId).GreaterThan(0);
        RuleFor(x => x.ReportType).Must(VendorReportTypes.IsValid)
            .WithMessage("ReportType must be GENERAL, OVERCHARGE or PRICE_NOT_POSTED.");
        // A typed complaint says what it is about by its type; a general one needs words.
        RuleFor(x => x.Reason).NotEmpty().When(x => x.ReportType == VendorReportTypes.General);
        RuleFor(x => x.Reason).MaximumLength(VendorReportRules.NoteMaxLength);
        RuleFor(x => x.EvidenceUrl).MaximumLength(500);
        RuleFor(x => x.SlotId).GreaterThan(0).When(x => x.SlotId.HasValue);
        RuleFor(x => x.ScannedPermitId).GreaterThan(0).When(x => x.ScannedPermitId.HasValue);

        When(x => x.ReportType == VendorReportTypes.Overcharge, () =>
        {
            RuleFor(x => x.MenuItemId).NotNull().GreaterThan(0);
            RuleFor(x => x.Quantity).NotNull().InclusiveBetween(1, VendorReportRules.MaxQuantity);
            RuleFor(x => x.ChargedAmount).NotNull()
                .InclusiveBetween(1, VendorReportRules.MaxChargedAmount)
                .Must(amount => amount is null || decimal.Truncate(amount.Value) == amount.Value)
                .WithMessage("ChargedAmount must be a whole number of VND.");
            RuleFor(x => x.PurchasedAt).NotNull();
        });
        When(x => x.ReportType != VendorReportTypes.Overcharge, () =>
            RuleFor(x => x)
                .Must(x => x.MenuItemId is null && x.Quantity is null && x.ChargedAmount is null && x.PurchasedAt is null)
                .WithMessage("Only an OVERCHARGE report names a dish, a quantity, an amount and a purchase time."));
    }
}

/// <summary>
/// BUY-05 / BUY-07. A customer account files the report (D3), so there is someone to send the result
/// to. For an overcharge the posted price is the server's: the MenuItemPriceHistory row in force when
/// the customer paid, times the quantity; nothing in the request can set it. A report never closes
/// itself and never leads to a penalty by itself (BR-41/43): the ward decides. It may be filed while
/// the vendor's permit is not valid, which is exactly when a customer is told to report.
/// </summary>
public sealed class ReportSuspiciousVendorCommandHandler(
    ICustomerContext customer,
    ICommunityVendorRepository vendors,
    VendorReportSettings settings,
    TimeProvider clock,
    StreetBiz.Application.Features.AiMarketplace.IVendorReportPreprocessor? preprocessor = null)
    : IRequestHandler<ReportSuspiciousVendorCommand, VendorReportReceiptDto>
{
    public async Task<VendorReportReceiptDto> Handle(
        ReportSuspiciousVendorCommand request,
        CancellationToken cancellationToken)
    {
        var customerUserId = await customer.RequireCustomerUserIdAsync(cancellationToken);
        var target = await vendors.GetReportTargetAsync(request.VendorId, request.ScannedPermitId, cancellationToken)
            ?? throw new NotFoundException(CommunityMessages.VendorNotFound);

        if (!await vendors.ReportReferencesBelongToVendorAsync(
                request.VendorId,
                request.SlotId,
                request.ScannedPermitId,
                cancellationToken))
        {
            throw new DomainRuleException(CommunityMessages.ReportReferenceMismatch);
        }

        if (await vendors.HasPendingReportAsync(customerUserId, request.VendorId, cancellationToken))
        {
            throw new ConflictException(CommunityMessages.DuplicatePendingReport);
        }

        var now = clock.GetUtcNow().UtcDateTime;
        PostedPriceRow? price = null;
        DateTime? purchasedAt = null;
        decimal? postedAmount = null;
        if (request.ReportType == VendorReportTypes.Overcharge)
        {
            purchasedAt = VendorReportRules.PurchaseTime(request.PurchasedAt!.Value, now);
            price = await vendors.FindPostedPriceAsync(
                    request.VendorId, request.MenuItemId!.Value, purchasedAt.Value, cancellationToken)
                ?? throw new NotFoundException(CommunityMessages.MenuItemNotFound);
            if (price.PriceHistoryId is null || price.UnitPrice is null || !price.IsListed)
            {
                throw new DomainRuleException(CommunityMessages.NotListedAtPurchase);
            }

            postedAmount = price.UnitPrice.Value * request.Quantity!.Value;
            if (request.ChargedAmount!.Value <= postedAmount)
            {
                throw new DomainRuleException(CommunityMessages.NotAbovePostedPrice);
            }
        }

        var dueAt = VendorReportRules.DueAt(BusinessCalendar.Today(clock), settings.DueWorkingDays);
        var vendorName = price?.StorefrontName ?? target.DisplayName;
        var slotCode = price?.SlotCode ?? target.SlotCode;
        var reason = string.IsNullOrWhiteSpace(request.Reason)
            ? DefaultReason(request.ReportType, price, request.Quantity)
            : request.Reason.Trim();
        var notices = VendorReportNotices.ForNewReport(
            request.ReportType, vendorName, slotCode, dueAt, price, request.Quantity, request.ChargedAmount, postedAmount);

        var reportId = await vendors.AddReportAsync(
            request.VendorId,
            customerUserId,
            new CustomerVendorReport(
                reason,
                string.IsNullOrWhiteSpace(request.EvidenceUrl) ? null : request.EvidenceUrl.Trim(),
                request.SlotId,
                request.ScannedPermitId,
                request.ReportType,
                price?.MenuItemId,
                price?.PriceHistoryId,
                price is null ? null : request.Quantity,
                postedAmount,
                price is null ? null : request.ChargedAmount,
                purchasedAt,
                dueAt),
            notices,
            cancellationToken);
        // AIB-01: the photo is read in the background so the ward officer finds the notes ready; never awaited.
        preprocessor?.Enqueue(reportId);
        return new(reportId, VendorReportStatuses.Pending, request.ReportType, dueAt, postedAmount,
            price is null ? null : request.ChargedAmount);
    }

    private static string DefaultReason(string reportType, PostedPriceRow? price, int? quantity) => reportType switch
    {
        VendorReportTypes.Overcharge when price is not null =>
            $"Bị tính cao hơn giá niêm yết: {quantity} {price.PriceUnit} {price.ItemName}.",
        VendorReportTypes.PriceNotPosted => "Quầy không treo bảng giá.",
        _ => "Phản ánh về quầy.",
    };
}

/// <summary>"Phản ánh của tôi": the calling customer's own reports, newest first, with their outcome.</summary>
public sealed record ListMyVendorReportsQuery(int Page = 1) : IRequest<IReadOnlyList<MyVendorReportDto>>;

public sealed class ListMyVendorReportsQueryValidator : AbstractValidator<ListMyVendorReportsQuery>
{
    public ListMyVendorReportsQueryValidator() => RuleFor(x => x.Page).InclusiveBetween(1, 10_000);
}

public sealed class ListMyVendorReportsQueryHandler(
    ICustomerContext customer,
    ICommunityVendorRepository vendors,
    TimeProvider clock) : IRequestHandler<ListMyVendorReportsQuery, IReadOnlyList<MyVendorReportDto>>
{
    public const int PageSize = 20;

    public async Task<IReadOnlyList<MyVendorReportDto>> Handle(
        ListMyVendorReportsQuery request,
        CancellationToken cancellationToken)
    {
        var customerUserId = await customer.RequireCustomerUserIdAsync(cancellationToken);
        var now = clock.GetUtcNow().UtcDateTime;
        var rows = await vendors.ListMyReportsAsync(customerUserId, request.Page, PageSize, cancellationToken);
        return rows.Select(row => new MyVendorReportDto(
                row.ReportId,
                row.VendorId,
                row.VendorName,
                row.SlotCode,
                row.ReportType,
                row.Reason,
                row.Status,
                Utc(row.CreatedAt),
                row.DueAt is { } due ? Utc(due) : null,
                VendorReportRules.IsOverdue(row.Status, row.DueAt, now),
                row.Resolution,
                row.ResolutionNote,
                row.RefundAmount,
                row.ReviewedAt is { } reviewed ? Utc(reviewed) : null,
                row.HasPhoto,
                row.PriceEvidence?.ToDto()))
            .ToArray();
    }

    private static DateTime Utc(DateTime value) => DateTime.SpecifyKind(value, DateTimeKind.Utc);
}

internal static class CommunityVendorMapping
{
    public static PriceBoardItemDto ToDto(this PriceBoardItemRow row) => new(
        row.MenuItemId,
        row.Name,
        row.NameEn,
        row.PriceUnit,
        row.UnitPrice);

    public static VendorReportPriceEvidenceDto ToDto(this VendorReportPriceEvidenceRow row) => new(
        row.MenuItemId,
        row.ItemName,
        row.PriceUnit,
        row.PostedUnitPrice,
        row.PriceVersion,
        DateTime.SpecifyKind(row.PriceVersionAt, DateTimeKind.Utc),
        row.Quantity,
        row.PostedAmount,
        row.ChargedAmount,
        row.ChargedAmount - row.PostedAmount,
        DateTime.SpecifyKind(row.PurchasedAt, DateTimeKind.Utc));

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
