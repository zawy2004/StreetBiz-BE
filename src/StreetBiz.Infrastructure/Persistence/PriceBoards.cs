using Microsoft.EntityFrameworkCore;
using StreetBiz.Application.Common.Models;
using StreetBiz.Application.Features.Commerce;
using StreetBiz.Infrastructure.Persistence.ScaffoldedModels;

namespace StreetBiz.Infrastructure.Persistence;

/// <summary>
/// BUY-06: a storefront's price board and its append-only history (MenuItemPriceHistory), shared by
/// the vendor's menu (CommerceManagement), moderation (PlatformAdministrationRepository) and the
/// public check (CommunityVendorRepository) so all three agree on what "on the board" means.
/// </summary>
internal static class PriceBoards
{
    /// <summary>
    /// Appends one row snapshotting <paramref name="item"/> as it now stands. The caller saves it in the
    /// same transaction as the change itself, so the history can never miss or invent a price.
    /// </summary>
    public static void Record(StreetBizDbContext db, MenuItem item, string changeKind, long actorUserId, DateTime nowUtc) =>
        db.MenuItemPriceHistories.Add(new MenuItemPriceHistory
        {
            menu_item = item,
            storefront_id = item.storefront_id,
            item_name = item.item_name,
            item_name_en = item.item_name_en,
            price_unit = string.IsNullOrWhiteSpace(item.price_unit) ? PriceBoardRules.DefaultPriceUnit : item.price_unit,
            unit_price = item.unit_price,
            is_listed = PriceBoardRules.IsListed(item.availability_status),
            change_kind = changeKind,
            changed_by = actorUserId,
            changed_at = nowUtc,
        });

    /// <summary>What a history row records of a dish: a change to any of these is a new board version.</summary>
    public static (string Name, string? NameEn, string Unit, decimal Price, bool Listed) Snapshot(MenuItem item) =>
        (item.item_name, item.item_name_en, item.price_unit, item.unit_price, PriceBoardRules.IsListed(item.availability_status));

    /// <summary>The board of the one storefront <paramref name="storefront"/> selects, read now; null if none.</summary>
    public static async Task<PriceBoardRow?> ReadAsync(
        StreetBizDbContext db,
        IQueryable<Storefront> storefront,
        CancellationToken cancellationToken)
    {
        var store = await storefront.AsNoTracking()
            .Select(s => new { s.storefront_id, s.storefront_name, s.contract_id, SlotCode = s.contract.slot.slot_code })
            .SingleOrDefaultAsync(cancellationToken);
        if (store is null)
        {
            return null;
        }

        var items = await db.MenuItems.AsNoTracking()
            .Where(item => item.storefront_id == store.storefront_id
                && (item.availability_status == "AVAILABLE" || item.availability_status == "SOLD_OUT"))
            .OrderBy(item => item.menu_item_id)
            .Select(item => new PriceBoardItemRow(
                item.menu_item_id, item.item_name, item.item_name_en, item.price_unit, item.unit_price))
            .ToListAsync(cancellationToken);
        var history = db.MenuItemPriceHistories.AsNoTracking().Where(row => row.storefront_id == store.storefront_id);
        var version = await history.CountAsync(cancellationToken);
        var updatedAt = await history.MaxAsync(row => (DateTime?)row.changed_at, cancellationToken);
        return new PriceBoardRow(
            store.storefront_id, store.storefront_name, store.SlotCode, store.contract_id, version, updatedAt, items);
    }
}
