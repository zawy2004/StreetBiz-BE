using System.Globalization;
using System.Linq.Expressions;
using FluentValidation;
using MediatR;
using StreetBiz.Application.Common.Exceptions;
using StreetBiz.Application.Common.Geo;
using StreetBiz.Application.Common.Interfaces;
using StreetBiz.Application.Common.Models;
using StreetBiz.Application.Common.Security;
using StreetBiz.Application.DTOs.Commerce;

namespace StreetBiz.Application.Features.Commerce;

// A customer's view of the marketplace: which wards have open storefronts (DISC-02), the storefronts
// themselves (DISC-03), search and filters (DISC-04/05) and one storefront in full (DISC-06).
// Distances are measured from the customer's position (DISC-01, detected on the client) when it is sent.

file static class DiscoveryRules
{
    /// <summary>Storefronts are few per ward, so distance and radius are refined in memory over this many candidates.</summary>
    public const int MaxStorefrontCandidates = 500;

    /// <summary>
    /// Vietnamese alphabetical order ("Đ" follows "D", not "Z"). Falls back to ordinal on hosts that
    /// run without culture data (invariant globalization), where names then sort by code point.
    /// </summary>
    public static readonly StringComparer NameOrder = CreateNameOrder();

    private static StringComparer CreateNameOrder()
    {
        try
        {
            return StringComparer.Create(CultureInfo.GetCultureInfo("vi-VN"), ignoreCase: true);
        }
        catch (CultureNotFoundException)
        {
            return StringComparer.OrdinalIgnoreCase;
        }
    }

    public static void PositionRules<T>(
        AbstractValidator<T> validator,
        Expression<Func<T, decimal?>> latitude,
        Expression<Func<T, decimal?>> longitude)
    {
        var lat = latitude.Compile();
        var lng = longitude.Compile();
        validator.RuleFor(x => x).Must(x => lat(x).HasValue == lng(x).HasValue)
            .WithMessage("Provide latitude and longitude together, or omit both.");
        validator.RuleFor(latitude).InclusiveBetween(-90, 90).When(x => lat(x).HasValue);
        validator.RuleFor(longitude).InclusiveBetween(-180, 180).When(x => lng(x).HasValue);
    }

    public static double? Distance(decimal? fromLat, decimal? fromLng, decimal toLat, decimal toLng) =>
        fromLat.HasValue
            ? GeoMath.DistanceMeters((double)fromLat.Value, (double)fromLng!.Value, (double)toLat, (double)toLng)
            : null;
}

public static class DiscoveryMappers
{
    public static StorefrontHourDto ToDto(this StorefrontHourRow row) =>
        new(row.DayOfWeek, row.OpensAt.ToString("HH:mm"), row.ClosesAt.ToString("HH:mm"));

    public static StorefrontSummaryDto ToSummaryDto(
        this MarketplaceStorefrontRow row,
        MarketplaceOpenAt now,
        double? distanceMeters) => new(
            row.StorefrontId,
            row.StorefrontName,
            row.Description,
            row.ImageUrl,
            row.VendorId,
            row.Address,
            row.WardId,
            row.WardName,
            row.ZoneName,
            row.SlotCode,
            row.Latitude,
            row.Longitude,
            distanceMeters,
            StorefrontHours.IsOpen(row.Hours, now),
            StorefrontHours.ForDay(row.Hours, now.DayOfWeek).Select(h => h.ToDto()).ToArray(),
            row.CommunityRating,
            row.CommunityCount,
            row.MenuItemCount,
            row.MinPrice,
            row.Categories);
}

public sealed record ListServiceAreasQuery(decimal? Latitude = null, decimal? Longitude = null)
    : IRequest<IReadOnlyList<ServiceAreaDto>>;

public sealed class ListServiceAreasQueryValidator : AbstractValidator<ListServiceAreasQuery>
{
    public ListServiceAreasQueryValidator() =>
        DiscoveryRules.PositionRules(this, x => x.Latitude, x => x.Longitude);
}

