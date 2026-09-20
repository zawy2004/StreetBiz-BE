using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.Extensions.Options;
using Moq;
using System.Security.Cryptography;
using System.Text;
using StreetBiz.Application.Common.Exceptions;
using StreetBiz.Application.Common.Models;
using StreetBiz.Application.Common.Security;
using StreetBiz.Application.Features.Commerce;
using StreetBiz.Infrastructure.Persistence;
using StreetBiz.Infrastructure.Persistence.Repositories;
using StreetBiz.Infrastructure.Persistence.ScaffoldedModels;
using StreetBiz.Infrastructure.Payments;
using StreetBiz.Infrastructure.Services;

namespace StreetBiz.Infrastructure.Tests;

public sealed class CommerceManagementTests
{
    [Fact]
    public async Task Seller_cannot_manage_another_vendors_store()
    {
        using var f = await Fixture.Create();
        f.Vendor.Setup(x => x.RequireVendorIdAsync(default)).ReturnsAsync(99);
        Assert.Empty(await f.Service.Stores(default));
        await Assert.ThrowsAsync<NotFoundException>(() => f.Service.Menu(1, default));
        await Assert.ThrowsAsync<NotFoundException>(() => f.Service.ArchiveMenu(1, 1, default));
    }

    [Fact]
    public async Task Menu_changes_reach_marketplace_and_archiving_preserves_history()
    {
        using var f = await Fixture.Create();
        var added = await f.Service.SaveMenu(1, null, new(1, "Noodle", null, 30000, "AVAILABLE"), default);
        Assert.Equal("Noodle", (await f.Repository.GetMenuItemAsync(added.MenuItemId, default))!.ItemName);
        await f.Service.SaveMenu(1, added.MenuItemId, new(1, "Noodle updated", null, 35000, "SOLD_OUT"), default);
        Assert.Equal("SOLD_OUT", (await f.Repository.GetMenuItemAsync(added.MenuItemId, default))!.AvailabilityStatus);
        await f.Service.ArchiveMenu(1, added.MenuItemId, default);
        Assert.Null(await f.Repository.GetMenuItemAsync(added.MenuItemId, default));
        Assert.True(await f.Db.MenuItems.AnyAsync(x => x.menu_item_id == added.MenuItemId));
    }

    [Fact]
    public async Task Hidden_menu_cannot_be_restored_by_seller()
    {
        using var f = await Fixture.Create();
        (await f.Db.MenuItems.FindAsync(1L))!.availability_status = "HIDDEN";
        await f.Db.SaveChangesAsync();
        await Assert.ThrowsAsync<ConflictException>(() => f.Service.SaveMenu(1, 1, new(1, "Hidden", null, 10000, "AVAILABLE"), default));
    }

    [Fact]
    public async Task Cancelled_contract_cannot_open_a_store()
    {
        using var f = await Fixture.Create();
        (await f.Db.RentalContracts.FindAsync(1L))!.contract_status = "CANCELLED";
        await f.Db.SaveChangesAsync();
        await Assert.ThrowsAsync<DomainRuleException>(() => f.Service.SaveStore(1, new(1, 1, "Store", null, "OPEN"), default));
        Assert.Empty(await f.Repository.SearchMenuItemsAsync(null, 50, default));
        Assert.Equal(CartMutationOutcome.StorefrontUnavailable, (await f.Repository.AddCartItemAsync(2, 1, 1, null, default)).Outcome);
    }

    [Fact]
    public async Task Complaint_checks_ownership_refund_limit_and_duplicate_open_request()
    {
        using var f = await Fixture.Create();
        await Assert.ThrowsAsync<DomainRuleException>(() => f.Service.Complain(1, new("REFUND_REQUEST", "Too much", 25001), default));
        var created = await f.Service.Complain(1, new("REFUND_REQUEST", "Partial refund", 5000), default);
        Assert.Equal("OPEN", created.Status);
        await Assert.ThrowsAsync<ConflictException>(() => f.Service.Complain(1, new("COMPLAINT", "Duplicate", null), default));
        f.Customer.Setup(x => x.RequireCustomerUserIdAsync(default)).ReturnsAsync(99);
        await Assert.ThrowsAsync<NotFoundException>(() => f.Service.Complaints(1, default));
    }

