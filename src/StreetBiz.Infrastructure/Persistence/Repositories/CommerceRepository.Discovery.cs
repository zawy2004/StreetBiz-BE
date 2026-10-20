using Microsoft.EntityFrameworkCore;
using StreetBiz.Application.Common.Models;
using StreetBiz.Application.Common.Security;
using StreetBiz.Infrastructure.Persistence.ScaffoldedModels;

namespace StreetBiz.Infrastructure.Persistence.Repositories;

// Read models for customer discovery (DISC-02..06). Every read starts from PublicStorefronts(), so the
// storefront list, its counts and the menu-item search can never disagree about what is visible.
public sealed partial class CommerceRepository
{
    /// <summary>
    /// A storefront is publicly listed only while an order could be placed with it: EligibleStores() (switched on,
    /// approved registration, contract active and inside its dates) and, on top of that, an active vendor account.
    /// Listing and ordering share that one rule, so a customer is never shown a storefront that then refuses the order.
    /// </summary>
    private IQueryable<Storefront> PublicStorefronts() =>
        EligibleStores().AsNoTracking().Where(storefront =>
            storefront.contract.vendor.user.account_status == AccountStatuses.Active);

    /// <summary>
    /// Vietnamese is often typed without diacritics ("bun cha" for "Bún chả"), so text search compares
    /// accent-insensitively on SQL Server. SQLite, used only by tests, has no such collation and keeps a plain comparison.
    /// </summary>
    private string TextCollation => db.Database.IsSqlServer() ? "Latin1_General_100_CI_AI" : "BINARY";

    private IQueryable<Storefront> StorefrontsWhere(int? wardId, MarketplaceOpenAt? openAt)
    {
        var storefronts = PublicStorefronts();
        if (wardId is { } ward)
        {
            storefronts = storefronts.Where(storefront => storefront.contract.slot.zone.ward_unit_id == ward);
        }

        if (openAt is not null)
        {
            var day = openAt.DayOfWeek;
            var time = openAt.Time;
            // Same rule as StorefrontHours.IsOpen: no hours published means "governed by the open switch".
            storefronts = storefronts.Where(storefront =>
                !storefront.StorefrontBusinessHours.Any()
                || storefront.StorefrontBusinessHours.Any(hour =>
                    hour.day_of_week == day && hour.opens_at <= time && hour.closes_at > time));
        }

        return storefronts;
    }

    public async Task<IReadOnlyList<MarketplaceStorefrontRow>> ListStorefrontsAsync(
        MarketplaceStorefrontFilter filter,
        int take,
        CancellationToken cancellationToken)
    {
        var storefronts = StorefrontsWhere(filter.WardId, filter.OpenAt);
        if (filter.CategoryId is { } categoryId)
        {
            var inCategory = MarketplaceMenuQuery()
                .Where(item => item.category_id == categoryId)
                .Select(item => item.storefront_id);
            storefronts = storefronts.Where(storefront => inCategory.Contains(storefront.storefront_id));
        }

        if (filter.Query is { } query)
        {
            var collation = TextCollation;
            var itemHits = MarketplaceMenuQuery()
                .Where(item => EF.Functions.Collate(item.item_name, collation).Contains(query)
                    || EF.Functions.Collate(item.category.category_name, collation).Contains(query))
                .Select(item => item.storefront_id);
            storefronts = storefronts.Where(storefront =>
                EF.Functions.Collate(storefront.storefront_name, collation).Contains(query)
                || EF.Functions.Collate(storefront.description!, collation).Contains(query)
                || itemHits.Contains(storefront.storefront_id));
        }

        if (filter.Area is { } area)
        {
            var (minLat, maxLat, minLon, maxLon) =
                ((decimal)area.MinLat, (decimal)area.MaxLat, (decimal)area.MinLon, (decimal)area.MaxLon);
            storefronts = storefronts.Where(storefront =>
                storefront.contract.slot.latitude >= minLat
                && storefront.contract.slot.latitude <= maxLat
                && storefront.contract.slot.longitude >= minLon
                && storefront.contract.slot.longitude <= maxLon);
        }

        return await LoadStorefrontRowsAsync(
            storefronts.OrderBy(storefront => storefront.storefront_id).Take(take),
            cancellationToken);
    }