public sealed class ListServiceAreasQueryHandler(ICommerceRepository repository)
    : IRequestHandler<ListServiceAreasQuery, IReadOnlyList<ServiceAreaDto>>
{
    public async Task<IReadOnlyList<ServiceAreaDto>> Handle(
        ListServiceAreasQuery request,
        CancellationToken cancellationToken)
    {
        var locations = await repository.ListStorefrontLocationsAsync(cancellationToken);
        return locations
            .GroupBy(l => new { l.WardId, l.WardName, l.DistrictName })
            .Select(g => new ServiceAreaDto(
                g.Key.WardId,
                g.Key.WardName,
                g.Key.DistrictName,
                g.Count(),
                g.Min(l => DiscoveryRules.Distance(request.Latitude, request.Longitude, l.Latitude, l.Longitude))))
            .OrderBy(a => a.DistanceMeters ?? double.MaxValue)
            .ThenBy(a => a.WardName, DiscoveryRules.NameOrder)
            .ToArray();
    }
}

public sealed record ListMarketplaceCategoriesQuery : IRequest<IReadOnlyList<MarketplaceCategoryDto>>;

public sealed class ListMarketplaceCategoriesQueryHandler(ICommerceRepository repository)
    : IRequestHandler<ListMarketplaceCategoriesQuery, IReadOnlyList<MarketplaceCategoryDto>>
{
    public async Task<IReadOnlyList<MarketplaceCategoryDto>> Handle(
        ListMarketplaceCategoriesQuery request,
        CancellationToken cancellationToken) =>
        (await repository.ListMarketplaceCategoriesAsync(cancellationToken))
        .Select(c => new MarketplaceCategoryDto(c.CategoryId, c.CategoryName, c.ItemCount))
        .ToArray();
}

public sealed record ListStorefrontsQuery(
    string? Query = null,
    int? WardId = null,
    int? CategoryId = null,
    bool? OpenNow = null,
    decimal? Latitude = null,
    decimal? Longitude = null,
    double? RadiusMeters = null,
    string? Sort = null,
    int Take = 50) : IRequest<IReadOnlyList<StorefrontSummaryDto>>;

public sealed class ListStorefrontsQueryValidator : AbstractValidator<ListStorefrontsQuery>
{
    public ListStorefrontsQueryValidator()
    {
        RuleFor(x => x.Query).MaximumLength(100);
        RuleFor(x => x.WardId).GreaterThan(0).When(x => x.WardId.HasValue);
        RuleFor(x => x.CategoryId).GreaterThan(0).When(x => x.CategoryId.HasValue);
        DiscoveryRules.PositionRules(this, x => x.Latitude, x => x.Longitude);
        RuleFor(x => x.RadiusMeters).InclusiveBetween(1, 50_000).When(x => x.RadiusMeters.HasValue);
        RuleFor(x => x).Must(x => !x.RadiusMeters.HasValue || x.Latitude.HasValue)
            .WithMessage("radiusMeters needs latitude and longitude.");
        RuleFor(x => x.Sort).Must(sort => sort is null || MarketplaceStorefrontSorts.IsValid(sort))
            .WithMessage("sort must be distance, rating or name.");
        RuleFor(x => x).Must(x => x.Sort != MarketplaceStorefrontSorts.Distance || x.Latitude.HasValue)
            .WithMessage("Sorting by distance needs latitude and longitude.");
        RuleFor(x => x.Take).InclusiveBetween(1, 100);
    }
}