    [Fact]
    public async Task Review_requires_completed_order_and_updates_existing_row()
    {
        using var f = await Fixture.Create();
        var created = await f.Service.SaveReview(1, new(5, "Good"), default);
        var edited = await f.Service.SaveReview(1, new(4, "Updated"), default);
        Assert.Equal(created.ReviewId, edited.ReviewId);
        Assert.Equal(4, edited.Rating);
        (await f.Db.Orders.FindAsync(1L))!.order_status = "PLACED";
        await f.Db.SaveChangesAsync();
        await Assert.ThrowsAsync<DomainRuleException>(() => f.Service.SaveReview(1, new(5, "Too early"), default));
    }

    [Fact]
    public async Task Sandbox_failure_cancels_order_and_cannot_be_resurrected()
    {
        using var f = await Fixture.Create();
        (await f.Db.Orders.FindAsync(1L))!.order_status = "PENDING_PAYMENT";
        var payment = (await f.Db.PaymentTransactions.FindAsync(1L))!;
        payment.transaction_status = "PENDING";
        payment.provider_reference = null;
        await f.Db.SaveChangesAsync();
        await f.Payments.Fail(1, default);
        Assert.Equal("FAILED", (await f.Db.PaymentTransactions.FindAsync(1L))!.transaction_status);
        Assert.Equal("CANCELLED", (await f.Db.Orders.FindAsync(1L))!.order_status);
        var paid = await f.Repository.ConfirmSandboxPaymentAsync(2, 1, default);
        Assert.Equal(OrderMutationOutcome.Conflict, paid.Outcome);
        Assert.Equal("CANCELLED", (await f.Db.Orders.FindAsync(1L))!.order_status);
        Assert.Single(await f.Db.OrderStatusHistories.ToListAsync());
        Assert.Single(await f.Db.PaymentTransactions.ToListAsync());
        Assert.Single(await f.Db.Notifications.ToListAsync());
    }

    [Fact]
    public async Task Sandbox_refund_is_idempotent_and_rejects_real_payment_reference()
    {
        using var f = await Fixture.Create();
        f.Db.RefundTransactions.Add(new RefundTransaction { order_id = 1, payment_transaction_id = 1,
            idempotency_key = "test-refund", amount = 5000, refund_reason = "ORDER_CANCELLED", provider = "MOMO", refund_status = "PENDING" });
        await f.Db.SaveChangesAsync();
        (await f.Db.PaymentTransactions.FindAsync(1L))!.provider_reference = "REAL-PROVIDER-1";
        await f.Db.SaveChangesAsync();
        await Assert.ThrowsAsync<ConflictException>(() => f.Payments.Refund(1, default));
        (await f.Db.PaymentTransactions.FindAsync(1L))!.provider_reference = "SANDBOX-MOMO-1";
        await f.Db.SaveChangesAsync();
        await f.Payments.Refund(1, default);
        var first = (await f.Db.RefundTransactions.SingleAsync()).completed_at;
        await f.Payments.Refund(1, default);
        var refund = await f.Db.RefundTransactions.SingleAsync();
        Assert.Equal("SUCCESS", refund.refund_status);
        Assert.Equal(first, refund.completed_at);
    }

    [Fact]
    public async Task Checkout_uses_database_price_and_keeps_cart_active()
    {
        using var f = await Fixture.Create();
        var cartId = await AddCart(f, quantity: 2);
        var cart = await f.Repository.GetActiveCartAsync(2, default);
        var result = await f.Repository.CheckoutAsync(2, cartId, "MOMO", "checkout-price", default);
        Assert.Equal("12 Nguyễn Văn Linh, Hải Châu, Đà Nẵng", cart!.StorefrontAddress);
        Assert.Equal(OrderMutationOutcome.Updated, result.Outcome);
        Assert.Equal(50000, result.Order!.TotalAmount);
        Assert.Equal("PENDING_PAYMENT", result.Order.OrderStatus);
        Assert.Equal("12 Nguyễn Văn Linh, Hải Châu, Đà Nẵng", result.Order.StorefrontAddress);
        Assert.Equal(
            "12 Nguyễn Văn Linh, Hải Châu, Đà Nẵng",
            (await f.Db.Orders.FindAsync(result.Order.OrderId))!.storefront_address_snapshot);
        Assert.Equal("ACTIVE", (await f.Db.ShoppingCarts.FindAsync(cartId))!.cart_status);

        var registration = await f.Db.BusinessRegistrations.FindAsync(1L);
        registration!.declared_address = "Địa chỉ đã thay đổi";
        await f.Db.SaveChangesAsync();
        f.Db.ChangeTracker.Clear();
        var reread = await f.Repository.GetCustomerOrderAsync(2, result.Order.OrderId, default);
        Assert.Equal("12 Nguyễn Văn Linh, Hải Châu, Đà Nẵng", reread!.StorefrontAddress);
    }

