using Microsoft.EntityFrameworkCore;
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

        _ = await repository.SearchMenuItemsAsync(null, 10, CancellationToken.None);

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

        if (existingOrder.order_status != OrderStatuses.PendingPayment)
        {
            var sellerOrder = await repository.GetSellerOrderAsync(
                existingOrder.VendorId,
                existingOrder.order_id,
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
