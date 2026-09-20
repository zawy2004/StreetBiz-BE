using Microsoft.EntityFrameworkCore;
using StreetBiz.Application.Common.Models;
using StreetBiz.Application.Common.Security;
using StreetBiz.Infrastructure.Persistence;
using StreetBiz.Infrastructure.Persistence.Repositories;

namespace StreetBiz.Infrastructure.Tests;

public sealed class CommerceRepositoryDatabaseTests
{
    [Fact]
    public async Task Live_database_commerce_read_models_translate_when_connection_is_supplied()
    {
        var connection = Environment.GetEnvironmentVariable("STREETBIZ_DB_CONNECTION");
        if (string.IsNullOrWhiteSpace(connection))
        {
            // This is an opt-in, read-only smoke test. Unit/CI runs do not require SQL Server.
            return;
        }

        var options = new DbContextOptionsBuilder<StreetBizDbContext>()
            .UseSqlServer(connection)
            .Options;
        await using var db = new StreetBizDbContext(options);
        var repository = new CommerceRepository(db, TimeProvider.System);

        _ = await repository.SearchMenuItemsAsync(
            new MarketplaceMenuFilter(null, null, null, null, null, null, MarketplaceMenuSorts.Name),
            10,
            CancellationToken.None);
        foreach (var sort in new[] { MarketplaceMenuSorts.PriceAsc, MarketplaceMenuSorts.PriceDesc })
        {
            _ = await repository.SearchMenuItemsAsync(
                new MarketplaceMenuFilter("a", 1, 1, 0, 100_000, new MarketplaceOpenAt(1, new TimeOnly(10, 0)), sort),
                10,
                CancellationToken.None);
        }

        var openAt = new MarketplaceOpenAt(1, new TimeOnly(10, 0));
        var storefronts = await repository.ListStorefrontsAsync(
            new MarketplaceStorefrontFilter("a", 1, 1, openAt, null),
            10,
            CancellationToken.None);
        _ = await repository.ListStorefrontsAsync(
            new MarketplaceStorefrontFilter(null, null, null, null, null),
            10,
            CancellationToken.None);
        _ = await repository.ListStorefrontLocationsAsync(CancellationToken.None);
        _ = await repository.ListMarketplaceCategoriesAsync(CancellationToken.None);
        _ = await repository.GetStorefrontAsync(storefronts.FirstOrDefault()?.StorefrontId ?? 0, CancellationToken.None);


        var existingOrder = await db.Orders.AsNoTracking()
            .Select(order => new
            {
                order.order_id,
                order.customer_user_id,
                VendorId = order.storefront.registration.vendor_id,
                order.order_status,
            })
            .FirstOrDefaultAsync();
        if (existingOrder is null)
        {
            return;
        }

        var customerOrder = await repository.GetCustomerOrderAsync(
            existingOrder.customer_user_id,
            existingOrder.order_id,
            CancellationToken.None);
        Assert.NotNull(customerOrder);
        Assert.Equal(existingOrder.order_status, customerOrder.OrderStatus);

        var sellerVisibleOrder = await db.Orders.AsNoTracking()
            .Where(order => order.storefront.registration.vendor_id == existingOrder.VendorId
                && order.order_status != OrderStatuses.PendingPayment
                && order.OrderStatusHistories.Any(history =>
                    history.to_status == OrderStatuses.Placed))
            .Select(order => new { order.order_id })
            .FirstOrDefaultAsync();
        if (sellerVisibleOrder is not null)
        {
            var sellerOrder = await repository.GetSellerOrderAsync(
                existingOrder.VendorId,
                sellerVisibleOrder.order_id,
                CancellationToken.None);
            Assert.NotNull(sellerOrder);
        }

        var summary = await repository.GetSalesSummaryAsync(
            existingOrder.VendorId,
            SalesPeriods.Month,
            CancellationToken.None);
        Assert.Equal(SalesPeriods.Month, summary.Period);
        Assert.True(summary.FromUtc <= summary.ToUtc);
        Assert.Equal(summary.GrossSales - summary.RefundedAmount, summary.NetSales);
    }
}