    [Fact]
    public async Task Checkout_is_idempotent_for_same_customer_and_key()
    {
        using var f = await Fixture.Create();
        var cartId = await AddCart(f);
        var first = await f.Repository.CheckoutAsync(2, cartId, "MOMO", "same-key", default);
        var second = await f.Repository.CheckoutAsync(2, cartId, "MOMO", "same-key", default);
        Assert.Equal(first.Order!.OrderId, second.Order!.OrderId);
        Assert.Equal(2, await f.Db.Orders.CountAsync());
        Assert.Equal(2, await f.Db.PaymentTransactions.CountAsync());
    }

    [Fact]
    public async Task Checkout_rejects_empty_cart()
    {
        using var f = await Fixture.Create();
        var cart = new ShoppingCart { customer_user_id = 2, storefront_id = 1, cart_status = "ACTIVE" };
        f.Db.ShoppingCarts.Add(cart);
        await f.Db.SaveChangesAsync();
        var result = await f.Repository.CheckoutAsync(2, cart.cart_id, "MOMO", "empty-cart", default);
        Assert.Equal(OrderMutationOutcome.EmptyCart, result.Outcome);
    }

    [Fact]
    public async Task Checkout_rejects_closed_storefront()
    {
        using var f = await Fixture.Create();
        var cartId = await AddCart(f);
        (await f.Db.Storefronts.FindAsync(1L))!.availability_status = "CLOSED";
        await f.Db.SaveChangesAsync();
        var result = await f.Repository.CheckoutAsync(2, cartId, "MOMO", "closed-store", default);
        Assert.Equal(OrderMutationOutcome.StorefrontUnavailable, result.Outcome);
    }

    [Fact]
    public async Task Checkout_rejects_unavailable_menu_item()
    {
        using var f = await Fixture.Create();
        var cartId = await AddCart(f);
        (await f.Db.MenuItems.FindAsync(1L))!.availability_status = "SOLD_OUT";
        await f.Db.SaveChangesAsync();
        var result = await f.Repository.CheckoutAsync(2, cartId, "MOMO", "sold-out", default);
        Assert.Equal(OrderMutationOutcome.MenuItemUnavailable, result.Outcome);
    }

    [Fact]
    public async Task Valid_success_callback_places_order_checks_out_cart_and_notifies_both_parties()
    {
        using var f = await Fixture.Create();
        var cartId = await AddCart(f);
        var checkout = await f.Repository.CheckoutAsync(2, cartId, "MOMO", "callback-ok", default);
        var callback = await ApplyCallback(f, "callback-ok", 25000, "SUCCESS", true);
        Assert.Equal(PaymentCallbackOutcome.Applied, callback.Outcome);
        Assert.Equal("PLACED", (await f.Db.Orders.FindAsync(checkout.Order!.OrderId))!.order_status);
        Assert.Equal("CHECKED_OUT", (await f.Db.ShoppingCarts.FindAsync(cartId))!.cart_status);
        Assert.Equal(2, await f.Db.Notifications.CountAsync());
        Assert.Null((await f.Db.OrderStatusHistories.OrderByDescending(x => x.history_id).FirstAsync()).changed_by);
    }

    [Fact]
    public async Task Duplicate_success_callback_does_not_duplicate_history_or_notifications()
    {
        using var f = await Fixture.Create();
        var cartId = await AddCart(f);
        var checkout = await f.Repository.CheckoutAsync(2, cartId, "MOMO", "callback-duplicate", default);
        await ApplyCallback(f, "callback-duplicate", 25000, "SUCCESS", true);
        var historyCount = await f.Db.OrderStatusHistories.CountAsync(x => x.order_id == checkout.Order!.OrderId);
        var notificationCount = await f.Db.Notifications.CountAsync();
        var duplicate = await ApplyCallback(f, "callback-duplicate", 25000, "SUCCESS", true);
        Assert.Equal(PaymentCallbackOutcome.Duplicate, duplicate.Outcome);
        Assert.Equal(historyCount, await f.Db.OrderStatusHistories.CountAsync(x => x.order_id == checkout.Order!.OrderId));
        Assert.Equal(notificationCount, await f.Db.Notifications.CountAsync());
        Assert.Equal(2, await f.Db.PaymentCallbackEvents.CountAsync());
    }