    public async Task<MarketplaceStorefrontDetailRow?> GetStorefrontAsync(
        long storefrontId,
        CancellationToken cancellationToken)
    {
        var rows = await LoadStorefrontRowsAsync(
            PublicStorefronts().Where(storefront => storefront.storefront_id == storefrontId),
            cancellationToken);
        if (rows.Count == 0)
        {
            return null;
        }

        var items = await ProjectMarketplaceMenuItems(MarketplaceMenuQuery()
                .Where(item => item.storefront_id == storefrontId)
                .OrderBy(item => item.category.category_name)
                .ThenBy(item => item.item_name)
                .ThenBy(item => item.menu_item_id))
            .ToListAsync(cancellationToken);
        return new MarketplaceStorefrontDetailRow(rows[0], items);
    }

    public async Task<IReadOnlyList<StorefrontLocationRow>> ListStorefrontLocationsAsync(
        CancellationToken cancellationToken) =>
        await PublicStorefronts()
            .Select(storefront => new StorefrontLocationRow(
                storefront.contract.slot.zone.ward_unit_id,
                storefront.contract.slot.zone.AdministrativeUnit!.unit_name,
                storefront.contract.slot.zone.AdministrativeUnit!.parent_unit!.unit_name,
                storefront.contract.slot.latitude,
                storefront.contract.slot.longitude))
            .ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<MarketplaceCategoryRow>> ListMarketplaceCategoriesAsync(
        CancellationToken cancellationToken) =>
        await MarketplaceMenuQuery()
            .GroupBy(item => new { item.category_id, item.category.category_name })
            .OrderBy(group => group.Key.category_name)
            .Select(group => new MarketplaceCategoryRow(group.Key.category_id, group.Key.category_name, group.Count()))
            .ToListAsync(cancellationToken);

    /// <summary>Loads the storefronts plus their rating, menu summary and opening hours in three round trips.</summary>
    private async Task<IReadOnlyList<MarketplaceStorefrontRow>> LoadStorefrontRowsAsync(
        IQueryable<Storefront> storefronts,
        CancellationToken cancellationToken)
    {
        var heads = await (
            from storefront in storefronts
            join ratingValue in db.vw_VendorRatings.AsNoTracking()
                on storefront.registration.vendor_id equals ratingValue.vendor_id into ratingValues
            from rating in ratingValues.DefaultIfEmpty()
            select new
            {
                storefront.storefront_id,
                storefront.storefront_name,
                storefront.description,
                storefront.image_url,
                VendorId = storefront.registration.vendor_id,
                Address = storefront.registration.declared_address,
                WardId = storefront.contract.slot.zone.ward_unit_id,
                WardName = storefront.contract.slot.zone.AdministrativeUnit!.unit_name,
                ZoneName = storefront.contract.slot.zone.zone_name,
                SlotCode = storefront.contract.slot.slot_code,
                storefront.contract.slot.latitude,
                storefront.contract.slot.longitude,
                CommunityRating = rating.community_rating,
                CommunityCount = (int?)rating.community_count,
            }).ToListAsync(cancellationToken);
        if (heads.Count == 0)
        {
            return [];
        }

        var ids = heads.Select(head => head.storefront_id).ToArray();
        var items = (await MarketplaceMenuQuery()
                .Where(item => ids.Contains(item.storefront_id))
                .Select(item => new { item.storefront_id, item.unit_price, item.category.category_name })
                .ToListAsync(cancellationToken))
            .ToLookup(item => item.storefront_id);
        var hours = (await db.StorefrontBusinessHours.AsNoTracking()
                .Where(hour => ids.Contains(hour.storefront_id))
                .Select(hour => new { hour.storefront_id, hour.day_of_week, hour.opens_at, hour.closes_at })
                .ToListAsync(cancellationToken))
            .ToLookup(hour => hour.storefront_id);

        return heads
            .OrderBy(head => head.storefront_id)
            .Select(head =>
            {
                var menu = items[head.storefront_id].ToArray();
                return new MarketplaceStorefrontRow(
                    head.storefront_id,
                    head.storefront_name,
                    head.description,
                    head.image_url,
                    head.VendorId,
                    head.Address,
                    head.WardId,
                    head.WardName,
                    head.ZoneName,
                    head.SlotCode,
                    head.latitude,
                    head.longitude,
                    head.CommunityRating,
                    head.CommunityCount ?? 0,
                    menu.Length,
                    menu.Length == 0 ? null : menu.Min(item => item.unit_price),
                    menu.Select(item => item.category_name).Distinct().Order().ToArray(),
                    hours[head.storefront_id]
                        .Select(hour => new StorefrontHourRow(hour.day_of_week, hour.opens_at, hour.closes_at))
                        .ToArray());
            })
            .ToArray();
    }
}