public sealed class ListStorefrontsQueryHandler(
    ICommerceRepository repository,
    IDateTimeProvider clock) : IRequestHandler<ListStorefrontsQuery, IReadOnlyList<StorefrontSummaryDto>>
{
    public async Task<IReadOnlyList<StorefrontSummaryDto>> Handle(
        ListStorefrontsQuery request,
        CancellationToken cancellationToken)
    {
        var now = StorefrontHours.LocalNow(clock.UtcNow);
        var hasPosition = request.Latitude.HasValue;
        GeoBoundingBox? area = hasPosition && request.RadiusMeters.HasValue
            ? GeoMath.BoundingBox((double)request.Latitude!.Value, (double)request.Longitude!.Value, request.RadiusMeters.Value)
            : null;

        var rows = await repository.ListStorefrontsAsync(
            new MarketplaceStorefrontFilter(
                string.IsNullOrWhiteSpace(request.Query) ? null : request.Query.Trim(),
                request.WardId,
                request.CategoryId,
                request.OpenNow == true ? now : null,
                area),
            DiscoveryRules.MaxStorefrontCandidates,
            cancellationToken);

        var candidates = rows
            .Select(row => (Row: row, Distance: DiscoveryRules.Distance(request.Latitude, request.Longitude, row.Latitude, row.Longitude)))
            // A bounding box also covers its corners, which lie outside the requested circle.
            .Where(x => !request.RadiusMeters.HasValue || x.Distance <= request.RadiusMeters);

        var sort = request.Sort ?? (hasPosition ? MarketplaceStorefrontSorts.Distance : MarketplaceStorefrontSorts.Name);
        IOrderedEnumerable<(MarketplaceStorefrontRow Row, double? Distance)> ordered = sort switch
        {
            MarketplaceStorefrontSorts.Distance => candidates.OrderBy(x => x.Distance),
            MarketplaceStorefrontSorts.Rating => candidates
                .OrderByDescending(x => x.Row.CommunityRating ?? -1m)
                .ThenByDescending(x => x.Row.CommunityCount),
            _ => candidates.OrderBy(x => x.Row.StorefrontName, DiscoveryRules.NameOrder),
        };

        return ordered
            .ThenBy(x => x.Row.StorefrontName, DiscoveryRules.NameOrder)
            .ThenBy(x => x.Row.StorefrontId)
            .Take(request.Take)
            .Select(x => x.Row.ToSummaryDto(now, x.Distance))
            .ToArray();
    }
}

public sealed record GetStorefrontQuery(long StorefrontId, decimal? Latitude = null, decimal? Longitude = null)
    : IRequest<StorefrontDetailDto>;

public sealed class GetStorefrontQueryValidator : AbstractValidator<GetStorefrontQuery>
{
    public GetStorefrontQueryValidator()
    {
        RuleFor(x => x.StorefrontId).GreaterThan(0);
        DiscoveryRules.PositionRules(this, x => x.Latitude, x => x.Longitude);
    }
}

public sealed class GetStorefrontQueryHandler(
    ICommerceRepository repository,
    IDateTimeProvider clock) : IRequestHandler<GetStorefrontQuery, StorefrontDetailDto>
{
    public async Task<StorefrontDetailDto> Handle(GetStorefrontQuery request, CancellationToken cancellationToken)
    {
        var detail = await repository.GetStorefrontAsync(request.StorefrontId, cancellationToken)
            ?? throw new NotFoundException(CommerceMessages.StorefrontNotFound);
        var row = detail.Storefront;
        var now = StorefrontHours.LocalNow(clock.UtcNow);

        return new StorefrontDetailDto(
            row.ToSummaryDto(now, DiscoveryRules.Distance(request.Latitude, request.Longitude, row.Latitude, row.Longitude)),
            row.Hours
                .OrderBy(h => h.DayOfWeek).ThenBy(h => h.OpensAt)
                .Select(h => h.ToDto())
                .ToArray(),
            detail.Items
                .GroupBy(i => new { i.CategoryId, i.CategoryName })
                .OrderBy(g => g.Key.CategoryName, DiscoveryRules.NameOrder)
                .Select(g => new StorefrontMenuCategoryDto(
                    g.Key.CategoryId,
                    g.Key.CategoryName,
                    g.OrderBy(i => i.ItemName, DiscoveryRules.NameOrder).Select(i => i.ToDto()).ToArray()))
                .ToArray());
    }
}