    [Fact]
    public async Task Invalid_signature_callback_is_recorded_and_rejected()
    {
        using var f = await Fixture.Create();
        var cartId = await AddCart(f);
        var checkout = await f.Repository.CheckoutAsync(2, cartId, "MOMO", "bad-signature", default);
        var callback = await ApplyCallback(f, "bad-signature", 25000, "SUCCESS", false);
        Assert.Equal(PaymentCallbackOutcome.Rejected, callback.Outcome);
        Assert.Equal("PENDING_PAYMENT", (await f.Db.Orders.FindAsync(checkout.Order!.OrderId))!.order_status);
        Assert.False((await f.Db.PaymentCallbackEvents.SingleAsync()).signature_valid);
    }

    [Fact]
    public async Task Wrong_amount_callback_is_recorded_and_rejected()
    {
        using var f = await Fixture.Create();
        var cartId = await AddCart(f);
        var checkout = await f.Repository.CheckoutAsync(2, cartId, "MOMO", "bad-amount", default);
        var callback = await ApplyCallback(f, "bad-amount", 1, "SUCCESS", true);
        Assert.Equal(PaymentCallbackOutcome.Rejected, callback.Outcome);
        Assert.Equal("PENDING_PAYMENT", (await f.Db.Orders.FindAsync(checkout.Order!.OrderId))!.order_status);
        Assert.Equal("REJECTED", (await f.Db.PaymentCallbackEvents.SingleAsync()).processing_result);
    }

    [Fact]
    public async Task Failed_callback_cancels_order_keeps_cart_active_and_does_not_notify_vendor()
    {
        using var f = await Fixture.Create();
        var cartId = await AddCart(f);
        var checkout = await f.Repository.CheckoutAsync(2, cartId, "MOMO", "callback-failed", default);
        var callback = await ApplyCallback(f, "callback-failed", 25000, "FAILED", true);
        Assert.Equal(PaymentCallbackOutcome.Applied, callback.Outcome);
        Assert.Equal("CANCELLED", (await f.Db.Orders.FindAsync(checkout.Order!.OrderId))!.order_status);
        Assert.Equal("ACTIVE", (await f.Db.ShoppingCarts.FindAsync(cartId))!.cart_status);
        var notification = await f.Db.Notifications.SingleAsync();
        Assert.Equal(2, notification.user_id);
    }

    [Fact]
    public async Task Customer_cannot_read_another_customers_order()
    {
        using var f = await Fixture.Create();
        Assert.Null(await f.Repository.GetCustomerOrderAsync(99, 1, default));
    }

    [Fact]
    public async Task Seller_cannot_transition_another_vendors_order()
    {
        using var f = await Fixture.Create();
        var result = await f.Repository.DecideSellerOrderAsync(
            99, 1, 1, "ACCEPT", null, "PLACED", default);
        Assert.Equal(OrderMutationOutcome.NotFound, result.Outcome);
    }

    [Fact]
    public async Task Invalid_seller_transition_is_rejected()
    {
        using var f = await Fixture.Create();
        f.Db.OrderStatusHistories.Add(new OrderStatusHistory
        {
            order_id = 1,
            from_status = "PENDING_PAYMENT",
            to_status = "PLACED",
            changed_at = DateTime.UtcNow,
        });
        await f.Db.SaveChangesAsync();
        var result = await f.Repository.UpdateSellerOrderStatusAsync(
            1, 1, 1, "READY_FOR_PICKUP", "PLACED", default);
        Assert.Equal(OrderMutationOutcome.Conflict, result.Outcome);
    }

    [Fact]
    public async Task Paid_customer_cancel_creates_one_pending_refund()
    {
        using var f = await Fixture.Create();
        var cartId = await AddCart(f);
        var checkout = await f.Repository.CheckoutAsync(2, cartId, "MOMO", "cancel-paid", default);
        await ApplyCallback(f, "cancel-paid", 25000, "SUCCESS", true);
        var cancelled = await f.Repository.CancelCustomerOrderAsync(
            2, checkout.Order!.OrderId, "PLACED", default);
        var repeated = await f.Repository.CancelCustomerOrderAsync(
            2, checkout.Order.OrderId, "PLACED", default);
        Assert.Equal(OrderMutationOutcome.Updated, cancelled.Outcome);
        Assert.Equal(OrderMutationOutcome.Conflict, repeated.Outcome);
        Assert.Single(await f.Db.RefundTransactions.Where(x => x.order_id == checkout.Order.OrderId).ToListAsync());
    }

    [Fact]
    public async Task Order_item_keeps_price_snapshot_after_menu_price_changes()
    {
        using var f = await Fixture.Create();
        var cartId = await AddCart(f);
        var checkout = await f.Repository.CheckoutAsync(2, cartId, "MOMO", "snapshot-price", default);
        (await f.Db.MenuItems.FindAsync(1L))!.unit_price = 99000;
        await f.Db.SaveChangesAsync();
        var tracked = await f.Repository.GetCustomerOrderAsync(2, checkout.Order!.OrderId, default);
        Assert.Equal(25000, tracked!.Items.Single().UnitPrice);
        Assert.Equal(25000, tracked.TotalAmount);
    }

    [Fact]
    public async Task Sales_summary_counts_only_completed_orders_in_period()
    {
        using var f = await Fixture.Create();
        var completed = (await f.Db.Orders.FindAsync(1L))!;
        completed.completed_at = DateTime.UtcNow;
        f.Db.Orders.Add(new Order { order_code = "NOT-COMPLETED", customer_user_id = 2,
            storefront_id = 1, order_status = "PLACED", subtotal_amount = 90000,
            total_amount = 90000, created_at = DateTime.UtcNow });
        await f.Db.SaveChangesAsync();
        var summary = await f.Repository.GetSalesSummaryAsync(1, "DAY", default);
        Assert.Equal(1, summary.CompletedOrderCount);
        Assert.Equal(25000, summary.GrossSales);
    }

    [Fact]
    public async Task Gateway_accepts_only_the_exact_hmac_signature()
    {
        const string raw = "{\"idempotencyKey\":\"key\",\"amount\":25000,\"status\":\"SUCCESS\"}";
        const string secret = "test-callback-secret";
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        var signature = Convert.ToHexString(hmac.ComputeHash(Encoding.UTF8.GetBytes(raw)));
        var gateway = new ConfiguredPaymentGateway(Options.Create(new PaymentGatewaySettings
        {
            Momo = new PaymentProviderSettings { CallbackSecret = secret }
        }));
        var valid = await gateway.VerifyCallbackAsync("MOMO", raw, signature, default);
        var invalid = await gateway.VerifyCallbackAsync("MOMO", raw, "00", default);
        Assert.True(valid.SignatureValid);
        Assert.False(invalid.SignatureValid);
    }

    private static async Task<long> AddCart(Fixture f, int quantity = 1)
    {
        var cart = new ShoppingCart
        {
            customer_user_id = 2,
            storefront_id = 1,
            cart_status = "ACTIVE",
            created_at = DateTime.UtcNow,
        };
        cart.ShoppingCartItems.Add(new ShoppingCartItem
        {
            menu_item_id = 1,
            quantity = quantity,
        });
        f.Db.ShoppingCarts.Add(cart);
        await f.Db.SaveChangesAsync();
        f.Db.ChangeTracker.Clear();
        return cart.cart_id;
    }

    private static Task<PaymentCallbackMutationResult> ApplyCallback(
        Fixture f,
        string idempotencyKey,
        decimal amount,
        string status,
        bool signatureValid) =>
        f.Repository.ApplyPaymentCallbackAsync(new PaymentCallbackData(
            "MOMO", null, idempotencyKey, amount, status,
            $"{{\"idempotencyKey\":\"{idempotencyKey}\"}}", signatureValid), default);

    private sealed class Fixture : IDisposable
    {
        private readonly SqliteConnection connection = new("Data Source=:memory:");
        public TestContext Db { get; private set; } = null!;
        public Mock<IVendorContext> Vendor { get; } = new();
        public Mock<ICustomerContext> Customer { get; } = new();
        public CommerceManagement Service { get; private set; } = null!;
        public OrderPaymentTesting Payments { get; private set; } = null!;
        public CommerceRepository Repository { get; private set; } = null!;
        public static async Task<Fixture> Create()
        {
            var f = new Fixture();
            await f.connection.OpenAsync();
            f.Db = new TestContext(new DbContextOptionsBuilder<StreetBizDbContext>().UseSqlite(f.connection).Options);
            await f.Db.Database.EnsureCreatedAsync();
            f.Vendor.Setup(x => x.RequireVendorIdAsync(default)).ReturnsAsync(1);
            f.Customer.Setup(x => x.RequireCustomerUserIdAsync(default)).ReturnsAsync(2);
            f.Service = new(f.Db, f.Vendor.Object, f.Customer.Object, TimeProvider.System);
            f.Payments = new(f.Db, f.Customer.Object, TimeProvider.System);
            f.Repository = new(f.Db, TimeProvider.System);
            f.Db.Roles.AddRange(new Role { role_code = "VENDOR", role_name = "Vendor" }, new Role { role_code = "CUSTOMER", role_name = "Customer" });
            f.Db.AdministrativeUnits.Add(new AdministrativeUnit { unit_id = 1, unit_type = "WARD", unit_name = "Ward" });
            foreach (var id in new long[] { 1, 2 }) f.Db.UserAccounts.Add(new UserAccount { user_id = id, phone_number = $"090000000{id}", password_hash = "test-only", full_name = "Tester", role_code = id == 1 ? "VENDOR" : "CUSTOMER", account_status = "ACTIVE" });
            f.Db.Vendors.Add(new Vendor { vendor_id = 1, user_id = 1 });
            f.Db.PricingZones.Add(new PricingZone { zone_id = 1, ward_unit_id = 1, zone_name = "Zone", created_by = 1 });
            f.Db.BusinessRegistrations.Add(new BusinessRegistration { registration_id = 1, vendor_id = 1, ward_unit_id = 1, vendor_type = "ITINERANT", display_name = "Vendor", declared_address = "12 Nguyễn Văn Linh, Hải Châu, Đà Nẵng", registration_status = "APPROVED" });
            f.Db.SidewalkSlots.Add(new SidewalkSlot { slot_id = 1, zone_id = 1, slot_code = "SLOT", source = "WARD_DEFINED", slot_status = "ACTIVE" });
            f.Db.RentalApplications.Add(new RentalApplication { application_id = 1, registration_id = 1, slot_id = 1, application_method = "MANUAL_SELECTED", application_status = "APPROVED" });
            f.Db.RentalContracts.Add(new RentalContract { contract_id = 1, application_id = 1, slot_id = 1, vendor_id = 1, start_date = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-1)), end_date = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(30)), contract_status = "ACTIVE" });
            f.Db.Storefronts.Add(new Storefront { storefront_id = 1, registration_id = 1, contract_id = 1, storefront_name = "Store", availability_status = "OPEN" });
            f.Db.FoodCategories.Add(new FoodCategory { category_id = 1, category_name = "Food" });
            f.Db.MenuItems.Add(new MenuItem { menu_item_id = 1, storefront_id = 1, category_id = 1, item_name = "Meal", unit_price = 25000, availability_status = "AVAILABLE" });
            f.Db.Orders.Add(new Order { order_id = 1, order_code = "ORDER-TEST", customer_user_id = 2, storefront_id = 1, order_status = "COMPLETED", total_amount = 25000, subtotal_amount = 25000 });
            f.Db.PaymentTransactions.Add(new PaymentTransaction { transaction_id = 1, order_id = 1, idempotency_key = "PAY-TEST", payment_purpose = "ORDER", provider = "MOMO", amount = 25000, transaction_status = "SUCCESS", provider_reference = "SANDBOX-MOMO-1" });
            await f.Db.SaveChangesAsync();
            f.Db.ChangeTracker.Clear();
            return f;
        }
        public void Dispose() { Db.Dispose(); connection.Dispose(); }
    }

    private sealed class TestContext(DbContextOptions<StreetBizDbContext> options) : StreetBizDbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);
            foreach (var entity in modelBuilder.Model.GetEntityTypes())
                foreach (var property in entity.GetProperties())
                {
                    if (property.GetComputedColumnSql() is not null) { property.SetComputedColumnSql(null); property.ValueGenerated = ValueGenerated.Never; }
                    property.SetDefaultValueSql(null);
                    if (property.ClrType == typeof(decimal) || property.ClrType == typeof(decimal?))
                        modelBuilder.Entity(entity.ClrType).Property(property.Name).HasConversion<double>();
                }
        }
    }
}
